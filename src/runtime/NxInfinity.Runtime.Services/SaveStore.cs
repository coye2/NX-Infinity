using System.Security.Cryptography;
using System.Text;

namespace NxInfinity.Runtime.Services;

public enum SaveMappingVerification { Unverified, AdapterVerified }
public sealed record SaveIdentity(string TitleId, string UserId, string BackendId, string FormatId, string FormatVersion)
{
    public SaveIdentity Canonical() => new(Ids.Hex(TitleId, 16, "Title ID"), Ids.Hex(UserId, 32, "User ID"),
        Ids.Slug(BackendId), Ids.Slug(FormatId), Ids.Slug(FormatVersion));
}

/// <summary>Adapter attestation, not an inference from a filename. The adapter owns format verification.</summary>
public sealed record SaveDirectoryMapping(SaveIdentity Identity, string RelativeDirectory,
    SaveMappingVerification Verification = SaveMappingVerification.Unverified, string VerificationEvidence = "");
public sealed record SaveBackupManifest(int SchemaVersion, string BackupId, SaveIdentity Identity, DateTimeOffset CreatedUtc,
    string OriginalRelativeDirectory, string VerificationEvidence, IReadOnlyList<ContentFile> Files, IReadOnlyList<string> Directories);
public sealed record SaveBackup(SaveBackupManifest Manifest, string DirectoryPath);
public sealed record SaveImportReceipt(string TransactionId, string ImportedBackupId, string PreservedBackupId,
    string PreservedOriginalDirectory, string RestoredDirectory);
public sealed record SaveRecoveryResult(bool Recovered, string? TransactionId, string Detail);
internal sealed record SaveRestoreState(int SchemaVersion, SaveIdentity Identity, string RelativeDirectory,
    string RollbackBackupId, string TransactionId);
internal sealed record PendingSaveTransaction(int SchemaVersion, string TransactionId, SaveDirectoryMapping Mapping,
    string OriginalRelativePath, string StagingRelativePath, string PreservedBackupId, string ImportedBackupId,
    SaveRestoreState? PreviousState);

/// <summary>
/// Checked, stopped-only directory backups/imports. Real emulator formats require a verified core adapter mapping.
/// Workers must hold .nx-runtime.lock and reject startup while .nx-save-transaction.json exists.
/// </summary>
public sealed partial class SaveStore
{
    public const string RuntimeLockFileName = ".nx-runtime.lock";
    public const string PendingTransactionFileName = ".nx-save-transaction.json";
    public string BackendDataRoot { get; }
    public string RuntimeLockPath { get; }
    private readonly NxWorkspace _workspace;
    private readonly string _root;
    private readonly string _pending;

    public SaveStore(NxWorkspace workspace, string backendDataRootRelativePath)
    {
        _workspace = workspace;
        var backendRootComponent = Paths.Relative(backendDataRootRelativePath).Split('/')[0];
        if (backendRootComponent.Equals("imports", StringComparison.OrdinalIgnoreCase) || backendRootComponent.Equals("nx-services", StringComparison.OrdinalIgnoreCase) ||
            backendRootComponent.StartsWith(".nx-", StringComparison.OrdinalIgnoreCase))
            throw new ServiceException(ServiceError.UnsafePath, "Backend data roots cannot overlap incoming bundles or shared service metadata.");
        BackendDataRoot = Paths.EnsureDirectory(workspace.RootPath, backendDataRootRelativePath);
        RuntimeLockPath = Paths.Under(BackendDataRoot, RuntimeLockFileName);
        _pending = Paths.Under(BackendDataRoot, PendingTransactionFileName);
        _root = Paths.EnsureDirectory(BackendDataRoot, ".nx-saves");
        Paths.EnsureDirectory(_root, "backups");
        Paths.EnsureDirectory(_root, "staging");
        Paths.EnsureDirectory(_root, "states");
        Paths.EnsureDirectory(_root, "transactions");
    }

    public SaveBackup CreateBackup(SaveDirectoryMapping mapping)
    {
        mapping = ValidateMapping(mapping);
        using var lease = StoreIo.AcquireLock(RuntimeLockPath);
        EnsureNoPending();
        return BackupUnlocked(mapping);
    }

