using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace NxInfinity.Runtime.Services;

/// <summary>
/// Discovers known emulator save roots and imports native-verified directory
/// saves into targets without game progress. The complete save-migrations
/// directory is private user data; export Read() or operation reports only.
/// </summary>
public sealed partial class SaveMigrationStore
{
    private const int MaximumRecords = 4096;
    private const string Scope = "Only existing account/device directories resolved by the selected native adapter are eligible. Imports require an empty or metadata-only target with exact title, user and native format; Ryujinx-family source and destination indexes are verified on private copies. Existing game progress is preserved. No ongoing two-way sync, game save-schema conversion, target allocation, or retail save reopening is claimed.";
    private static readonly HashSet<string> PlainOrigins = new(StringComparer.Ordinal) { "eden", "citron", "yuzu", "suyu", "sudachi" };
    private static readonly HashSet<string> IndexedOrigins = new(StringComparer.Ordinal) { "ryujinx", "ryujinx-canary", "kenji-nx" };
    private static readonly string[] RecoveryMarkers = [".nx-unclean.json", ".nx-save-transaction.json", ".nx-firmware-transaction.json", ".nx-mod-transaction.json", ".nx-content-transaction.json"];
    private readonly NxWorkspace _workspace;
    private readonly string _root;
    private readonly string _statePath;

    private sealed record SourceKey(string Id, string Origin);
    private sealed record ReceiptRecord(SaveMigrationReceipt Receipt, SaveDirectoryMapping Mapping,
        string ProfileRelative, string TransactionId, string ImportedBackupId, string PreservedBackupId,
        string SourcePath, string SourceSnapshotFingerprint);
    private sealed record MigrationState(int SchemaVersion, Dictionary<string, SourceKey> SourceIds,
        List<ReceiptRecord> Receipts, List<SaveMigrationOutcome> LastOutcomes);
    private sealed record LocatedRoot(SaveMigrationLocation Location, string SaveRoot, bool Plain,
        SaveMigrationCandidate[] Candidates);
    private sealed record Discovery(SaveMigrationDiscoveryReport Report, LocatedRoot[] Roots);

    public SaveMigrationStore(NxWorkspace workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _root = Paths.EnsureDirectory(workspace.DataPath, "save-migrations");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _statePath = Paths.Under(_root, "state.json");
    }

    public SaveMigrationStatus Read()
    {
        using var lease = _workspace.Acquire("save-migration");
        var state = ReadState();
        return new(Array.AsReadOnly(state.Receipts.Select(x => x.Receipt with
            { AdapterVersion = SafeAdapter(x.Receipt.AdapterVersion), Status = x.Receipt.Status == "applying" ? "interrupted" : x.Receipt.Status,
                CanRollback = x.Receipt.Status == "imported" }).ToArray()),
            Array.AsReadOnly(state.LastOutcomes.Select(Sanitize).ToArray()));
    }

    public Task<SaveMigrationDiscoveryReport> DiscoverAsync(SaveMigrationDiscoveryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var lease = _workspace.Acquire("save-migration");
        var state = ReadState();
        var result = DiscoverCore(state, ValidateOptions(options), cancellationToken);
        WriteState(state);
        return Task.FromResult(result.Report);
    }

    private static SaveMigrationDiscoveryOptions ValidateOptions(SaveMigrationDiscoveryOptions? options)
    {
        options ??= new();
        if (options.AdditionalLocations is null || options.AdditionalLocations.Count > 16 ||
            options.StabilityWindow < TimeSpan.FromMilliseconds(50) || options.StabilityWindow > TimeSpan.FromSeconds(5))
            throw new ServiceException(ServiceError.InvalidInput, "Use at most 16 explicit save locations and a stability observation between 50 ms and five seconds.");
        return options with { AdditionalLocations = options.AdditionalLocations.ToArray() };
    }

