namespace NxInfinity.Runtime.Services;

public sealed partial class SaveStore
{
    /// <summary>Restores an explicitly reviewed snapshot only while the target still equals its reviewed backup.</summary>
    internal SaveImportReceipt ImportReviewedIndexedFromInbox(SaveDirectoryMapping mapping, string incomingBundleRelativePath,
        string reviewedBackupId, string indexRelative, IReadOnlyList<ContentFile> indexFiles, IReadOnlyList<string> indexDirectories)
    {
        mapping = ValidateMapping(mapping); reviewedBackupId = Ids.GuidId(reviewedBackupId);
        using var lease = StoreIo.AcquireLock(RuntimeLockPath);
        EnsureNoPending();
        foreach (var name in new[] { ".nx-unclean.json", ".nx-firmware-transaction.json", ".nx-mod-transaction.json", ".nx-content-transaction.json" })
            if (File.Exists(Paths.Under(BackendDataRoot, name)) || File.Exists(Paths.Under(BackendDataRoot, name + ".new")))
                throw new ServiceException(ServiceError.RecoveryRequired, "This profile requires recovery before any save import.");
        if (indexRelative != "bis/system/save" || mapping.Identity.BackendId is not ("nxi") ||
            mapping.Identity.FormatId != "ryujinx-directory-save" || mapping.Identity.FormatVersion != "1")
            throw new ServiceException(ServiceError.UnverifiedSaveMapping, "Only the reviewed indexed save format is supported.");
        try
        {
            var index = Paths.Under(BackendDataRoot, indexRelative);
            StoreIo.VerifyTree(index, indexFiles, _workspace.Limits);
            var current = new List<string>(); Paths.EnumerateFiles(index, _workspace.Limits, current);
            if (!current.Order(StringComparer.Ordinal).SequenceEqual(indexDirectories.Order(StringComparer.Ordinal)))
                throw new ServiceException(ServiceError.Conflict, "The native save index changed after review.");
        }
        catch (ServiceException error) when (error.Code is ServiceError.CorruptData or ServiceError.NotFound)
        { throw new ServiceException(ServiceError.Conflict, "The native save index changed; review again before importing.", error); }
        var baseline = ReadLocalBackup(reviewedBackupId);
        if (baseline.Manifest.Identity != mapping.Identity || baseline.Manifest.OriginalRelativeDirectory != mapping.RelativeDirectory)
            throw new ServiceException(ServiceError.Conflict, "The reviewed backup belongs to another native save.");
        var live = Paths.Under(BackendDataRoot, mapping.RelativeDirectory);
        try
        {
            StoreIo.VerifyTree(live, baseline.Manifest.Files, _workspace.Limits);
            var directories = new List<string>(); Paths.EnumerateFiles(live, _workspace.Limits, directories);
            if (!directories.Order(StringComparer.Ordinal).SequenceEqual(baseline.Manifest.Directories.Order(StringComparer.Ordinal)))
                throw new ServiceException(ServiceError.Conflict, "The save directories changed after review.");
        }
        catch (ServiceException error) when (error.Code is ServiceError.CorruptData or ServiceError.NotFound)
        { throw new ServiceException(ServiceError.Conflict, "The save changed after review; newer progress was preserved.", error); }
        var imported = ReadSnapshot(_workspace.ResolveImportDirectory(incomingBundleRelativePath));
        if (imported.Manifest.Identity != mapping.Identity)
            throw new ServiceException(ServiceError.Conflict, "The reviewed source belongs to another save identity.");
        var retained = Paths.Under(_root, "backups/" + imported.Manifest.BackupId);
        if (Directory.Exists(retained) || File.Exists(retained))
            throw new ServiceException(ServiceError.Conflict, "This snapshot was already retained; review again.");
        var staging = Paths.Under(_root, "staging/indexed-" + Guid.NewGuid().ToString("N"));
        try
        {
            var copied = StoreIo.CopyTree(Paths.Under(imported.DirectoryPath, "payload"), Paths.EnsureDirectory(staging, "payload"), _workspace.Limits);
            if (!SameFiles(copied.Files, imported.Manifest.Files) ||
                !copied.Directories.SequenceEqual(imported.Manifest.Directories.Order(StringComparer.Ordinal)))
                throw new ServiceException(ServiceError.CorruptData, "The reviewed snapshot changed during retention.");
            StoreIo.AtomicJson(Paths.Under(staging, "manifest.json"), imported.Manifest);
            Directory.Move(staging, retained);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        StoreIo.AtomicJson(Paths.Under(Paths.EnsureDirectory(_root, "indexed-guards"), imported.Manifest.BackupId + ".json"),
            new IndexedGuard(mapping.Identity, mapping.RelativeDirectory, indexFiles, indexDirectories));
        return ImportUnlocked(mapping, ReadLocalBackup(imported.Manifest.BackupId));
    }

    private sealed record IndexedGuard(SaveIdentity Identity, string RelativeDirectory,
        IReadOnlyList<ContentFile> Files, IReadOnlyList<string> Directories);

    private void VerifyIndexedRollbackGuard(SaveDirectoryMapping mapping, string backupId)
    {
        if (mapping.Identity.FormatId != "ryujinx-directory-save") return;
        var guard = StoreIo.ReadJson<IndexedGuard>(Paths.Under(_root, "indexed-guards/" + Ids.GuidId(backupId) + ".json"));
        if (guard.Identity != mapping.Identity || guard.RelativeDirectory != mapping.RelativeDirectory)
            throw new ServiceException(ServiceError.Conflict, "The indexed rollback belongs to a different native save.");
        try
        {
            var index = Paths.Under(BackendDataRoot, "bis/system/save");
            StoreIo.VerifyTree(index, guard.Files, _workspace.Limits);
            var directories = new List<string>(); Paths.EnumerateFiles(index, _workspace.Limits, directories);
            if (!directories.Order(StringComparer.Ordinal).SequenceEqual(guard.Directories.Order(StringComparer.Ordinal)))
                throw new ServiceException(ServiceError.Conflict, "The native save index changed; rollback needs a new review.");
        }
        catch (ServiceException error) when (error.Code is ServiceError.NotFound or ServiceError.CorruptData)
        { throw new ServiceException(ServiceError.Conflict, "The native index changed; the current save was preserved.", error); }
    }
}
