namespace NxInfinity.Runtime.Services;

public sealed partial class SaveStore
{
    /// <summary>Imports only into a truly empty existing native-mapped directory, under the same lease as the transaction.</summary>
    public SaveImportReceipt ImportIntoEmptyFromInbox(SaveDirectoryMapping mapping, string incomingBundleRelativePath)
    {
        mapping = ValidateMapping(mapping);
        using var lease = StoreIo.AcquireLock(RuntimeLockPath);
        EnsureNoPending();
        var live = Paths.Under(BackendDataRoot, mapping.RelativeDirectory);
        if (!Directory.Exists(live))
            throw new ServiceException(ServiceError.NotFound, "Native target allocation is unavailable. An existing native-mapped empty save is required.");
        Paths.RejectLinks(live);
        if (Directory.EnumerateFileSystemEntries(live).Any())
            throw new ServiceException(ServiceError.Conflict, "The target save contains data. Automatic migration never overwrites it.");
        var incoming = _workspace.ResolveImportDirectory(incomingBundleRelativePath);
        var imported = ReadSnapshot(incoming);
        // Automatic migration keeps its checked source snapshot locally as well
        // as the pre-import empty baseline. Rollback can then compare live data
        // against immutable imported bytes under the same runtime lease.
        var retained = Paths.Under(_root, "backups/" + imported.Manifest.BackupId);
        if (Directory.Exists(retained) || File.Exists(retained))
            throw new ServiceException(ServiceError.Conflict, "That imported snapshot identity already exists in this save store.");
        var staging = Paths.Under(_root, "staging/migration-" + Guid.NewGuid().ToString("N"));
        try
        {
            var copied = StoreIo.CopyTree(Paths.Under(imported.DirectoryPath, "payload"), Paths.EnsureDirectory(staging, "payload"), _workspace.Limits);
            if (!SameFiles(copied.Files, imported.Manifest.Files) ||
                !copied.Directories.SequenceEqual(imported.Manifest.Directories.Order(StringComparer.Ordinal)))
                throw new ServiceException(ServiceError.CorruptData, "The migration source snapshot changed during retention.");
            StoreIo.AtomicJson(Paths.Under(staging, "manifest.json"), imported.Manifest);
            Directory.Move(staging, retained);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
        return ImportUnlocked(mapping, ReadLocalBackup(imported.Manifest.BackupId));
    }

    /// <summary>Restores the preserved baseline only while the imported bytes and transaction identity are unchanged.</summary>
    public SaveImportReceipt RollbackImportIfUnchanged(SaveDirectoryMapping mapping, string transactionId,
        string importedBackupId, string preservedBackupId)
    {
        mapping = ValidateMapping(mapping); transactionId = Ids.GuidId(transactionId);
        importedBackupId = Ids.GuidId(importedBackupId); preservedBackupId = Ids.GuidId(preservedBackupId);
        using var lease = StoreIo.AcquireLock(RuntimeLockPath);
        EnsureNoPending();
        VerifyIndexedRollbackGuard(mapping, importedBackupId);
        var state = ReadState(mapping);
        if (state?.TransactionId != transactionId || state.RollbackBackupId != preservedBackupId)
            throw new ServiceException(ServiceError.Conflict, "A later save transaction replaced this migration. Its data was preserved.");
        var imported = ReadLocalBackup(importedBackupId);
        if (imported.Manifest.Identity != mapping.Identity)
            throw new ServiceException(ServiceError.Conflict, "The imported snapshot belongs to another save identity.");
        var live = Paths.Under(BackendDataRoot, mapping.RelativeDirectory);
        try
        {
            StoreIo.VerifyTree(live, imported.Manifest.Files, _workspace.Limits);
            var directories = new List<string>(); Paths.EnumerateFiles(live, _workspace.Limits, directories);
            if (!directories.Order(StringComparer.Ordinal).SequenceEqual(imported.Manifest.Directories.Order(StringComparer.Ordinal)))
                throw new ServiceException(ServiceError.Conflict, "The save directory inventory changed after migration.");
        }
        catch (ServiceException error) when (error.Code is ServiceError.CorruptData or ServiceError.NotFound)
        { throw new ServiceException(ServiceError.Conflict, "The save changed after migration. Automatic rollback preserves the current data.", error); }
        return ImportUnlocked(mapping, ReadLocalBackup(preservedBackupId));
    }
}