    public SaveBackup InspectBackup(string backupId)
    {
        using var lease = StoreIo.AcquireLock(RuntimeLockPath);
        EnsureNoPending();
        return ReadLocalBackup(backupId);
    }

    /// <summary>
    /// Lists checked snapshots in this actual backend data root. This is an explicit stopped-only verification
    /// operation: every returned snapshot has its complete payload/inventory revalidated, not just its label.
    /// </summary>
    public IReadOnlyList<SaveBackup> ListBackups(SaveIdentity? identity = null)
    {
        identity = identity?.Canonical();
        using var lease = StoreIo.AcquireLock(RuntimeLockPath);
        EnsureNoPending();
        var directory = Paths.Under(_root, "backups");
        var entries = Directory.EnumerateFileSystemEntries(directory).Take(_workspace.Limits.MaximumFiles + 1).ToArray();
        if (entries.Length > _workspace.Limits.MaximumFiles)
            throw new ServiceException(ServiceError.CorruptData, "The backup catalog exceeds its entry limit.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var backups = new List<SaveBackup>();
        foreach (var path in entries)
        {
            Paths.RejectLinks(path);
            var name = Path.GetFileName(path);
            if (!Directory.Exists(path) || !Guid.TryParseExact(name, "N", out var parsed) || parsed.ToString("N") != name || !ids.Add(name))
                throw new ServiceException(ServiceError.CorruptData, "The backup catalog contains an unexpected entry or a colliding identity.");
            var backup = ReadLocalBackup(name);
            if (identity is null || backup.Manifest.Identity == identity) backups.Add(backup);
        }
        return backups.OrderByDescending(b => b.Manifest.CreatedUtc).ThenBy(b => b.Manifest.BackupId, StringComparer.Ordinal).ToArray();
    }

    public SaveImportReceipt ImportFromInbox(SaveDirectoryMapping mapping, string incomingBundleRelativePath)
    {
        mapping = ValidateMapping(mapping);
        using var lease = StoreIo.AcquireLock(RuntimeLockPath);
        EnsureNoPending();
        var incoming = _workspace.ResolveImportDirectory(incomingBundleRelativePath);
        var backup = ReadSnapshot(incoming);
        return ImportUnlocked(mapping, backup);
    }

    public SaveImportReceipt RestoreBackup(SaveDirectoryMapping mapping, string backupId)
    {
        mapping = ValidateMapping(mapping);
        using var lease = StoreIo.AcquireLock(RuntimeLockPath);
        EnsureNoPending();
        return ImportUnlocked(mapping, ReadLocalBackup(backupId));
    }

    public SaveImportReceipt RollbackLastImport(SaveDirectoryMapping mapping)
    {
        mapping = ValidateMapping(mapping);
        using var lease = StoreIo.AcquireLock(RuntimeLockPath);
        EnsureNoPending();
        var state = ReadState(mapping) ?? throw new ServiceException(ServiceError.NotFound, "No previous import exists for this exact save mapping.");
        return ImportUnlocked(mapping, ReadLocalBackup(state.RollbackBackupId));
    }

    public SaveRecoveryResult RecoverPendingImport()
    {
        using var lease = StoreIo.AcquireLock(RuntimeLockPath);
        return RecoverUnlocked();
    }

    private SaveBackup BackupUnlocked(SaveDirectoryMapping mapping)
    {
        var source = Paths.Under(BackendDataRoot, mapping.RelativeDirectory);
        if (!Directory.Exists(source)) throw new ServiceException(ServiceError.NotFound, "The adapter-mapped save directory does not exist.");
        var id = Guid.NewGuid().ToString("N");
        var staging = Paths.EnsureDirectory(_root, "staging/backup-" + id);
        var tree = StoreIo.CopyTree(source, Paths.EnsureDirectory(staging, "payload"), _workspace.Limits);
        var manifest = new SaveBackupManifest(1, id, mapping.Identity, DateTimeOffset.UtcNow, mapping.RelativeDirectory,
            mapping.VerificationEvidence, tree.Files, tree.Directories);
        StoreIo.AtomicJson(Paths.Under(staging, "manifest.json"), manifest);
        var destination = Paths.Under(_root, "backups/" + id);
        Directory.Move(staging, destination);
        return new SaveBackup(manifest, destination);
    }

    private SaveImportReceipt ImportUnlocked(SaveDirectoryMapping mapping, SaveBackup imported)
    {
        if (imported.Manifest.Identity != mapping.Identity)
            throw new ServiceException(ServiceError.Conflict, "Save title, user, backend, format ID, and format version must all match exactly. No conversion was attempted.");
        var live = Paths.Under(BackendDataRoot, mapping.RelativeDirectory);
        if (!Directory.Exists(live))
            throw new ServiceException(ServiceError.NotFound, "Create the adapter-verified save directory before importing; unrecognized layouts are not synthesized.");
        var originalBackup = BackupUnlocked(mapping);
        var transactionId = Guid.NewGuid().ToString("N");
        var (stagingRelative, originalRelative) = TransactionPaths(mapping.RelativeDirectory, transactionId);
        var staged = Paths.Under(BackendDataRoot, stagingRelative);
        var original = Paths.Under(BackendDataRoot, originalRelative);
        if (Directory.Exists(staged) || File.Exists(staged) || Directory.Exists(original) || File.Exists(original))
            throw new ServiceException(ServiceError.Conflict, "A transaction-owned destination already exists.");
        var copied = StoreIo.CopyTree(Paths.Under(imported.DirectoryPath, "payload"), staged, _workspace.Limits);
        if (!SameFiles(copied.Files, imported.Manifest.Files) || !copied.Directories.SequenceEqual(imported.Manifest.Directories.Order(StringComparer.Ordinal)))
            throw new ServiceException(ServiceError.CorruptData, "The imported snapshot changed during copying.");
        var pending = new PendingSaveTransaction(1, transactionId, mapping, originalRelative, stagingRelative,
            originalBackup.Manifest.BackupId, imported.Manifest.BackupId, ReadState(mapping));
        StoreIo.AtomicJson(_pending, pending);
        try
        {
            // Both are siblings of live: same-filesystem renames with a recovery marker guarding the two-rename gap.
            Paths.RejectLinks(live);
            Directory.Move(live, original);
            Paths.RejectLinks(staged);
            Directory.Move(staged, live);
            StoreIo.AtomicJson(StatePath(mapping), new SaveRestoreState(1, mapping.Identity, mapping.RelativeDirectory,
                originalBackup.Manifest.BackupId, transactionId));
            ArchivePending(transactionId, "completed");
        }
        catch (Exception importError) when (importError is IOException or UnauthorizedAccessException or ServiceException)
        {
            try { RecoverUnlocked(); }
            catch (Exception recoveryError)
            {
                throw new ServiceException(ServiceError.RecoveryRequired,
                    "Import could not finish and recovery needs attention. All copies and the startup-blocking transaction marker were preserved.",
                    new AggregateException(importError, recoveryError));
            }
            throw new ServiceException(ServiceError.CorruptData, "Import did not complete; the original save was restored and all backup copies were retained.", importError);
        }
        return new SaveImportReceipt(transactionId, imported.Manifest.BackupId, originalBackup.Manifest.BackupId, original, live);
    }

    private SaveRecoveryResult RecoverUnlocked()
    {
        if (!File.Exists(_pending)) return new SaveRecoveryResult(false, null, "No pending save transaction.");
        var pending = StoreIo.ReadJson<PendingSaveTransaction>(_pending);
        if (pending.SchemaVersion != 1) throw new ServiceException(ServiceError.RecoveryRequired, "The pending save transaction schema is unknown.");
        var transactionId = Ids.GuidId(pending.TransactionId);
        var mapping = ValidateMapping(pending.Mapping);
        var (expectedStaging, expectedOriginal) = TransactionPaths(mapping.RelativeDirectory, transactionId);
        if (pending.StagingRelativePath != expectedStaging || pending.OriginalRelativePath != expectedOriginal)
            throw new ServiceException(ServiceError.RecoveryRequired, "Pending paths do not match the service-owned transaction identity.");
        var backup = ReadLocalBackup(pending.PreservedBackupId);
        if (backup.Manifest.Identity != mapping.Identity)
            throw new ServiceException(ServiceError.RecoveryRequired, "The preserved save identity does not match the pending transaction.");
        Ids.GuidId(pending.ImportedBackupId);
        var live = Paths.Under(BackendDataRoot, mapping.RelativeDirectory);
        var staged = Paths.Under(BackendDataRoot, pending.StagingRelativePath);
        var original = Paths.Under(BackendDataRoot, pending.OriginalRelativePath);
        if (Directory.Exists(original))
        {
            VerifyPayload(original, backup.Manifest);
            if (Directory.Exists(live))
            {
                if (Directory.Exists(staged) || File.Exists(staged))
                    throw new ServiceException(ServiceError.RecoveryRequired, "Unexpected save directories exist; recovery preserved every copy for inspection.");
                Directory.Move(live, staged);
            }
            Directory.Move(original, live);
        }
        else if (!Directory.Exists(live))
            throw new ServiceException(ServiceError.RecoveryRequired, "Both the live directory and the original transaction directory are missing; the immutable backup remains available.");
        VerifyPayload(live, backup.Manifest);
        if (pending.PreviousState is not null)
        {
            ValidateState(pending.PreviousState, mapping);
            StoreIo.AtomicJson(StatePath(mapping), pending.PreviousState);
        }
        else
        {
            var state = StatePath(mapping);
            Paths.RejectLinks(state);
            if (File.Exists(state)) File.Delete(state);
        }
        ArchivePending(transactionId, "recovered");
        return new SaveRecoveryResult(true, transactionId, "The exact original save was restored; immutable backups and the unused imported copy were retained.");
    }

    private void ArchivePending(string transactionId, string status)
    {
        Paths.RejectLinks(_pending);
        var archive = Paths.Under(_root, "transactions/" + status + "-" + Ids.GuidId(transactionId) + ".json");
        File.Move(_pending, archive, overwrite: false);
    }

    private SaveBackup ReadLocalBackup(string backupId)
    {
        backupId = Ids.GuidId(backupId);
        var backup = ReadSnapshot(Paths.Under(_root, "backups/" + backupId));
        if (backup.Manifest.BackupId != backupId)
            throw new ServiceException(ServiceError.CorruptData, "The save receipt identity does not match its storage directory.");
        return backup;
    }

    private SaveBackup ReadSnapshot(string directory)
    {
        Paths.RejectLinks(directory);
        var manifest = StoreIo.ReadJson<SaveBackupManifest>(Paths.Under(directory, "manifest.json"));
        if (manifest.SchemaVersion != 1 || manifest.Identity is null || manifest.Identity != manifest.Identity.Canonical())
            throw new ServiceException(ServiceError.CorruptData, "Save metadata has an invalid schema or identity.");
        Ids.GuidId(manifest.BackupId);
        Paths.Relative(manifest.OriginalRelativeDirectory);
        if (string.IsNullOrWhiteSpace(manifest.VerificationEvidence) || manifest.VerificationEvidence.Length > 2048)
            throw new ServiceException(ServiceError.CorruptData, "The save manifest is missing its adapter mapping evidence.");
        ValidateDirectories(manifest);
        var payload = Paths.Under(directory, "payload");
        VerifyPayload(payload, manifest);
        var rootDirectories = new List<string>();
        var rootFiles = Paths.EnumerateFiles(directory, _workspace.Limits with { MaximumFiles = _workspace.Limits.MaximumFiles + 1 }, rootDirectories);
        if (!rootFiles.ToHashSet(StringComparer.Ordinal).SetEquals(manifest.Files.Select(f => "payload/" + f.Path).Append("manifest.json")) ||
            !rootDirectories.ToHashSet(StringComparer.Ordinal).SetEquals(manifest.Directories.Select(d => "payload/" + d).Append("payload")))
            throw new ServiceException(ServiceError.CorruptData, "The save bundle contains unlisted files or directories.");
        return new SaveBackup(manifest, directory);
    }

    private void VerifyPayload(string directory, SaveBackupManifest manifest)
    {
        StoreIo.VerifyTree(directory, manifest.Files, _workspace.Limits);
        var directories = new List<string>();
        Paths.EnumerateFiles(directory, _workspace.Limits, directories);
        if (!directories.ToHashSet(StringComparer.Ordinal).SetEquals(manifest.Directories))
            throw new ServiceException(ServiceError.CorruptData, "Save directories differ from the backup manifest, including empty directories.");
    }

    private void ValidateDirectories(SaveBackupManifest manifest)
    {
        StoreIo.ValidateFiles(manifest.Files, _workspace.Limits);
        if (manifest.Directories is null || manifest.Directories.Count > _workspace.Limits.MaximumFiles + 64 ||
            manifest.Directories.Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Directories.Count)
            throw new ServiceException(ServiceError.CorruptData, "The save directory inventory is invalid.");
        var filePaths = manifest.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in manifest.Directories)
        {
            Paths.Relative(directory);
            if (filePaths.Contains(directory)) throw new ServiceException(ServiceError.CorruptData, "A save entry is both a file and a directory.");
        }
    }

