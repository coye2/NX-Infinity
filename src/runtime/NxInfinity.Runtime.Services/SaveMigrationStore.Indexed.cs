using System.Text.Json;

namespace NxInfinity.Runtime.Services;

public sealed record IndexedSaveImportReview(string ReviewId, string BackendId, string TitleId, string UserId,
    string SourceOrigin, int SourceFiles, int DestinationFiles, DateTimeOffset CreatedUtc);

public sealed partial class SaveMigrationStore
{
    private sealed record IndexedReview(IndexedSaveImportReview Public, BackendLaunch Launch, string ProfileRelative,
        SaveDirectoryMapping Mapping, string BaselineBackupId, string Inbox, string IndexDirectory, CopiedTree IndexTree, string SourceId, string OriginalSource, string SourceFingerprint, string AdapterVersion);
    private readonly Dictionary<string, IndexedReview> _indexedReviews = new(StringComparer.Ordinal);

    public static bool SupportsIndexedImport(string backendId) => backendId is "nxi";

    public static bool SupportsAutomaticImport(string backendId) => SupportsIndexedImport(backendId);

    /// <summary>Native-verifies an external Ryujinx-family index on a private clone. Never writes to the source.</summary>
    public Task<IndexedSaveImportReview> ReviewIndexedImportAsync(SaveMigrationTarget target,
        SaveMigrationDiscoveryOptions? options = null, CancellationToken cancellationToken = default) =>
        ReviewIndexedImportInternalAsync(target, options, requireEmptyDestination: false, cancellationToken);

