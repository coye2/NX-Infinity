using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NxInfinity.Runtime.Services;

/// <summary>
/// Retains explicitly supplied local prerequisites once and provisions exact installed
/// cores through their public native import operation. It never starts emulation.
/// The complete shared-prerequisites directory is private user data and must not be
/// bundled or copied into diagnostics; export Read() or returned reports instead.
/// </summary>
public sealed partial class SharedPrerequisiteStore
{
    private readonly NxWorkspace _workspace;
    private readonly string _root;
    private readonly string _statePath;
    private const string RollbackNotice = "Rollback re-imports a previously retained shared source revision only. It does not restore unmanaged per-core setup or remove firmware/keys through guessed filesystem mappings.";
    private static readonly HashSet<string> KeySlots = new(StringComparer.Ordinal)
        { "prod.keys", "title.keys", "dev.keys", "console.keys" };
    private static readonly HashSet<string> SuccessStates = new(StringComparer.Ordinal) { "verified", "imported" };
    private const int MaximumRecords = 4096;

    public SharedPrerequisiteStore(NxWorkspace workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _root = Paths.EnsureDirectory(workspace.DataPath, "shared-prerequisites");
        PrivateDirectory(_root);
        Paths.EnsureDirectory(_root, "sources");
        _statePath = Paths.Under(_root, "state.json");
    }

    private sealed record SourceRecord(SharedPrerequisiteRevision Receipt, string PayloadEntry,
        IReadOnlyList<ContentFile> Files);
    private sealed record TargetRecord(SharedPrerequisiteOutcome Outcome, string? PreviousSourceRevisionId);
    private sealed record State(int SchemaVersion, Dictionary<string, string> ActiveSources,
        Dictionary<string, TargetRecord> Targets);

    public SharedPrerequisiteStatus Read()
    {
        using var lease = _workspace.Acquire("shared-prerequisites");
        var state = ReadState();
        return new(Array.AsReadOnly(SelectedSources(state).Select(x => x.Receipt).ToArray()),
            Array.AsReadOnly(state.Targets.Values.Select(x => PublicOutcome(x.Outcome))
                .OrderBy(x => x.BackendId, StringComparer.Ordinal).ThenBy(x => x.ProfileId, StringComparer.Ordinal)
                .ThenBy(x => x.Slot, StringComparer.Ordinal).ToArray()));
    }