    private SaveDirectoryMapping ValidateMapping(SaveDirectoryMapping mapping)
    {
        if (mapping is null || mapping.Identity is null)
            throw new ServiceException(ServiceError.InvalidInput, "A save mapping and identity are required.");
        if (mapping.Verification != SaveMappingVerification.AdapterVerified || string.IsNullOrWhiteSpace(mapping.VerificationEvidence) || mapping.VerificationEvidence.Length > 2048)
            throw new ServiceException(ServiceError.UnverifiedSaveMapping,
                "An adapter-verified save format and title/user directory mapping are required; raw directory names do not verify emulator save semantics.");
        var relative = Paths.Relative(mapping.RelativeDirectory);
        var first = relative.Split('/')[0];
        if (first.StartsWith(".nx-", StringComparison.OrdinalIgnoreCase) || first.Equals("nx-services", StringComparison.OrdinalIgnoreCase) || first.Equals("imports", StringComparison.OrdinalIgnoreCase))
            throw new ServiceException(ServiceError.UnsafePath, "A save mapping cannot target service metadata, imports, or transaction files.");
        Paths.Under(BackendDataRoot, relative);
        return mapping with { Identity = mapping.Identity.Canonical(), RelativeDirectory = relative };
    }

    private static (string Staging, string Original) TransactionPaths(string liveRelative, string id)
    {
        var split = liveRelative.LastIndexOf('/');
        var parent = split < 0 ? "" : liveRelative[..(split + 1)];
        var leaf = split < 0 ? liveRelative : liveRelative[(split + 1)..];
        return (Paths.Relative(parent + "." + leaf + ".nx-import-" + id), Paths.Relative(parent + "." + leaf + ".nx-original-" + id));
    }