    private Discovery DiscoverCore(MigrationState state, SaveMigrationDiscoveryOptions options, CancellationToken ct)
    {
        var roots = new List<LocatedRoot>();
        var candidates = new List<SaveMigrationCandidate>();
        var statuses = new List<SaveMigrationLocationStatus>();
        var locations = new List<SaveMigrationLocation>();
        if (options.IncludeKnownLocations) locations.AddRange(KnownLocations());
        locations.AddRange(options.AdditionalLocations);
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var location in locations)
        {
            ct.ThrowIfCancellationRequested();
            if (location is null || !PlainOrigins.Contains(location.Origin) && !IndexedOrigins.Contains(location.Origin))
                throw new ServiceException(ServiceError.InvalidInput, "The selected save origin has no supported discovery rule.");
            var path = Absolute(location.RootPath);
            if (!seen.Add(path)) continue;
            bool plain = PlainOrigins.Contains(location.Origin);
            var saveRoot = Path.Combine(path, plain ? "nand/user/save" : "bis/user/save");
            if (Overlap(path, _workspace.RootPath))
            { statuses.Add(new(location.Origin, "unsafe", 0, "An external source cannot overlap the NX workspace.")); continue; }
            if (!Directory.Exists(saveRoot))
            { statuses.Add(new(location.Origin, "not_found", 0, "No save root was found at this known location.")); continue; }
            try
            {
                Paths.RejectLinks(path); Paths.RejectLinks(saveRoot);
                if (SourceBusy(location))
                { statuses.Add(new(location.Origin, "deferred_busy", 0, "A source emulator or protected profile is active; discovery is deferred.")); continue; }
                var items = new List<SaveMigrationCandidate>();
                if (plain)
                {
                    string legacy = Path.Combine(saveRoot, "0000000000000000");
                    foreach (var user in Directories(legacy))
                    {
                        string uid = Path.GetFileName(user);
                        if (!Hex(uid, 32)) continue;
                        foreach (var title in Directories(user)) Add(title, uid.ToUpperInvariant(), "legacy", uid.All(c => c == '0') ? "device" : "account");
                    }
                    foreach (var user in Directories(Path.Combine(saveRoot, "account")))
                    {
                        string uid = Path.GetFileName(user);
                        if (!Hex(uid, 32)) continue;
                        // Common::UUID::RawString is the byte order of u128,
                        // while the protocol is high-word then low-word hex.
                        byte[] bytes = Convert.FromHexString(uid); Array.Reverse(bytes);
                        string protocolUser = Convert.ToHexString(bytes);
                        foreach (var title in Directories(user)) Add(title, protocolUser, "future", "account");
                    }
                    foreach (var title in Directories(Path.Combine(saveRoot, "device"))) Add(title, new string('0', 32), "future", "device");
                }
                else
                {
                    foreach (var directory in Directories(saveRoot))
                    {
                        if (!Hex(Path.GetFileName(directory), 16)) continue;
                        items.Add(new(SourceId(state, location.Origin, directory), location.Origin, "indexed_container",
                            null, null, null, "native_verification_required",
                            "The source save-data index will be verified on a private copy. Its numeric directory name does not identify a title or user."));
                    }
                }
                candidates.AddRange(items);
                if (candidates.Count > MaximumRecords) throw new ServiceException(ServiceError.InvalidInput, "The discovered save catalog exceeds its bounded entry limit.");
                roots.Add(new(location with { RootPath = path }, saveRoot, plain, items.ToArray()));
                statuses.Add(new(location.Origin, "available", items.Count,
                    plain ? "Directory candidates were found. Native source verification and an existing empty target are still required."
                        : "Indexed save containers were found. Native source and destination index verification is required before automatic import."));

                void Add(string titlePath, string user, string layout, string type)
                {
                    string title = Path.GetFileName(titlePath);
                    if (!Hex(title, 16) || title.All(c => c == '0')) return;
                    string directory = layout == "future" ? Path.Combine(titlePath, "0") : titlePath;
                    if (!Directory.Exists(directory)) return;
                    Paths.RejectLinks(directory);
                    items.Add(new(SourceId(state, location.Origin, directory), location.Origin,
                        "plain_directory_" + layout, title.ToUpperInvariant(), user, type,
                        "native_verification_required", "This is a directory-layout candidate. No native identity or import eligibility has been confirmed."));
                }
            }
            catch (ServiceException error) when (error.Code is ServiceError.UnsafePath or ServiceError.Busy or ServiceError.InvalidInput)
            { statuses.Add(new(location.Origin, error.Code == ServiceError.Busy ? "deferred_busy" : "unsafe", 0, "The source contains an unsafe, busy or oversized directory layout; it was not imported.")); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { statuses.Add(new(location.Origin, "unavailable", 0, "The known source directory could not be read; no import was attempted.")); }
        }
        return new(new(Array.AsReadOnly(candidates.ToArray()), Array.AsReadOnly(statuses.ToArray()), DateTimeOffset.UtcNow,
            "Known application-data save locations and explicitly supplied roots only. Directory names provide candidates; native source verification is required before automatic import."), roots.ToArray());
    }