    private async Task<IndexedSaveImportReview> ReviewIndexedImportInternalAsync(SaveMigrationTarget target,
        SaveMigrationDiscoveryOptions? options, bool requireEmptyDestination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var launch = BindLaunch(target.Launch);
        var mapping = NativeSaveMappings.FromNativeObservation(target.Observation, launch.BackendId, launch.Revision);
        if (!SupportsIndexedImport(launch.BackendId) || mapping.Identity.FormatId != "ryujinx-directory-save" ||
            mapping.Identity.FormatVersion != "1" || !IndexedSavePath(mapping.RelativeDirectory))
            throw new ServiceException(ServiceError.UnverifiedSaveMapping, "This operation requires an indexed Ryujinx-family native save mapping.");
        string profileRelative = "backends/" + launch.BackendId + "/" + Ids.Slug(target.ProfileId);
        string profile = Paths.Under(_workspace.RootPath, profileRelative);
        if (!Directory.Exists(profile)) throw new ServiceException(ServiceError.NotFound, "Create this native save in-game before importing.");
        using var serviceLease = _workspace.Acquire("save-migration");
        if (_indexedReviews.Count >= 8) throw new ServiceException(ServiceError.Busy, "Finish or cancel the existing save reviews first.");
        using var client = StoreIo.AcquireLock(Paths.Under(profile, ".nx-client.lock"));
        CheckRecovery(profile);
        var state = ReadState();
        if (state.Receipts.Count >= MaximumRecords) throw new ServiceException(ServiceError.Unavailable, "The bounded migration ledger is full.");
        var checkedOptions = ValidateOptions(options);
        var discovery = DiscoverCore(state, checkedOptions, cancellationToken);
        string probe = Paths.EnsureDirectory(_root, "indexed-probe-" + Guid.NewGuid().ToString("N"));
        string? inbox = null;
        try
        {
            // Index metadata is bound as well as payload: a numeric save ID is not a title/user ID.
            string indexDirectory = "bis/system/save";
            var indexPath = Paths.Under(profile, indexDirectory);
            CopiedTree indexTree;
            SaveBackup baseline;
            using (StoreIo.AcquireLock(Paths.Under(profile, ".nx-runtime.lock")))
            {
                // Avoid copying a populated game save merely to reject an
                // automatic import at every launcher startup.
                if (requireEmptyDestination && !EmptyIndexedDirectory(Paths.Under(profile, mapping.RelativeDirectory)))
                    throw new IndexedAutomaticOutcome("target_conflict");
                if (!Directory.Exists(indexPath)) throw new ServiceException(ServiceError.UnverifiedSaveMapping, "The target native save index is missing.");
                indexTree = await InventoryAsync(indexPath, cancellationToken);
            }
            baseline = new SaveStore(_workspace, profileRelative).CreateBackup(mapping);
            // LibHac's new directory-save container already has 0/, 1/ and
            // ExtraData. Only that metadata-only shape is safe for automatic
            // import; any payload, unknown file or nested directory needs review.
            // Apply compares this complete hashed baseline again under the
            // runtime lease, so progress appearing after this check wins.
            if (requireEmptyDestination && !EmptyIndexedContainer(baseline.Manifest.Files, baseline.Manifest.Directories))
                throw new IndexedAutomaticOutcome("target_conflict");
            // Re-resolve the destination on a private clone: do not trust a stale numeric save path.
            string targetWork = Paths.EnsureDirectory(probe, "target-check");
            string targetClone = Paths.EnsureDirectory(targetWork, "backends/" + launch.BackendId + "/probe");
            await CopyCheckedAsync(indexPath, Paths.EnsureDirectory(targetClone, indexDirectory), cancellationToken);
            await CopyCheckedAsync(Paths.Under(baseline.DirectoryPath, "payload"),
                Paths.EnsureDirectory(targetClone, mapping.RelativeDirectory), cancellationToken);
            VerifyUnchanged(indexPath, indexTree);
            var targetCatalog = await ObserveCloneAsync(launch, targetWork, mapping.Identity.TitleId, cancellationToken);
            bool exactTarget = targetCatalog.Catalog.Entries.Any(entry => entry.Existing && entry.AdapterVerified &&
                entry.BackendId == launch.BackendId && entry.TitleId.Equals(mapping.Identity.TitleId, StringComparison.OrdinalIgnoreCase) &&
                entry.UserId.Equals(mapping.Identity.UserId, StringComparison.OrdinalIgnoreCase) && entry.Type == target.Observation.Type &&
                entry.SaveDataId.Equals(target.Observation.SaveDataId, StringComparison.OrdinalIgnoreCase) &&
                entry.RelativePath == mapping.RelativeDirectory && entry.FormatId == mapping.Identity.FormatId && entry.FormatVersion == "1");
            if (!exactTarget) throw new ServiceException(ServiceError.Conflict, "The destination index no longer resolves the reviewed game/user/save. Discover it again.");
            VerifyUnchanged(indexPath, indexTree);
            var matches = new List<VerifiedSource>();
            foreach (var source in discovery.Roots.Where(root => !root.Plain && root.Candidates.Length != 0))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (SourceBusy(source.Location)) throw new ServiceException(ServiceError.Busy, "Close the source emulator before reviewing its saves.");
                var sourceLockPath = Path.Combine(source.Location.RootPath, ".nx-runtime.lock");
                using var sourceLock = File.Exists(sourceLockPath) ? StoreIo.AcquireLock(sourceLockPath) : null;
                string work = Paths.EnsureDirectory(probe, Guid.NewGuid().ToString("N"));
                string clone = Paths.EnsureDirectory(work, "backends/" + launch.BackendId + "/probe");
                string sourceIndex = Path.Combine(source.Location.RootPath, "bis/system/save");
                if (!Directory.Exists(sourceIndex)) continue;
                var copiedIndex = await CopyCheckedAsync(sourceIndex, Paths.EnsureDirectory(clone, indexDirectory), cancellationToken);
                var copiedSaves = await CopyCheckedAsync(source.SaveRoot, Paths.EnsureDirectory(clone, "bis/user/save"), cancellationToken);
                await Task.Delay(checkedOptions.StabilityWindow, cancellationToken);
                VerifyUnchanged(sourceIndex, copiedIndex); VerifyUnchanged(source.SaveRoot, copiedSaves);
                var observed = await ObserveCloneAsync(launch, work, mapping.Identity.TitleId, cancellationToken);
                VerifyUnchanged(sourceIndex, copiedIndex); VerifyUnchanged(source.SaveRoot, copiedSaves);
                if (SourceBusy(source.Location, checkLease: false)) throw new ServiceException(ServiceError.Busy, "The source emulator became active during verification.");
                foreach (var item in observed.Catalog.Entries.Where(item => item.TitleId.Equals(mapping.Identity.TitleId, StringComparison.OrdinalIgnoreCase) &&
                    item.UserId.Equals(mapping.Identity.UserId, StringComparison.OrdinalIgnoreCase) && item.Type == target.Observation.Type))
                {
                    var sourceMapping = NativeSaveMappings.FromNativeObservation(new(item.BackendId, launch.Revision, item.TitleId,
                        item.UserId, item.SaveDataId, item.Type, item.FormatId, item.FormatVersion, item.RelativePath,
                        item.Existing, item.AdapterVerified, item.Provenance), launch.BackendId, launch.Revision);
                    if (sourceMapping.Identity != mapping.Identity || !IndexedSavePath(sourceMapping.RelativeDirectory))
                        throw new ServiceException(ServiceError.UnverifiedSaveMapping, "The source index does not resolve the exact compatible save identity.");
                    string clonedSave = Paths.Under(clone, sourceMapping.RelativeDirectory);
                    string original = Paths.Under(source.Location.RootPath, sourceMapping.RelativeDirectory);
                    var tree = await InventoryAsync(clonedSave, cancellationToken);
                    VerifyUnchanged(original, tree);
                    if (tree.Files.Count == 0 || requireEmptyDestination && EmptyIndexedContainer(tree.Files, tree.Directories)) continue;
                    matches.Add(new(SourceId(state, source.Location.Origin, original), source.Location.Origin, original,
                        clonedSave, sourceMapping, observed.AdapterVersion, tree.Files, tree.Directories, TreeFingerprint(tree)));
                }
            }
            if (matches.Count != 1 && requireEmptyDestination)
                throw new IndexedAutomaticOutcome(matches.Count == 0
                    ? discovery.Report.Locations.Any(location => location.Status == "deferred_busy") ? "source_busy" : "no_matching_source"
                    : "source_conflict");
            if (matches.Count != 1) throw new ServiceException(matches.Count == 0 ? ServiceError.NotFound : ServiceError.Conflict,
                matches.Count == 0 ? "No native-verified external save matches this exact game and user. Select its emulator data folder; no user IDs are guessed."
                    : "Multiple matching sources exist. Keep only the intended source location in the scan before importing.");
            var match = matches.Single();
            string reviewId = Guid.NewGuid().ToString("N"), backupId = Guid.NewGuid().ToString("N");
            inbox = "indexed-save-" + reviewId;
            string incoming = Paths.EnsureDirectory(_workspace.ImportsPath, inbox);
            var payload = await CopyCheckedAsync(match.CloneDirectory, Paths.EnsureDirectory(incoming, "payload"), cancellationToken);
            if (TreeFingerprint(payload) != match.Fingerprint)
                throw new ServiceException(ServiceError.CorruptData, "The reviewed source snapshot changed.");
            StoreIo.AtomicJson(Paths.Under(incoming, "manifest.json"), new SaveBackupManifest(1, backupId, mapping.Identity,
                DateTimeOffset.UtcNow, mapping.RelativeDirectory, mapping.VerificationEvidence, payload.Files, payload.Directories));
            var result = new IndexedSaveImportReview(reviewId, launch.BackendId, mapping.Identity.TitleId, mapping.Identity.UserId,
                match.Origin, payload.Files.Count, baseline.Manifest.Files.Count, DateTimeOffset.UtcNow);
            WriteState(state);
            _indexedReviews.Add(reviewId, new(result, launch, profileRelative, mapping, baseline.Manifest.BackupId,
                inbox, indexDirectory, indexTree, match.SourceId, match.OriginalDirectory, match.Fingerprint, match.AdapterVersion));
            inbox = null;
            return result;
        }
        finally
        {
            if (inbox is not null && Directory.Exists(Paths.Under(_workspace.ImportsPath, inbox)))
                Directory.Delete(Paths.Under(_workspace.ImportsPath, inbox), true);
            if (Directory.Exists(probe)) TryDeleteProbes(probe);
        }
    }

    public Task<SaveImportReceipt> ApplyIndexedImportAsync(string reviewId, bool acknowledged, CancellationToken cancellationToken = default)
    {
        reviewId = Ids.GuidId(reviewId);
        if (!acknowledged) throw new ServiceException(ServiceError.InvalidInput, "Explicit confirmation is required to replace a save.");
        cancellationToken.ThrowIfCancellationRequested();
        using var serviceLease = _workspace.Acquire("save-migration");
        if (!_indexedReviews.TryGetValue(reviewId, out var review))
            throw new ServiceException(ServiceError.NotFound, "This review expired or was already applied. Review the source again.");
        BindLaunch(review.Launch);
        string profile = Paths.Under(_workspace.RootPath, review.ProfileRelative);
        using var client = StoreIo.AcquireLock(Paths.Under(profile, ".nx-client.lock"));
        CheckRecovery(profile);
        var state = ReadState();
        if (state.Receipts.Count >= MaximumRecords) throw new ServiceException(ServiceError.Unavailable, "The bounded migration ledger is full.");
        var item = new SaveMigrationReceipt(reviewId, review.Public.BackendId, review.Launch.Revision, review.AdapterVersion,
            LaunchFingerprint(review.Launch), review.ProfileRelative.Split('/')[2], review.Public.TitleId, review.Public.UserId,
            review.SourceId, review.Public.SourceOrigin, review.Mapping.Identity.FormatId, review.Mapping.Identity.FormatVersion,
            DateTimeOffset.UtcNow, "applying", false);
        var record = new ReceiptRecord(item, review.Mapping, review.ProfileRelative, new string('0', 32),
            new string('0', 32), review.BaselineBackupId, review.OriginalSource, review.SourceFingerprint);
        state.Receipts.Add(record); WriteState(state);
        try
        {
            var store = new SaveStore(_workspace, review.ProfileRelative);
            var receipt = store.ImportReviewedIndexedFromInbox(review.Mapping, review.Inbox, review.BaselineBackupId,
                review.IndexDirectory, review.IndexTree.Files, review.IndexTree.Directories);
            state.Receipts[^1] = record with { Receipt = item with { Status = "imported", CanRollback = true },
                TransactionId = receipt.TransactionId, ImportedBackupId = receipt.ImportedBackupId, PreservedBackupId = receipt.PreservedBackupId };
            WriteState(state);
            _indexedReviews.Remove(reviewId);
            var incoming = Paths.Under(_workspace.ImportsPath, review.Inbox);
            try { if (Directory.Exists(incoming)) Directory.Delete(incoming, true); } catch (IOException) { }
            return Task.FromResult(receipt);
        }
        catch (ServiceException error) when (error.Code is ServiceError.Busy or ServiceError.Conflict or ServiceError.NotFound)
        {
            state.Receipts.RemoveAll(value => value.Receipt.ReceiptId == reviewId && value.Receipt.Status == "applying");
            WriteState(state); throw;
        }
    }

    public void DiscardIndexedImport(string reviewId)
    {
        reviewId = Ids.GuidId(reviewId);
        using var lease = _workspace.Acquire("save-migration");
        if (!_indexedReviews.Remove(reviewId, out var review)) return;
        var inbox = Paths.Under(_workspace.ImportsPath, review.Inbox);
        if (Directory.Exists(inbox)) Directory.Delete(inbox, true);
    }

    private static bool IndexedSavePath(string relative)
    {
        var parts = relative.Split('/');
        return parts.Length == 4 && parts[0] == "bis" && parts[1] == "user" && parts[2] == "save" && Hex(parts[3], 16);
    }

    private static bool EmptyIndexedContainer(IReadOnlyList<ContentFile> files, IReadOnlyList<string> directories) =>
        files.All(file => file.Path == "ExtraData") && directories.All(directory => directory is "0" or "1");

    private static bool EmptyIndexedDirectory(string directory)
    {
        Paths.RejectLinks(directory);
        if (!Directory.Exists(directory))
            throw new ServiceException(ServiceError.NotFound, "Create this native save in-game before importing.");
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            Paths.RejectLinks(entry);
            if (Directory.Exists(entry))
            {
                if (Path.GetFileName(entry) is not ("0" or "1") || Directory.EnumerateFileSystemEntries(entry).Any()) return false;
            }
            else if (Path.GetFileName(entry) != "ExtraData") return false;
        }
        return true;
    }

    private sealed class IndexedAutomaticOutcome(string status) : Exception
    {
        public string Status { get; } = status;
    }
}