    private string StatePath(SaveDirectoryMapping mapping)
    {
        var identity = mapping.Identity;
        var key = string.Join('\n', identity.TitleId, identity.UserId, identity.BackendId, identity.FormatId, identity.FormatVersion, mapping.RelativeDirectory);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return Paths.Under(_root, "states/" + hash + ".json");
    }

    private SaveRestoreState? ReadState(SaveDirectoryMapping mapping)
    {
        var path = StatePath(mapping);
        if (!File.Exists(path)) return null;
        var state = StoreIo.ReadJson<SaveRestoreState>(path);
        ValidateState(state, mapping);
        return state;
    }

    private static void ValidateState(SaveRestoreState state, SaveDirectoryMapping mapping)
    {
        if (state.SchemaVersion != 1 || state.Identity != mapping.Identity || state.RelativeDirectory != mapping.RelativeDirectory)
            throw new ServiceException(ServiceError.CorruptData, "Stored rollback state does not match this exact save mapping.");
        Ids.GuidId(state.RollbackBackupId);
        Ids.GuidId(state.TransactionId);
    }

    private static bool SameFiles(IReadOnlyList<ContentFile> a, IReadOnlyList<ContentFile> b) =>
        a.OrderBy(f => f.Path, StringComparer.Ordinal).Select(f => f with { Sha256 = f.Sha256.ToLowerInvariant() }).SequenceEqual(
            b.OrderBy(f => f.Path, StringComparer.Ordinal).Select(f => f with { Sha256 = f.Sha256.ToLowerInvariant() }));

    private void EnsureNoPending()
    {
        Paths.RejectLinks(_pending);
        if (File.Exists(_pending)) throw new ServiceException(ServiceError.RecoveryRequired, "RecoverPendingImport must restore the original save before another operation or a worker launch.");
    }
}
