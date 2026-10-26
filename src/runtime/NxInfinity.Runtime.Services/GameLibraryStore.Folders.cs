namespace NxInfinity.Runtime.Services;

public sealed record GameLibraryFolder(string Id, string AbsolutePath, bool Recursive = true, bool ScanAtStartup = true);
internal sealed record GameLibraryFolders(int SchemaVersion, IReadOnlyList<GameLibraryFolder> Folders);
public sealed record LibraryFolderProgress(string Stage, string? FileName, int Discovered, int Processed,
    int Added, int Unchanged, long BytesRead = 0, long TotalBytes = 0);
public sealed record LibraryFolderImportItem(string AbsolutePath, string? EntryId, string Outcome, string? Detail = null);
public sealed record LibraryFolderImportResult(IReadOnlyList<LibraryFolderImportItem> Items,
    IReadOnlyList<LibraryDiscoveryIssue> Issues, bool Truncated, bool Canceled)
{
    public int Added => Items.Count(i => i.Outcome == "added");
    public int Unchanged => Items.Count(i => i.Outcome is "unchanged" or "duplicate");
    public int Failed => Items.Count(i => i.Outcome == "failed");
}

public sealed partial class GameLibraryStore
{
    private string FoldersPath => Paths.Under(_root, "folders.json");

    public IReadOnlyList<GameLibraryFolder> Folders()
    {
        using var lease = Acquire();
        return ReadFolders().Folders.ToArray();
    }

    public GameLibraryFolder AddFolder(string absolutePath, bool recursive = true, bool scanAtStartup = true)
    {
        var path = Path.TrimEndingDirectorySeparator(AbsolutePath(absolutePath));
        Paths.RejectLinks(path);
        if (!Directory.Exists(path)) throw new ServiceException(ServiceError.NotFound, "The game folder is not available.");
        using var lease = Acquire();
        var state = ReadFolders();
        var existing = state.Folders.FirstOrDefault(f => FilePathComparer.Equals(f.AbsolutePath, path));
        var folder = new GameLibraryFolder(existing?.Id ?? Guid.NewGuid().ToString("N"), path, recursive, scanAtStartup);
        var folders = state.Folders.Where(f => f.Id != folder.Id).Append(folder).ToArray();
        if (folders.Length > 64) throw Invalid("At most 64 game folders can be watched; use a shared parent folder.");
        StoreIo.AtomicJson(FoldersPath, new GameLibraryFolders(1, folders));
        return folder;
    }

    public void SetFolderStartupScan(string id, bool enabled)
    {
        id = Ids.GuidId(id);
        using var lease = Acquire();
        var state = ReadFolders();
        if (!state.Folders.Any(f => f.Id == id)) throw new ServiceException(ServiceError.NotFound, "The game folder is no longer registered.");
        StoreIo.AtomicJson(FoldersPath, state with { Folders = state.Folders.Select(f => f.Id == id ? f with { ScanAtStartup = enabled } : f).ToArray() });
    }

    public void RemoveFolder(string id)
    {
        id = Ids.GuidId(id);
        using var lease = Acquire();
        var state = ReadFolders();
        StoreIo.AtomicJson(FoldersPath, state with { Folders = state.Folders.Where(f => f.Id != id).ToArray() });
        // Stopping discovery never unregisters games or deletes source files.
    }

    private GameLibraryFolders ReadFolders()
    {
        if (!File.Exists(FoldersPath)) return new(1, []);
        var state = StoreIo.ReadJson<GameLibraryFolders>(FoldersPath, 512 * 1024);
        if (state.SchemaVersion != 1 || state.Folders is null || state.Folders.Count > 64)
            throw new ServiceException(ServiceError.CorruptData, "The game-folder list is invalid and was preserved.");
        var ids = new HashSet<string>(); var paths = new HashSet<string>(FilePathComparer);
        foreach (var folder in state.Folders)
        {
            if (folder is null || !ids.Add(Ids.GuidId(folder.Id)) || !paths.Add(AbsolutePath(folder.AbsolutePath)))
                throw new ServiceException(ServiceError.CorruptData, "The game-folder list contains invalid or duplicate entries and was preserved.");
        }
        return state;
    }