    /// <summary>Retains immutable bytes privately; source validation belongs to each exact native importer.</summary>
    public async Task<SharedPrerequisiteRevision> StageAsync(string kind, string sourcePath,
        CancellationToken cancellationToken = default)
    {
        if (kind is not ("keys" or "firmware"))
            throw new ServiceException(ServiceError.InvalidInput, "Choose keys or firmware.");
        var source = Absolute(sourcePath);
        Paths.RejectLinks(source);
        bool directory = Directory.Exists(source);
        if (!directory && !File.Exists(source))
            throw new ServiceException(ServiceError.NotFound, "The selected local prerequisite does not exist.");
        string slot, entry, form;
        if (kind == "keys")
        {
            slot = Path.GetFileName(source).ToLowerInvariant();
            if (directory || !KeySlots.Contains(slot) || new FileInfo(source).Length is <= 0 or > 1048576)
                throw new ServiceException(ServiceError.InvalidInput, "Select a nonempty prod.keys, title.keys, dev.keys or console.keys file of at most 1 MiB. Each core validates its supported file types.");
            entry = slot; form = "file";
        }
        else
        {
            slot = "firmware";
            var extension = Path.GetExtension(source).ToLowerInvariant();
            if (!directory && extension is not (".zip" or ".xci"))
                throw new ServiceException(ServiceError.InvalidInput, "Select a local firmware directory, ZIP package or XCI update source. Each core must validate and support that source.");
            entry = directory ? "directory" : "firmware" + extension;
            form = directory ? "directory" : extension[1..];
        }
        cancellationToken.ThrowIfCancellationRequested();
        using var lease = _workspace.Acquire("shared-prerequisites");
        var state = ReadState();
        if (Directory.EnumerateDirectories(Paths.Under(_root, "sources")).Take(MaximumRecords + 1).Count() >= MaximumRecords)
            throw new ServiceException(ServiceError.Conflict, "The retained source revision limit has been reached; existing revisions were preserved.");
        var id = Guid.NewGuid().ToString("N");
        var staging = Paths.EnsureDirectory(_root, ".stage-" + id);
        PrivateDirectory(staging);
        var payload = Paths.EnsureDirectory(staging, "payload");
        var records = new List<ContentFile>();
        bool published = false;
        try
        {
            long total = 0;
            if (directory)
            {
                var files = Paths.EnumerateFiles(source, _workspace.Limits);
                if (files.Count == 0) throw new ServiceException(ServiceError.InvalidInput, "The selected firmware directory is empty.");
                foreach (var relative in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var destinationRelative = entry + "/" + relative;
                    var file = await CopyPrivateAsync(Paths.Under(source, relative), Paths.Under(payload, destinationRelative),
                        destinationRelative, _workspace.Limits.MaximumTotalBytes - total, cancellationToken);
                    total = checked(total + file.Size); records.Add(file);
                }
            }
            else records.Add(await CopyPrivateAsync(source, Paths.Under(payload, entry), entry,
                _workspace.Limits.MaximumTotalBytes, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            var previous = state.ActiveSources.GetValueOrDefault(slot);
            var receipt = new SharedPrerequisiteRevision(id, kind, slot, previous, DateTimeOffset.UtcNow, form);
            var record = new SourceRecord(receipt, entry, records);
            StoreIo.AtomicJson(Paths.Under(staging, "source.json"), record);
            PrivateFile(Paths.Under(staging, "source.json"));
            StoreIo.VerifyTree(payload, records, _workspace.Limits);
            var final = SourceDirectory(id);
            Directory.Move(staging, final);
            // This atomic selection is the commit point. Orphaned immutable sources after
            // interruption are never treated as selected and contain no executable content.
            state.ActiveSources[slot] = id;
            WriteState(state);
            published = true;
            return receipt;
        }
        finally
        {
            if (!published && Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private async Task<ContentFile> CopyPrivateAsync(string source, string destination, string relative,
        long remaining, CancellationToken ct)
    {
        Paths.RejectLinks(source); Paths.RejectLinks(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
        PrivateFile(destination);
        long total = 0;
        byte[] buffer = new byte[65536];
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            total = checked(total + count);
            if (total > remaining || total > _workspace.Limits.MaximumFileBytes)
                throw new ServiceException(ServiceError.InvalidInput, "The local prerequisite exceeds the configured private storage limit.");
            digest.AppendData(buffer.AsSpan(0, count));
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        await output.FlushAsync(ct); output.Flush(flushToDisk: true);
        if (total == 0) throw new ServiceException(ServiceError.InvalidInput, "An empty prerequisite file cannot be retained.");
        var hash = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
        input.Position = 0;
        var second = Convert.ToHexString(await SHA256.HashDataAsync(input, ct));
        if (!hash.Equals(second, StringComparison.OrdinalIgnoreCase))
            throw new ServiceException(ServiceError.Conflict, "The selected local file changed while it was being retained.");
        Paths.RejectLinks(source);
        return new(relative, hash, total);
    }

    private string SourceDirectory(string id) => Paths.Under(_root, "sources/" + Ids.GuidId(id));
    private SourceRecord ReadSource(string id)
    {
        var source = StoreIo.ReadJson<SourceRecord>(Paths.Under(SourceDirectory(id), "source.json"));
        if (source.Receipt is null || source.Receipt.Id != id || source.Receipt.Kind is not ("keys" or "firmware") ||
            (source.Receipt.Kind == "keys" ? !KeySlots.Contains(source.Receipt.Slot) : source.Receipt.Slot != "firmware") ||
            source.Receipt.SourceForm is not ("file" or "directory" or "zip" or "xci"))
            throw new ServiceException(ServiceError.CorruptData, "A private source revision is invalid; it was not reset.");
        if (source.Receipt.PreviousRevisionId is not null) Ids.GuidId(source.Receipt.PreviousRevisionId);
        Paths.Relative(source.PayloadEntry);
        StoreIo.ValidateFiles(source.Files, _workspace.Limits);
        if (source.Files.Count == 0 || (source.Receipt.Kind == "keys" &&
            (source.Files.Count != 1 || source.PayloadEntry != source.Receipt.Slot || source.Files[0].Path != source.Receipt.Slot || source.Files[0].Size is <= 0 or > 1048576)))
            throw new ServiceException(ServiceError.CorruptData, "A private source payload is invalid.");
        return source;
    }

    private SourceRecord[] SelectedSources(State state) => state.ActiveSources.OrderBy(x => x.Key == "firmware" ? 1 : 0)
        .ThenBy(x => x.Key == "prod.keys" ? 0 : 1).ThenBy(x => x.Key, StringComparer.Ordinal)
        .Select(x => { var source = ReadSource(x.Value); if (source.Receipt.Slot != x.Key) throw new ServiceException(ServiceError.CorruptData, "A source slot binding is invalid."); return source; }).ToArray();

    private State ReadState()
    {
        if (!File.Exists(_statePath)) return new(1, new(StringComparer.Ordinal), new(StringComparer.Ordinal));
        var state = StoreIo.ReadJson<State>(_statePath);
        if (state.SchemaVersion != 1 || state.ActiveSources is null || state.Targets is null || state.ActiveSources.Count > 5 || state.Targets.Count > MaximumRecords)
            throw new ServiceException(ServiceError.CorruptData, "The shared prerequisite ledger is invalid; no metadata was reset.");
        foreach (var (slot, id) in state.ActiveSources)
        {
            if (!KeySlots.Contains(slot) && slot != "firmware") throw new ServiceException(ServiceError.CorruptData, "A source slot is invalid.");
            Ids.GuidId(id);
        }
        foreach (var (key, record) in state.Targets)
        {
            Ids.Hex(key, 64, "Private target identity");
            if (record is null || record.Outcome is null) throw new ServiceException(ServiceError.CorruptData, "A provision observation is invalid.");
            var observation = record.Outcome;
            Ids.Slug(observation.BackendId); Ids.Slug(observation.ProfileId);
            Ids.Hex(observation.BackendRevision, 40, "Backend revision");
            Ids.Hex(observation.InstallationFingerprint, 64, "Installation fingerprint");
            Ids.GuidId(observation.SourceRevisionId);
            if (record.PreviousSourceRevisionId is not null) Ids.GuidId(record.PreviousSourceRevisionId);
            if (observation.IssueCodes is null || !AllowedStatuses.Contains(observation.Status) || observation.Kind is not ("keys" or "firmware") ||
                (observation.Kind == "keys" ? !KeySlots.Contains(observation.Slot) : observation.Slot != "firmware"))
                throw new ServiceException(ServiceError.CorruptData, "A provision status or source kind is invalid.");
        }
        return state;
    }

    private void WriteState(State state)
    {
        if (state.Targets.Count > MaximumRecords) throw new ServiceException(ServiceError.Conflict, "The private prerequisite observation limit was reached.");
        StoreIo.AtomicJson(_statePath, state); PrivateFile(_statePath);
    }

    private static string Absolute(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ServiceException(ServiceError.UnsafePath, "An absolute local path is required.");
        var result = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        Paths.RejectLinks(result); return result;
    }

    private static void PrivateDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    private static void PrivateFile(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    private static string HashText(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