    private IEnumerable<string> Directories(string root)
    {
        if (!Directory.Exists(root)) return [];
        Paths.RejectLinks(root);
        var entries = Directory.EnumerateFileSystemEntries(root).Take(MaximumRecords + 1).ToArray();
        if (entries.Length > MaximumRecords) throw new ServiceException(ServiceError.InvalidInput, "A save directory exceeds the bounded discovery limit.");
        foreach (var entry in entries) Paths.RejectLinks(entry);
        return entries.Where(Directory.Exists).Order(StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<SaveMigrationLocation> KnownLocations()
    {
        string applicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        string data = OperatingSystem.IsWindows() ? applicationData :
            xdgData is not null && Path.IsPathFullyQualified(xdgData) ? xdgData : Path.Combine(homeDirectory, ".local", "share");
        if (Path.IsPathFullyQualified(data))
            foreach (var origin in PlainOrigins) yield return new(origin, Path.Combine(data, origin));
        if (Path.IsPathFullyQualified(applicationData))
        {
            yield return new("ryujinx", Path.Combine(applicationData, "Ryujinx"));
        }
    }

    private static bool SourceBusy(SaveMigrationLocation location, bool checkLease = true)
    {
        foreach (var marker in RecoveryMarkers)
            if (File.Exists(Path.Combine(location.RootPath, marker))) return true;
        if (checkLease && File.Exists(Path.Combine(location.RootPath, ".nx-runtime.lock")))
        {
            try { using var probe = new FileStream(Path.Combine(location.RootPath, ".nx-runtime.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { return true; }
        }
        var names = location.Origin switch
        {
            "ryujinx" or "ryujinx-canary" => new[] { "Ryujinx", "ryujinx" },
            "kenji-nx" => ["KenjiNX", "kenji-nx"],
            _ => new[] { location.Origin, char.ToUpperInvariant(location.Origin[0]) + location.Origin[1..] }
        };
        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            var processes = Process.GetProcessesByName(name);
            try { if (processes.Any(process => process.Id != Environment.ProcessId && !process.HasExited)) return true; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        return false;
    }

    private static string Absolute(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ServiceException(ServiceError.UnsafePath, "An absolute local source root is required.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
    private static bool Hex(string value, int length) => value.Length == length && value.All(Uri.IsHexDigit);
    private static bool Overlap(string first, string second)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        first = Path.TrimEndingDirectorySeparator(first); second = Path.TrimEndingDirectorySeparator(second);
        return first.Equals(second, comparison) || first.StartsWith(second + Path.DirectorySeparatorChar, comparison) || second.StartsWith(first + Path.DirectorySeparatorChar, comparison);
    }
    private static string HashText(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string SourceId(MigrationState state, string origin, string path)
    {
        string canonical = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
        string key = HashText(origin + "\n" + canonical);
        if (!state.SourceIds.TryGetValue(key, out var source)) state.SourceIds[key] = source = new(Guid.NewGuid().ToString("N"), origin);
        return source.Id;
    }
    private MigrationState ReadState()
    {
        if (!File.Exists(_statePath)) return new(1, new(StringComparer.Ordinal), [], []);
        var state = StoreIo.ReadJson<MigrationState>(_statePath);
        if (state.SchemaVersion != 1 || state.SourceIds is null || state.Receipts is null || state.LastOutcomes is null ||
            state.SourceIds.Count > MaximumRecords || state.Receipts.Count > MaximumRecords || state.LastOutcomes.Count > 64)
            throw new ServiceException(ServiceError.CorruptData, "The migration ledger is invalid; it was not reset.");
        foreach (var (key, value) in state.SourceIds)
        { Ids.Hex(key, 64, "Private source identity"); Ids.GuidId(value.Id); ValidateOrigin(value.Origin); }
        foreach (var record in state.Receipts)
        {
            if (record?.Receipt is null || record.Mapping is null) throw new ServiceException(ServiceError.CorruptData, "A migration receipt is invalid.");
            var receipt = record.Receipt;
            Ids.GuidId(receipt.ReceiptId); Ids.Slug(receipt.BackendId); Ids.Slug(receipt.ProfileId);
            Ids.Hex(receipt.BackendRevision, 40, "Backend revision"); Ids.Hex(receipt.InstallationFingerprint, 64, "Installation fingerprint");
            Ids.Hex(receipt.TitleId, 16, "Title ID"); Ids.Hex(receipt.UserId, 32, "User ID");
            Ids.GuidId(receipt.SourceId); ValidateOrigin(receipt.SourceOrigin); Ids.Slug(receipt.FormatId); Ids.Slug(receipt.FormatVersion);
            if (receipt.Status is not ("imported" or "rolled_back" or "applying" or "interrupted") || receipt.AdapterVersion is null ||
                receipt.AdapterVersion.Length > 64 || receipt.AdapterVersion.Any(char.IsControl))
                throw new ServiceException(ServiceError.CorruptData, "A migration receipt status is invalid.");
            Paths.Relative(record.ProfileRelative); Ids.GuidId(record.TransactionId); Ids.GuidId(record.ImportedBackupId); Ids.GuidId(record.PreservedBackupId);
            Ids.Hex(record.SourceSnapshotFingerprint, 64, "Private source snapshot identity");
            if (record.ProfileRelative != "backends/" + receipt.BackendId + "/" + receipt.ProfileId ||
                record.Mapping.Identity != new SaveIdentity(receipt.TitleId, receipt.UserId, receipt.BackendId, receipt.FormatId, receipt.FormatVersion) ||
                record.Mapping.Verification != SaveMappingVerification.AdapterVerified)
                throw new ServiceException(ServiceError.CorruptData, "A migration receipt is bound to a different native save mapping.");
        }
        foreach (var outcome in state.LastOutcomes)
        {
            if (outcome is null) throw new ServiceException(ServiceError.CorruptData, "A migration observation is invalid.");
            Ids.Slug(outcome.BackendId); Ids.Slug(outcome.ProfileId); Ids.Hex(outcome.TitleId, 16, "Title ID"); Ids.Hex(outcome.UserId, 32, "User ID");
            if (outcome.ReceiptId is not null) Ids.GuidId(outcome.ReceiptId);
            if (outcome.Status is not ("imported" or "already_imported" or "rolled_back" or "rollback_conflict" or "target_conflict" or
                "source_conflict" or "target_allocation_unavailable" or "format_verification_unavailable" or "native_verification_unavailable" or
                "identity_unverified" or "source_unstable" or "unsafe_source" or "source_busy" or "deferred_busy" or "recovery_required" or
                "no_matching_source" or "cancelled" or "interrupted" or "failed"))
                throw new ServiceException(ServiceError.CorruptData, "A migration observation status is invalid.");
        }
        return state;
    }
    private static void ValidateOrigin(string origin)
    { if (!PlainOrigins.Contains(origin) && !IndexedOrigins.Contains(origin)) throw new ServiceException(ServiceError.CorruptData, "The recorded save origin is invalid."); }
    private void WriteState(MigrationState state)
    {
        if (state.SourceIds.Count > MaximumRecords || state.Receipts.Count > MaximumRecords)
            throw new ServiceException(ServiceError.Conflict, "The migration receipt catalog is full; existing records were preserved.");
        StoreIo.AtomicJson(_statePath, state);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_statePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
