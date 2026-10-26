using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace NxInfinity.Runtime.Services;

internal sealed record LibraryIndex(int SchemaVersion, long Generation, IReadOnlyList<GameLibraryEntry> Entries);
internal sealed record LibraryIndexEnvelope(LibraryIndex Index, string Sha256);

/// <summary>Durable, private local-content metadata. Original content is read explicitly and never copied or deleted.</summary>
public sealed partial class GameLibraryStore
{
    private const int MaximumIndexBytes = 16 * 1024 * 1024;
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".nro", ".nso", ".nsp", ".xci", ".nca" };
    private static readonly StringComparer FilePathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly NxWorkspace _workspace;
    private readonly GameLibraryLimits _limits;
    private readonly string _root;
    private readonly string _indexPath;

    public GameLibraryStore(NxWorkspace workspace, GameLibraryLimits? limits = null)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _limits = limits ?? new();
        if (_limits.MaximumEntries is < 1 or > 100000 || _limits.MaximumDiscoveryEntries is < 1 or > 100000 ||
            _limits.MaximumContentBytes < 1 || _limits.MaximumArtworkBytes is < 64 or > 32 * 1024 * 1024 ||
            _limits.MaximumArtworkDimension is < 1 or > 16384 || _limits.MaximumLaunchesPerEntry is < 1 or > 1000 ||
            _limits.MaximumSettingsPerBackend is < 1 or > 4096)
            throw Invalid("Library limits are invalid.");
        _root = Paths.EnsureDirectory(workspace.DataPath, "library");
        Paths.EnsureDirectory(_root, "artwork");
        _indexPath = Paths.Under(_root, "index.json");
    }

    public IReadOnlyList<GameLibraryEntry> List(LibraryQuery? query = null)
    {
        query ??= new();
        if (query.Offset < 0 || query.Limit is < 1 or > 2000 || !Enum.IsDefined(query.Sort) ||
            (query.Kind is not null && !Enum.IsDefined(query.Kind.Value))) throw Invalid("The library query is invalid.");
        var search = OptionalText(query.Search, 512, "Search text");
        using var lease = Acquire();
        IEnumerable<GameLibraryEntry> entries = Read().Entries;
        if (query.FavoritesOnly) entries = entries.Where(e => e.Favorite);
        if (query.Kind is not null) entries = entries.Where(e => e.Kind == query.Kind);
        if (search is not null)
        {
            var terms = search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            entries = entries.Where(e => terms.All(term => SearchFields(e).Any(value => value.Contains(term, StringComparison.OrdinalIgnoreCase))));
        }
        entries = query.Sort switch
        {
            LibrarySort.RecentlyPlayed => entries.OrderByDescending(e => e.Launches.Count == 0 ? DateTimeOffset.MinValue : e.Launches.Max(l => l.StartedUtc))
                .ThenBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.EntryId, StringComparer.Ordinal),
            LibrarySort.RecentlyAdded => entries.OrderByDescending(e => e.AddedUtc).ThenBy(e => e.EntryId, StringComparer.Ordinal),
            _ => entries.OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.EntryId, StringComparer.Ordinal)
        };
        return entries.Skip(query.Offset).Take(query.Limit).ToArray();
    }

    public IReadOnlyList<GameLibraryEntry> Search(string text, int limit = 200) => List(new(Search: text, Limit: limit));
    public IReadOnlyList<GameLibraryEntry> Recent(int limit = 20)
    {
        if (limit is < 1 or > 2000) throw Invalid("Recent item limit is invalid.");
        return List(new(Sort: LibrarySort.RecentlyPlayed, Limit: 2000)).Where(e => e.Launches.Count != 0).Take(limit).ToArray();
    }

    public GameLibraryEntry Get(string entryId)
    {
        entryId = Ids.GuidId(entryId);
        using var lease = Acquire();
        return Required(Read(), entryId);
    }

    public async Task<GameLibraryEntry> RegisterAsync(ContentRegistrationRequest request,
        IProgress<ContentHashProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (request is null || !Enum.IsDefined(request.Kind)) throw Invalid("A content registration request is required.");
        var path = ContentPath(request.AbsolutePath);
        var name = RequiredText(request.DisplayName ?? Path.GetFileNameWithoutExtension(path), 256, "Display name");
        var relation = request.RelatedGameEntryId is null ? null : Ids.GuidId(request.RelatedGameEntryId);
        if (request.Kind == LibraryContentKind.Game && relation is not null) throw Invalid("A game cannot be associated with another game.");
        RegisteredContentFile? known;
        using (var lease = Acquire()) known = Read().Entries.FirstOrDefault(e => FilePathComparer.Equals(e.Content.AbsolutePath, path))?.Content;
        var content = known is null ? await HashContentAsync(path, progress, cancellationToken).ConfigureAwait(false)
            : await VerifyContentAsync(known, progress, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return RegisterHashed(request, path, name, relation, content);
    }

    private GameLibraryEntry RegisterHashed(ContentRegistrationRequest request, string path, string name,
        string? relation, RegisteredContentFile content, bool deduplicateBytes = false)
    {
        using var lease = Acquire();
        var index = Read();
        ValidateRelation(index, relation);
        var existing = index.Entries.FirstOrDefault(e => FilePathComparer.Equals(e.Content.AbsolutePath, path));
        if (existing is not null)
        {
            if (existing.Content.Sha256 != content.Sha256 || existing.Content.Size != content.Size)
                throw new ServiceException(ServiceError.Conflict, "This registered path now contains different bytes. The existing entry was preserved; explicitly unregister it before registering the replacement.");
            if (request.Kind != LibraryContentKind.Unidentified && existing.Kind != LibraryContentKind.Unidentified &&
                (existing.Kind != request.Kind || existing.RelatedGameEntryId != relation))
                throw new ServiceException(ServiceError.Conflict, "This file already has a different registration kind or game association.");
            // Timestamp-only changes are not new identities. Commit their verified
            // metadata so periodic scans do not keep rehashing the same file.
            if (existing.Content != content)
            {
                var refreshed = existing with { Content = content, UpdatedUtc = DateTimeOffset.UtcNow };
                Replace(index, refreshed);
                return refreshed;
            }
            return existing;
        }
        if (deduplicateBytes && index.Entries.FirstOrDefault(e => e.Content.Sha256 == content.Sha256 &&
            e.Content.Size == content.Size && (e.Kind == request.Kind || request.Kind == LibraryContentKind.Unidentified) && e.RelatedGameEntryId == relation) is { } duplicate)
        {
            // A moved game must no longer be pinned to a deleted path. Keep its
            // entry ID, favorite, artwork, native observations, and associations.
            // Never displace a still-existing original or guess identity by name.
            if (!File.Exists(duplicate.Content.AbsolutePath))
            {
                var moved = duplicate with { Content = content, UpdatedUtc = DateTimeOffset.UtcNow };
                Replace(index, moved);
                return moved;
            }
            return duplicate;
        }
        if (index.Entries.Count >= _limits.MaximumEntries) throw Invalid("The library entry limit was reached.");
        var now = DateTimeOffset.UtcNow;
        var entry = new GameLibraryEntry(1, Guid.NewGuid().ToString("N"), request.Kind, content, name, false, [], null, null,
            now, now, [], [], [], relation);
        Commit(index with { Entries = index.Entries.Append(entry).ToArray() });
        return entry;
    }

    private sealed record PendingRegistration(ContentRegistrationRequest Request, RegisteredContentFile Content, string Name, string? Relation);
    private sealed record RegistrationResult(PendingRegistration Pending, GameLibraryEntry? Entry, bool Added, string? Error);

    // Hashes are prepared outside the lock. Re-read under the cross-process lease
    // so a concurrent favorite/edit/unregister is never overwritten by a scan snapshot.
    private IReadOnlyList<RegistrationResult> RegisterBatch(IReadOnlyList<PendingRegistration> pending)
    {
        using var lease = Acquire();
        var index = Read();
        var entries = index.Entries.ToList();
        bool dirty = false;
        var result = new List<RegistrationResult>(pending.Count);
        foreach (var item in pending)
        {
            try
            {
                var current = index with { Entries = entries };
                ValidateRelation(current, item.Relation);
                if (item.Request.Kind == LibraryContentKind.Game && item.Relation is not null) throw Invalid("A game cannot be associated with another game.");
                var existing = entries.FirstOrDefault(e => FilePathComparer.Equals(e.Content.AbsolutePath, item.Content.AbsolutePath));
                if (existing is not null)
                {
                    if (existing.Content.Sha256 != item.Content.Sha256 || existing.Content.Size != item.Content.Size ||
                        item.Request.Kind != LibraryContentKind.Unidentified && existing.Kind != LibraryContentKind.Unidentified &&
                        (existing.Kind != item.Request.Kind || existing.RelatedGameEntryId != item.Relation))
                        throw new ServiceException(ServiceError.Conflict, "The registered file changed; existing identity and metadata were preserved.");
                    if (existing.Content != item.Content)
                    {
                        existing = existing with { Content = item.Content, UpdatedUtc = DateTimeOffset.UtcNow };
                        entries[entries.FindIndex(e => e.EntryId == existing.EntryId)] = existing;
                        dirty = true;
                    }
                    result.Add(new(item, existing, false, null)); continue;
                }
                var duplicate = entries.FirstOrDefault(e => e.Content.Sha256 == item.Content.Sha256 && e.Content.Size == item.Content.Size &&
                    (e.Kind == item.Request.Kind || item.Request.Kind == LibraryContentKind.Unidentified) && e.RelatedGameEntryId == item.Relation);
                if (duplicate is not null)
                {
                    if (!File.Exists(duplicate.Content.AbsolutePath))
                    {
                        duplicate = duplicate with { Content = item.Content, UpdatedUtc = DateTimeOffset.UtcNow };
                        entries[entries.FindIndex(e => e.EntryId == duplicate.EntryId)] = duplicate;
                        dirty = true;
                    }
                    result.Add(new(item, duplicate, false, null)); continue;
                }
                if (entries.Count >= _limits.MaximumEntries) throw Invalid("The library entry limit was reached.");
                var now = DateTimeOffset.UtcNow;
                var entry = new GameLibraryEntry(1, Guid.NewGuid().ToString("N"), item.Request.Kind, item.Content, item.Name, false, [], null, null,
                    now, now, [], [], [], item.Relation);
                entries.Add(entry); dirty = true; result.Add(new(item, entry, true, null));
            }
            catch (ServiceException e) { result.Add(new(item, null, false, ErrorCode(e))); }
        }
        if (dirty) Commit(index with { Entries = entries.ToArray() });
        return result;
    }

    public async Task<LibraryLaunchTarget> ResolveForLaunchAsync(string entryId,
        IProgress<ContentHashProgress>? progress = null, CancellationToken cancellationToken = default)
        => await ResolveContentAsync(entryId, true, progress, cancellationToken).ConfigureAwait(false);

    public Task<LibraryLaunchTarget> ResolveForInspectionAsync(string entryId, CancellationToken cancellationToken = default)
        => ResolveContentAsync(entryId, false, null, cancellationToken);

    private async Task<LibraryLaunchTarget> ResolveContentAsync(string entryId, bool requireGame,
        IProgress<ContentHashProgress>? progress, CancellationToken cancellationToken)
    {
        var original = Get(entryId);
        if (requireGame && original.Kind != LibraryContentKind.Game) throw Invalid("Only a game registration can be resolved for launch.");
        var verified = await VerifyContentAsync(original.Content, progress, cancellationToken).ConfigureAwait(false);
        if (verified.Sha256 != original.Content.Sha256 || verified.Size != original.Content.Size)
            throw new ServiceException(ServiceError.Conflict, "The registered content changed. Launch was refused and the prior metadata was preserved.");
        cancellationToken.ThrowIfCancellationRequested();
        // Re-read after the potentially long hash so a concurrent unregister cannot return a stale live entry.
        using var lease = Acquire();
        var current = Required(Read(), original.EntryId);
        if (current.Content != original.Content) throw new ServiceException(ServiceError.Conflict, "The content registration changed during verification.");
        if (requireGame && current.Kind != LibraryContentKind.Game) throw Invalid("The registration is no longer a game.");
        return new(current, current.Content.AbsolutePath, verified.Sha256, DateTimeOffset.UtcNow);
    }

    /// <summary>Caller must supply a native classification bound to these exact registered bytes.
    /// Does not infer content type from a title, filename, catalog match or artwork.</summary>
    public GameLibraryEntry SetInspectedKind(string entryId, string contentSha256, LibraryContentKind kind)
    {
        if (!Enum.IsDefined(kind)) throw Invalid("Invalid native content classification.");
        return Update(entryId, (index, entry) =>
        {
            if (entry.Content.Sha256 != HashValue(contentSha256)) throw new ServiceException(ServiceError.Conflict, "The inspected content differs from the registered file.");
            if (entry.Kind == kind) return entry;
            if (entry.Launches.Any(l => l.Outcome == LibraryLaunchOutcome.Started) || index.Entries.Any(e => e.RelatedGameEntryId == entryId))
                throw new ServiceException(ServiceError.Conflict, "Stop the game and review associated content before reclassifying it.");
            return entry with { Kind = kind, RelatedGameEntryId = kind == LibraryContentKind.Game ? null : entry.RelatedGameEntryId };
        });
    }

    public bool Remove(string entryId)
    {
        entryId = Ids.GuidId(entryId);
        using var lease = Acquire();
        var index = Read();
        if (!index.Entries.Any(e => e.EntryId == entryId)) return false;
        if (index.Entries.Any(e => e.RelatedGameEntryId == entryId))
            throw new ServiceException(ServiceError.Conflict, "Other registered content refers to this game; clear those associations before unregistering it.");
        if (Required(index, entryId).Launches.Any(l => l.Outcome == LibraryLaunchOutcome.Started))
            throw new ServiceException(ServiceError.Conflict, "Finish or explicitly mark the recorded launch interrupted before unregistering this entry.");
        Commit(index with { Entries = index.Entries.Where(e => e.EntryId != entryId).ToArray() });
        return true; // Original content and content-addressed artwork are deliberately retained.
    }

    public GameLibraryEntry SetFavorite(string entryId, bool favorite) => Update(entryId, (_, e) => e with { Favorite = favorite });

    public GameLibraryEntry SetMetadata(string entryId, string displayName, IReadOnlyList<string> tags, string? notes = null)
    {
        var name = RequiredText(displayName, 256, "Display name");
        var cleanTags = Tags(tags);
        var cleanNotes = OptionalText(notes, 4096, "Notes", multiline: true);
        return Update(entryId, (_, e) => e with { DisplayName = name, Tags = cleanTags, Notes = cleanNotes });
    }

    public GameLibraryEntry SetRelatedGame(string entryId, string? gameEntryId)
    {
        gameEntryId = gameEntryId is null ? null : Ids.GuidId(gameEntryId);
        return Update(entryId, (index, e) =>
        {
            if (e.Kind == LibraryContentKind.Game && gameEntryId is not null) throw Invalid("Only updates and downloadable content can be associated with a game.");
            ValidateRelation(index, gameEntryId);
            return e with { RelatedGameEntryId = gameEntryId };
        });
    }

    /// <summary>Commit one native inspection without three full index rewrites.
    /// The fresh locked entry preserves concurrent favorites, tags, notes, artwork and history.</summary>
    public GameLibraryEntry ApplyNativeInspection(string entryId, LibraryContentKind kind, LibraryCoreObservation observation)
    {
        if (!Enum.IsDefined(kind)) throw Invalid("Invalid native content classification.");
        observation = NormalizeObservation(observation) with { ObservedUtc = DateTimeOffset.UtcNow };
        return Update(entryId, (index, entry) =>
        {
            if (observation.ContentSha256 != entry.Content.Sha256)
                throw new ServiceException(ServiceError.Conflict, "The native inspection does not describe these registered bytes.");
            if (entry.Kind != kind && (entry.Launches.Any(l => l.Outcome == LibraryLaunchOutcome.Started) || index.Entries.Any(e => e.RelatedGameEntryId == entryId)))
                throw new ServiceException(ServiceError.Conflict, "Stop the game and review associated content before reclassifying it.");
            var observations = entry.Observations.Where(o => o.BackendId != observation.BackendId || o.BackendRevision != observation.BackendRevision).Append(observation).ToArray();
            if (observations.Length > 64) throw Invalid("The per-entry backend observation limit was reached.");
            return entry with { Kind = kind, RelatedGameEntryId = kind == LibraryContentKind.Game ? null : entry.RelatedGameEntryId,
                Observations = observations, DisplayName = observation.DisplayName ?? entry.DisplayName };
        });
    }

    public GameLibraryEntry AddCoreObservation(string entryId, LibraryCoreObservation observation)
    {
        if (observation is null) throw Invalid("An observation is required.");
        observation = NormalizeObservation(observation) with { ObservedUtc = DateTimeOffset.UtcNow };
        return Update(entryId, (_, e) =>
        {
            if (observation.ContentSha256 != e.Content.Sha256)
                throw new ServiceException(ServiceError.Conflict, "The adapter observation does not describe these registered bytes.");
            var observations = e.Observations.Where(o => o.BackendId != observation.BackendId || o.BackendRevision != observation.BackendRevision)
                .Append(observation).ToArray();
            if (observations.Length > 64) throw Invalid("The per-entry backend observation limit was reached.");
            return e with { Observations = observations };
        });
    }

    public LibraryLaunch MarkLaunch(string entryId, string backendId, string backendRevision)
    {
        backendId = Ids.Slug(backendId);
        backendRevision = Ids.Hex(backendRevision, 40, "Backend revision");
        var launch = new LibraryLaunch(Guid.NewGuid().ToString("N"), backendId, backendRevision, DateTimeOffset.UtcNow,
            null, LibraryLaunchOutcome.Started, null);
        Update(entryId, (_, e) =>
        {
            if (e.Kind != LibraryContentKind.Game) throw Invalid("Only a game can have a launch record.");
            var history = e.Launches.ToList();
            while (history.Count >= _limits.MaximumLaunchesPerEntry)
            {
                var completed = history.FindIndex(l => l.Outcome != LibraryLaunchOutcome.Started);
                if (completed < 0) throw new ServiceException(ServiceError.Busy, "The launch history is full of unfinished records; resolve them explicitly first.");
                history.RemoveAt(completed);
            }
            history.Add(launch);
            return e with { Launches = history.ToArray() };
        });
        return launch;
    }

    public LibraryLaunch CompleteLaunch(string entryId, string launchId, LibraryLaunchOutcome outcome, string? errorCode = null)
    {
        launchId = Ids.GuidId(launchId);
        if (!Enum.IsDefined(outcome) || outcome == LibraryLaunchOutcome.Started) throw Invalid("A terminal launch outcome is required.");
        if (errorCode is not null) errorCode = Ids.Slug(errorCode);
        if (outcome == LibraryLaunchOutcome.Completed && errorCode is not null) throw Invalid("A completed launch cannot have an error code.");
        LibraryLaunch? updated = null;
        Update(entryId, (_, e) =>
        {
            var launch = e.Launches.FirstOrDefault(l => l.LaunchId == launchId)
                ?? throw new ServiceException(ServiceError.NotFound, "The launch record does not exist.");
            if (launch.Outcome != LibraryLaunchOutcome.Started)
            {
                if (launch.Outcome != outcome || launch.ErrorCode != errorCode)
                    throw new ServiceException(ServiceError.Conflict, "A completed launch record cannot be relabeled.");
                updated = launch;
                return e;
            }
            updated = launch with { Outcome = outcome, ErrorCode = errorCode, EndedUtc = DateTimeOffset.UtcNow };
            return e with { Launches = e.Launches.Select(l => l.LaunchId == launchId ? updated : l).ToArray() };
        });
        return updated!;
    }

    public LibrarySettingRequestView GetSettingRequests(string entryId, string backendId, string backendRevision)
    {
        backendId = Ids.Slug(backendId);
        backendRevision = Ids.Hex(backendRevision, 40, "Backend revision");
        var stored = Get(entryId).SettingRequests.FirstOrDefault(s => s.BackendId == backendId);
        return new(stored, stored is not null && stored.BackendRevision != backendRevision);
    }

    public LibrarySettingRequests SetSettingRequests(string entryId, string backendId, string backendRevision,
        IReadOnlyDictionary<string, JsonElement> values)
    {
        var request = new LibrarySettingRequests(Ids.Slug(backendId), Ids.Hex(backendRevision, 40, "Backend revision"),
            Settings(values), DateTimeOffset.UtcNow);
        Update(entryId, (_, e) =>
        {
            if (e.Kind != LibraryContentKind.Game) throw Invalid("Only a game can have setting requests.");
            var requests = e.SettingRequests.Where(s => s.BackendId != request.BackendId).Append(request).ToArray();
            if (requests.Length > 32) throw Invalid("The per-entry backend settings limit was reached.");
            return e with { SettingRequests = requests };
        });
        return request;
    }

    public LibraryArtwork SetArtwork(string entryId, ReadOnlyMemory<byte> bytes)
    {
        entryId = Ids.GuidId(entryId);
        if (bytes.Length > _limits.MaximumArtworkBytes) throw Invalid("Artwork exceeds its byte limit.");
        var owned = bytes.ToArray();
        var artwork = LibraryImage.Validate(owned, _limits);
        using var lease = Acquire();
        var index = Read();
        var entry = Required(index, entryId);
        var path = ArtworkPath(artwork);
        if (File.Exists(path))
        {
            try
            {
                if (new FileInfo(path).Length != artwork.Bytes)
                    throw new ServiceException(ServiceError.CorruptData, "The existing content-addressed artwork has a different length.");
                VerifyArtwork(artwork, File.ReadAllBytes(path));
            }
            catch (ServiceException error) when (error.Code == ServiceError.CorruptData)
            {
                // The incoming bytes were independently validated and their hash
                // names this exact cache path. Preserve the damaged copy before
                // replacing it; never clear an entry or discard user-selected art.
                Paths.EnsureDirectory(_root, "artwork/quarantine");
                var damaged = Paths.Under(_root, "artwork/quarantine/" + artwork.Sha256 + "-" + Guid.NewGuid().ToString("N") + ".bin");
                Paths.RejectLinks(path);
                File.Move(path, damaged);
                try { WriteArtwork(path, owned); }
                catch
                {
                    if (!File.Exists(path)) { Paths.RejectLinks(path); File.Move(damaged, path); }
                    throw;
                }
            }
        }
        else WriteArtwork(path, owned);
        Replace(index, entry with { Artwork = artwork, UpdatedUtc = DateTimeOffset.UtcNow });
        return artwork;
    }

    public LibraryArtworkData? GetArtwork(string entryId)
    {
        entryId = Ids.GuidId(entryId);
        LibraryArtwork? artwork;
        using (var lease = Acquire()) artwork = Required(Read(), entryId).Artwork;
        if (artwork is null) return null;
        return ReadArtwork(artwork);
    }

    /// <summary>Reads a content-addressed snapshot without locking the mutable library index. Always validates bytes against the supplied hash and bounds.</summary>
    public LibraryArtworkData ReadArtwork(LibraryArtwork artwork)
    {
        ArgumentNullException.ThrowIfNull(artwork);
        if (artwork.MediaType is not ("image/png" or "image/jpeg") || artwork.Bytes is < 1 || artwork.Bytes > _limits.MaximumArtworkBytes ||
            artwork.Width is < 1 || artwork.Height is < 1 || artwork.Width > _limits.MaximumArtworkDimension ||
            artwork.Height > _limits.MaximumArtworkDimension || (long)artwork.Width * artwork.Height > 16777216 ||
            HashValue(artwork.Sha256) != artwork.Sha256)
            throw Invalid("Artwork snapshot metadata is invalid.");
        var path = ArtworkPath(artwork);
        if (!File.Exists(path) || new FileInfo(path).Length != artwork.Bytes)
            throw new ServiceException(ServiceError.CorruptData, "The stored artwork is missing or has a different length.");
        var bytes = File.ReadAllBytes(path);
        VerifyArtwork(artwork, bytes);
        return new(artwork, bytes);
    }

    public GameLibraryEntry ClearArtwork(string entryId) => Update(entryId, (_, e) => e with { Artwork = null });

    /// <summary>Returns extension candidates, not valid or compatible games. Never follows links or hashes file contents.</summary>
    public LibraryDiscoveryResult Discover(string absoluteDirectory, bool recursive = false, CancellationToken cancellationToken = default,
        bool skipRawNca = false)
    {
        var root = AbsolutePath(absoluteDirectory);
        Paths.RejectLinks(root);
        if (!Directory.Exists(root)) throw new ServiceException(ServiceError.NotFound, "The selected directory does not exist.");
        var candidates = new List<LocalContentCandidate>();
        var issues = new List<LibraryDiscoveryIssue>();
        var pending = new Stack<string>();
        pending.Push(root);
        // An ordinary game directory can contain thousands of unrelated files.
        // Only supported candidates consume the candidate limit; maintain a
        // separate bounded traversal limit so huge trees cannot hang a scan.
        long visited = 0;
        long maximumVisited = Math.Max(128L, (long)_limits.MaximumDiscoveryEntries * 32);
        var truncated = false;
        while (pending.Count != 0 && !truncated)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            try
            {
                Paths.RejectLinks(directory);
                foreach (var path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++visited > maximumVisited) { truncated = true; break; }
                    try
                    {
                        Paths.RejectLinks(path);
                        var attributes = File.GetAttributes(path);
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            if (recursive) pending.Push(path);
                        }
                        else if (Extensions.Contains(Path.GetExtension(path)) &&
                            !(skipRawNca && Path.GetExtension(path).Equals(".nca", StringComparison.OrdinalIgnoreCase)))
                        {
                            var info = ReadableFile(path);
                            if (candidates.Count >= _limits.MaximumDiscoveryEntries) { truncated = true; break; }
                            candidates.Add(new(path, info.Name, info.Extension.ToLowerInvariant(), info.Length, info.LastWriteTimeUtc));
                        }
                    }
                    catch (ServiceException ex) { issues.Add(new(Path.GetRelativePath(root, path), ex.Code.ToString())); }
                    catch (IOException) { issues.Add(new(Path.GetRelativePath(root, path), "unavailable")); }
                    catch (UnauthorizedAccessException) { issues.Add(new(Path.GetRelativePath(root, path), "access_denied")); }
                }
            }
            catch (IOException) { issues.Add(new(Path.GetRelativePath(root, directory), "unavailable")); }
            catch (UnauthorizedAccessException) { issues.Add(new(Path.GetRelativePath(root, directory), "access_denied")); }
        }
        return new(candidates.OrderBy(c => c.AbsolutePath, FilePathComparer).ToArray(), issues.ToArray(), truncated);
    }

    private FileStream Acquire()
    {
        // Artwork/metadata observers and imports share this short-lived index
        // lock. Brief contention must not turn a normal import into a failure.
        // Keep session/save ownership locks and sustained contention fail-closed.
        long started = Environment.TickCount64;
        while (true)
        {
            try { return _workspace.Acquire("library"); }
            catch (ServiceException e) when (e.Code == ServiceError.Busy && Environment.TickCount64 - started < 250)
            { Thread.Sleep(5); }
        }
    }
    private LibraryIndex Read()
    {
        if (!File.Exists(_indexPath)) return new(1, 0, []);
        var envelope = StoreIo.ReadJson<LibraryIndexEnvelope>(_indexPath, MaximumIndexBytes);
        try
        {
            if (envelope.Index is null || envelope.Sha256 is null || envelope.Sha256 != HashIndex(envelope.Index))
                throw Invalid("The library index hash is invalid.");
            ValidateIndex(envelope.Index);
            return envelope.Index;
        }
        catch (Exception ex) when (ex is ServiceException or ArgumentException or NullReferenceException or OverflowException)
        { throw new ServiceException(ServiceError.CorruptData, "The library index is invalid; it was preserved without reset.", ex); }
    }

    private void Commit(LibraryIndex index)
    {
        index = index with { Generation = checked(index.Generation + 1) };
        ValidateIndex(index);
        var envelope = new LibraryIndexEnvelope(index, HashIndex(index));
        if (JsonSerializer.SerializeToUtf8Bytes(envelope, StoreIo.Json).Length > MaximumIndexBytes)
            throw Invalid("The library index size limit was reached; previous metadata was preserved.");
        StoreIo.AtomicJson(_indexPath, envelope);
    }

    private static string HashIndex(LibraryIndex index) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(index, StoreIo.Json)));
    private static GameLibraryEntry Required(LibraryIndex index, string entryId) => index.Entries.FirstOrDefault(e => e.EntryId == entryId)
        ?? throw new ServiceException(ServiceError.NotFound, "The library entry does not exist.");
    private void Replace(LibraryIndex index, GameLibraryEntry entry) => Commit(index with
        { Entries = index.Entries.Select(e => e.EntryId == entry.EntryId ? entry : e).ToArray() });
    private GameLibraryEntry Update(string entryId, Func<LibraryIndex, GameLibraryEntry, GameLibraryEntry> change)
    {
        entryId = Ids.GuidId(entryId);
        using var lease = Acquire();
        var index = Read();
        var entry = change(index, Required(index, entryId)) with { UpdatedUtc = DateTimeOffset.UtcNow };
        Replace(index, entry);
        return entry;
    }

    private void ValidateIndex(LibraryIndex index)
    {
        if (index.SchemaVersion != 1 || index.Generation < 0 || index.Entries is null || index.Entries.Count > _limits.MaximumEntries)
            throw Invalid("The library index header is invalid.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(FilePathComparer);
        foreach (var entry in index.Entries)
        {
            if (entry is null || entry.SchemaVersion != 1 || !Enum.IsDefined(entry.Kind) || entry.Content is null ||
                !ids.Add(Ids.GuidId(entry.EntryId)) || !paths.Add(ContentPath(entry.Content.AbsolutePath)) ||
                entry.Content.AbsolutePath != ContentPath(entry.Content.AbsolutePath) ||
                entry.Content.Sha256 != HashValue(entry.Content.Sha256) || entry.Content.Size is < 1 || entry.Content.Size > _limits.MaximumContentBytes ||
                entry.AddedUtc == default || entry.UpdatedUtc < entry.AddedUtc || entry.Content.LastWriteUtc == default)
                throw Invalid("A library entry or content identity is invalid.");
            RequiredText(entry.DisplayName, 256, "Display name"); Tags(entry.Tags); OptionalText(entry.Notes, 4096, "Notes", multiline: true);
            if (entry.Observations is null || entry.Observations.Count > 64 || entry.Launches is null || entry.Launches.Count > _limits.MaximumLaunchesPerEntry ||
                entry.SettingRequests is null || entry.SettingRequests.Count > 32) throw Invalid("A library entry collection is invalid.");
            var observations = new HashSet<string>(StringComparer.Ordinal);
            foreach (var observation in entry.Observations)
            {
                var normalized = NormalizeObservation(observation);
                if (normalized != observation || observation.ObservedUtc is null || observation.ObservedUtc == default(DateTimeOffset) ||
                    observation.ContentSha256 != entry.Content.Sha256 || !observations.Add(observation.BackendId + ":" + observation.BackendRevision))
                    throw Invalid("An adapter observation is invalid or duplicated.");
            }
            var launches = new HashSet<string>(StringComparer.Ordinal);
            foreach (var launch in entry.Launches)
            {
                if (launch is null || !launches.Add(Ids.GuidId(launch.LaunchId)) ||
                    !Enum.IsDefined(launch.Outcome) || launch.BackendId != Ids.Slug(launch.BackendId) ||
                    launch.BackendRevision != Ids.Hex(launch.BackendRevision, 40, "Backend revision") || launch.StartedUtc == default ||
                    (launch.Outcome == LibraryLaunchOutcome.Started ? launch.EndedUtc is not null || launch.ErrorCode is not null : launch.EndedUtc is null || launch.EndedUtc < launch.StartedUtc) ||
                    (launch.Outcome == LibraryLaunchOutcome.Completed && launch.ErrorCode is not null)) throw Invalid("A launch record is invalid.");
                if (launch.ErrorCode is not null) Ids.Slug(launch.ErrorCode);
            }
            var settings = new HashSet<string>(StringComparer.Ordinal);
            foreach (var request in entry.SettingRequests)
            {
                if (request is null || !settings.Add(Ids.Slug(request.BackendId)) ||
                    request.BackendRevision != Ids.Hex(request.BackendRevision, 40, "Backend revision") || request.UpdatedUtc == default)
                    throw Invalid("A setting request is invalid or duplicated.");
                Settings(request.Values);
            }
            if (entry.Artwork is not null)
            {
                var art = entry.Artwork;
                if (art.Sha256 != HashValue(art.Sha256) || art.MediaType is not ("image/png" or "image/jpeg") ||
                    art.Bytes is < 1 || art.Bytes > _limits.MaximumArtworkBytes || art.Width is < 1 || art.Height is < 1 ||
                    art.Width > _limits.MaximumArtworkDimension || art.Height > _limits.MaximumArtworkDimension || (long)art.Width * art.Height > 16777216)
                    throw Invalid("Artwork metadata is invalid.");
            }
        }
        foreach (var entry in index.Entries)
        {
            if (entry.Kind == LibraryContentKind.Game && entry.RelatedGameEntryId is not null) throw Invalid("A game association is invalid.");
            ValidateRelation(index, entry.RelatedGameEntryId);
        }
    }

    private static void ValidateRelation(LibraryIndex index, string? relation)
    {
        if (relation is not null && Required(index, Ids.GuidId(relation)).Kind != LibraryContentKind.Game)
            throw Invalid("A content association must refer to a registered game. It does not establish native compatibility.");
    }

    private static string AbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || !Path.IsPathFullyQualified(path) || path.Any(char.IsControl) ||
            path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "." or "..") ||
            (OperatingSystem.IsWindows() && path.StartsWith("\\\\", StringComparison.Ordinal)))
            throw new ServiceException(ServiceError.UnsafePath, "Choose a fully qualified local path without parent traversal or device/UNC syntax.");
        try { return Path.GetFullPath(path); }
        catch (ArgumentException ex) { throw new ServiceException(ServiceError.UnsafePath, "The selected path is invalid.", ex); }
    }

    private static string ContentPath(string path)
    {
        path = AbsolutePath(path);
        if (!Extensions.Contains(Path.GetExtension(path))) throw Invalid("Select an NRO, NSO, NSP, XCI, or NCA file; the backend must still validate its contents.");
        return path;
    }

    private FileInfo ReadableFile(string path)
    {
        Paths.RejectLinks(path);
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        { throw new ServiceException(ServiceError.NotFound, "The selected content file no longer exists.", ex); }
        if ((attributes & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
            throw new ServiceException(ServiceError.UnsafePath, "Only regular content files are accepted.");
        var info = new FileInfo(path);
        if (info.Length < 1 || info.Length > _limits.MaximumContentBytes) throw Invalid("The content file is empty or exceeds the registration size limit.");
        return info;
    }

    private async Task<RegisteredContentFile> HashContentAsync(string path, IProgress<ContentHashProgress>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        path = ContentPath(path);
        try
        {
            var before = ReadableFile(path);
            var size = before.Length;
            var modified = before.LastWriteTimeUtc;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
                FileOptions.SequentialScan);
            if (stream.Length != size) throw new ServiceException(ServiceError.Conflict, "The content changed before hashing began.");
            var versionBefore = _fileVersion(stream);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(1024 * 1024);
            try
            {
            long read = 0;
            progress?.Report(new(0, size));
            int bytes;
            while ((bytes = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                read += bytes;
                if (read > size) throw new ServiceException(ServiceError.Conflict, "The content grew during hashing.");
                hash.AppendData(buffer, 0, bytes);
                progress?.Report(new(read, size));
            }
            cancellationToken.ThrowIfCancellationRequested();
            var after = ReadableFile(path);
            if (read != size || stream.Length != size || after.Length != size || after.LastWriteTimeUtc != modified)
                throw new ServiceException(ServiceError.Conflict, "The content changed during hashing; no registration was written.");
            var content = new RegisteredContentFile(path, Convert.ToHexStringLower(hash.GetHashAndReset()), size, modified);
            var versionAfter = _fileVersion(stream);
            if (versionBefore is not null && versionBefore == versionAfter) RememberVerifiedVersion(content, versionAfter);
            return content;
            }
            finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
        }
        catch (IOException ex) { throw new ServiceException(ServiceError.Unavailable, "The selected content could not be read exclusively from cooperating writers.", ex); }
        catch (UnauthorizedAccessException ex) { throw new ServiceException(ServiceError.Unavailable, "The selected content is not readable.", ex); }
    }

    private static LibraryCoreObservation NormalizeObservation(LibraryCoreObservation value)
    {
        if (value is null) throw Invalid("An adapter observation is missing.");
        var title = value.TitleId is null ? null : Ids.Hex(value.TitleId, 16, "Observed title ID");
        var build = value.BuildId is null ? null : Ids.Hex(value.BuildId, 64, "Observed full build ID");
        if (title?.All(c => c == '0') == true || build?.All(c => c == '0') == true)
            throw Invalid("Unknown all-zero title/build IDs must be represented by null, never verified identity.");
        return value with
        {
            BackendId = Ids.Slug(value.BackendId), BackendRevision = Ids.Hex(value.BackendRevision, 40, "Backend revision"),
            ContentSha256 = HashValue(value.ContentSha256), TitleId = title, BuildId = build,
            ContentKind = OptionalText(value.ContentKind, 32, "Native content kind"),
            DisplayName = OptionalText(value.DisplayName, 256, "Observed display name"),
            DisplayVersion = OptionalText(value.DisplayVersion, 64, "Observed display version"),
            Provenance = RequiredText(value.Provenance, 512, "Observation provenance")
        };
    }

    private IReadOnlyDictionary<string, JsonElement> Settings(IReadOnlyDictionary<string, JsonElement> values)
    {
        if (values is null || values.Count > _limits.MaximumSettingsPerBackend) throw Invalid("The setting request count is invalid.");
        var result = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in values)
        {
            Ids.Slug(pair.Key);
            if (!names.Add(pair.Key)) throw Invalid("Setting IDs cannot differ only by case.");
            switch (pair.Value.ValueKind)
            {
                case JsonValueKind.True: case JsonValueKind.False: break;
                case JsonValueKind.Number:
                    if (!pair.Value.TryGetDouble(out var number) || !double.IsFinite(number)) throw Invalid("Setting numbers must be finite.");
                    break;
                case JsonValueKind.String:
                    var text = pair.Value.GetString()!;
                    if (text.Length is < 1 or > 128 || text.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.' or ':' or '+' or ' ')))
                        throw Invalid("Setting strings must be short scalar values without paths or executable expressions.");
                    break;
                default: throw Invalid("Only Boolean, finite number, and short scalar string setting requests are supported.");
            }
            result.Add(pair.Key, pair.Value.Clone());
        }
        return result;
    }

    private static IEnumerable<string> SearchFields(GameLibraryEntry entry) => new[] { entry.DisplayName, Path.GetFileName(entry.Content.AbsolutePath), entry.Notes ?? "" }
        .Concat(entry.Tags).Concat(entry.Observations.SelectMany(o => new[] { o.TitleId ?? "", o.DisplayName ?? "", o.DisplayVersion ?? "" }));
    private static string[] Tags(IReadOnlyList<string> tags)
    {
        if (tags is null || tags.Count > 32) throw Invalid("At most 32 tags are accepted.");
        var result = tags.Select(t => RequiredText(t, 64, "Tag")).ToArray();
        if (result.Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Length) throw Invalid("Tags must be distinct.");
        return result.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static string RequiredText(string? text, int maximum, string name)
        => OptionalText(text, maximum, name) ?? throw Invalid(name + " is required.");
    private static string? OptionalText(string? text, int maximum, string name, bool multiline = false)
    {
        if (text is null) return null;
        text = text.Trim();
        if (text.Length == 0) return null;
        if (text.Length > maximum || text.Any(c => char.IsControl(c) && !(multiline && c is '\r' or '\n' or '\t')))
            throw Invalid(name + " is too long or contains control characters.");
        return text;
    }
    private static string HashValue(string value) => Ids.Hex(value, 64, "SHA-256").ToLowerInvariant();
    private static ServiceException Invalid(string message) => new(ServiceError.InvalidInput, message);

    private string ArtworkPath(LibraryArtwork art) => Paths.Under(_root, "artwork/" + art.Sha256 + (art.MediaType == "image/png" ? ".png" : ".jpg"));
    private void VerifyArtwork(LibraryArtwork art, byte[] bytes)
    {
        try { if (LibraryImage.Validate(bytes, _limits) != art) throw Invalid("Artwork metadata does not match its bytes."); }
        catch (ServiceException ex) { throw new ServiceException(ServiceError.CorruptData, "Stored artwork failed integrity validation.", ex); }
    }
    private static void WriteArtwork(string path, byte[] bytes)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, ".write-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            Paths.RejectLinks(path);
            File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