    /// <summary>Reads selected roots on a background task. Completed imports survive cancellation; originals are never moved.</summary>
    public Task<LibraryFolderImportResult> ImportFoldersAsync(IEnumerable<GameLibraryFolder> folders,
        Func<LocalContentCandidate, CancellationToken, Task<ContentRegistrationRequest>>? classify = null,
        Func<GameLibraryEntry, CancellationToken, Task>? registered = null,
        IProgress<LibraryFolderProgress>? progress = null, CancellationToken cancellationToken = default,
        bool retryUnidentified = true)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var roots = folders.Take(65).ToArray();
        if (roots.Length > 64) throw Invalid("Too many game folders were selected.");
        return Task.Run(async () =>
        {
            var items = new List<LibraryFolderImportItem>(); var issues = new List<LibraryDiscoveryIssue>();
            var candidates = new Dictionary<string, LocalContentCandidate>(FilePathComparer);
            bool truncated = false, canceled = false;
            int processed = 0, added = 0, unchanged = 0;
            var pending = new List<PendingRegistration>(64);
            var batchClock = System.Diagnostics.Stopwatch.StartNew();
            void Flush()
            {
                if (pending.Count == 0) return;
                try
                {
                    foreach (var result in RegisterBatch(pending))
                    {
                        items.Add(new(result.Pending.Content.AbsolutePath, result.Entry?.EntryId,
                            result.Error is not null ? "failed" : result.Added ? "added" : "duplicate", result.Error));
                        if (result.Added) added++; else if (result.Error is null) unchanged++;
                    }
                }
                catch (Exception e) when (e is ServiceException or IOException or UnauthorizedAccessException)
                { foreach (var item in pending) items.Add(new(item.Content.AbsolutePath, null, "failed", ErrorCode(e))); }
                finally { pending.Clear(); batchClock.Restart(); }
            }
            try
            {
                foreach (var folder in roots)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(new("discovering", Path.GetFileName(folder.AbsolutePath), candidates.Count, 0, 0, 0));
                    try
                    {
                        var discovered = Discover(folder.AbsolutePath, folder.Recursive, cancellationToken, skipRawNca: true);
                        truncated |= discovered.Truncated;
                        issues.AddRange(discovered.Issues.Take(Math.Max(0, 1000 - issues.Count)));
                        foreach (var candidate in discovered.Candidates)
                        {
                            // Raw NCAs are also firmware/container parts. Folder discovery matches the ordinary game picker.
                            if (candidate.Extension == ".nca") continue;
                            if (candidates.Count >= _limits.MaximumDiscoveryEntries && !candidates.ContainsKey(candidate.AbsolutePath))
                            { truncated = true; break; }
                            candidates.TryAdd(candidate.AbsolutePath, candidate);
                        }
                    }
                    catch (Exception e) when (e is ServiceException or IOException or UnauthorizedAccessException)
                    { issues.Add(new(Path.GetFileName(folder.AbsolutePath), ErrorCode(e))); }
                }
                Dictionary<string, GameLibraryEntry> existing;
                using (var lease = Acquire()) existing = Read().Entries.ToDictionary(e => e.Content.AbsolutePath, FilePathComparer);
                foreach (var candidate in candidates.Values.OrderBy(c => c.AbsolutePath, FilePathComparer))
                {
                    if (batchClock.ElapsedMilliseconds >= 250) Flush();
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(new("importing", candidate.FileName, candidates.Count, processed, added, unchanged));
                    try
                    {
                        if (existing.TryGetValue(candidate.AbsolutePath, out var previous) && previous.Content.Size == candidate.Size &&
                            previous.Content.LastWriteUtc == candidate.LastWriteUtc &&
                            !(retryUnidentified && registered is not null && (previous.Kind == LibraryContentKind.Unidentified ||
                                previous.Kind == LibraryContentKind.Game && candidate.Extension is ".nsp" or ".xci" &&
                                !previous.Observations.Any(o => o.ContentSha256 == previous.Content.Sha256 && (o.ContentKind is not null || o.LoaderAccepted)))))
                        {
                            // Metadata-only discovery shortcut. Launch separately verifies the content version or full hash.
                            items.Add(new(candidate.AbsolutePath, previous.EntryId, "unchanged")); unchanged++; processed++; continue;
                        }
                        var request = classify is null ? new ContentRegistrationRequest(candidate.AbsolutePath) : await classify(candidate, cancellationToken).ConfigureAwait(false);
                        if (!FilePathComparer.Equals(ContentPath(request.AbsolutePath), candidate.AbsolutePath) || !Enum.IsDefined(request.Kind))
                            throw Invalid("The content classifier returned a different file or invalid kind.");
                        var hashProgress = new InlineFolderProgress<ContentHashProgress>(p => progress?.Report(new("hashing", candidate.FileName,
                            candidates.Count, processed, added, unchanged, p.BytesRead, p.TotalBytes)));
                        var content = await HashContentAsync(candidate.AbsolutePath, hashProgress, cancellationToken).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (registered is null)
                        {
                            pending.Add(new(request, content,
                                RequiredText(request.DisplayName ?? Path.GetFileNameWithoutExtension(candidate.AbsolutePath), 256, "Display name"),
                                request.RelatedGameEntryId is null ? null : Ids.GuidId(request.RelatedGameEntryId)));
                            processed++;
                            if (pending.Count >= 64) Flush();
                            continue;
                        }
                        var entry = RegisterHashed(request, candidate.AbsolutePath,
                            RequiredText(request.DisplayName ?? Path.GetFileNameWithoutExtension(candidate.AbsolutePath), 256, "Display name"),
                            request.RelatedGameEntryId is null ? null : Ids.GuidId(request.RelatedGameEntryId), content, deduplicateBytes: true);
                        bool duplicate = !FilePathComparer.Equals(entry.Content.AbsolutePath, candidate.AbsolutePath) || existing.ContainsKey(candidate.AbsolutePath);
                        existing[entry.Content.AbsolutePath] = entry;
                        items.Add(new(candidate.AbsolutePath, entry.EntryId, duplicate ? "duplicate" : "added"));
                        if (duplicate) unchanged++; else added++;
                        // Reinspect a verified entry whenever its file metadata
                        // changed or its old path was recovered. "duplicate" only
                        // means the identity was preserved, not that inspection is
                        // unnecessary. Unchanged files used the fast path above.
                        if (registered is not null)
                        {
                            try { await registered(entry, cancellationToken).ConfigureAwait(false); }
                            catch (Exception e) when (e is ServiceException or NxInfinity.Runtime.NxException or IOException or UnauthorizedAccessException)
                            { issues.Add(new(candidate.FileName, "inspection_" + ErrorCode(e))); }
                        }
                    }
                    catch (Exception e) when (e is ServiceException or NxInfinity.Runtime.NxException or IOException or UnauthorizedAccessException)
                    { items.Add(new(candidate.AbsolutePath, null, "failed", ErrorCode(e))); }
                    processed++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { canceled = true; }
            finally { Flush(); } // Completed hashes are committed atomically, including on cancellation.
            progress?.Report(new(canceled ? "canceled" : "complete", null, candidates.Count, processed, added, unchanged));
            return new LibraryFolderImportResult(items.ToArray(), issues.Take(1000).ToArray(), truncated, canceled);
        });
    }

    private static string ErrorCode(Exception e) => e switch
    {
        ServiceException service => service.Code.ToString(), NxInfinity.Runtime.NxException nx => nx.Code,
        UnauthorizedAccessException => "access_denied", _ => "unavailable"
    };
    private sealed class InlineFolderProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
}

