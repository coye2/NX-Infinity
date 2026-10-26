namespace NxInfinity.Runtime.Services;

public sealed partial class SaveMigrationStore
{
    /// <summary>
    /// Imports matching external saves into NX Infinity Core.
    /// Existing progress is never replaced automatically. Indexed saves use
    /// the same index, snapshot and transaction guards as reviewed imports.
    /// </summary>
    public async Task<SaveMigrationReport> AutoImportAsync(IEnumerable<SaveMigrationTarget> targets,
        SaveMigrationDiscoveryOptions? options = null, IProgress<SaveMigrationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var selected = targets.Take(65).ToArray();
        if (selected.Length > 64) throw new ServiceException(ServiceError.InvalidInput, "Use at most 64 explicit native save targets.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // Validate the complete batch before performing any import. A bad
        // later target must not turn a rejected batch into a partial write.
        for (int index = 0; index < selected.Length; index++)
        {
            var target = selected[index];
            if (target?.Launch is null || target.Observation is null)
                throw new ServiceException(ServiceError.InvalidInput, "An exact installed core and native save observation are required.");
            var launch = BindLaunch(target.Launch);
            string profile = Ids.Slug(target.ProfileId);
            string title = Ids.Hex(target.Observation.TitleId, 16, "Title ID");
            string user = Ids.Hex(target.Observation.UserId, 32, "User ID");
            if (!seen.Add(launch.BackendId + ":" + profile + ":" + title + ":" + user))
                throw new ServiceException(ServiceError.InvalidInput, "The batch contains a duplicate native save target.");
            selected[index] = target with { Launch = launch, ProfileId = profile,
                Observation = target.Observation with { TitleId = title, UserId = user } };
        }
        var checkedOptions = ValidateOptions(options);
        var outcomes = new List<SaveMigrationOutcome>();
        var plain = selected.Where(target => !SupportsIndexedImport(target.Launch.BackendId)).ToArray();
        bool cancelled = false;
        if (plain.Length != 0)
        {
            var report = await AutoImportPlainAsync(plain, checkedOptions,
                progress is null ? null : new SaveBatchProgress(progress, selected.Length), cancellationToken);
            outcomes.AddRange(report.Outcomes);
            cancelled = report.Cancelled;
        }
        foreach (var target in selected.Where(target => SupportsIndexedImport(target.Launch.BackendId)))
        {
            string backend = target.Launch.BackendId, profile = Ids.Slug(target.ProfileId);
            string title = Ids.Hex(target.Observation.TitleId, 16, "Title ID"), user = Ids.Hex(target.Observation.UserId, 32, "User ID");
            SaveMigrationOutcome Outcome(string status, string? error = null, string? receiptId = null, bool verified = false) =>
                Sanitize(new(backend, profile, title, user, status, "", error, receiptId,
                    status == "imported", verified, status == "imported"));
            string? reviewId = null;
            try
            {
                if (cancellationToken.IsCancellationRequested)
                { cancelled = true; outcomes.Add(Outcome("cancelled", "cancelled")); continue; }
                ReceiptRecord? prior;
                using (var lease = _workspace.Acquire("save-migration"))
                    prior = ReadState().Receipts.LastOrDefault(record => record.Receipt.BackendId == backend &&
                        record.Receipt.ProfileId == profile && record.Receipt.TitleId == title && record.Receipt.UserId == user);
                if (prior is not null)
                {
                    string status = prior.Receipt.Status == "imported" ? "already_imported" :
                        prior.Receipt.Status == "rolled_back" ? "rolled_back" : "interrupted";
                    outcomes.Add(Outcome(status, receiptId: prior.Receipt.ReceiptId)); continue;
                }
                if (!target.Observation.Existing)
                { outcomes.Add(Outcome("target_allocation_unavailable")); continue; }
                progress?.Report(new(backend, title, "native_source_verification", outcomes.Count, selected.Length));
                var review = await ReviewIndexedImportInternalAsync(target, checkedOptions,
                    requireEmptyDestination: true, cancellationToken);
                reviewId = review.ReviewId;
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new(backend, title, "importing_empty_target", outcomes.Count, selected.Length));
                // Automatic import is limited to the reviewed metadata-only
                // baseline. The transaction rechecks every baseline/index byte
                // while owning the runtime lease before replacing anything.
                await ApplyIndexedImportAsync(reviewId, acknowledged: true, cancellationToken);
                outcomes.Add(Outcome("imported", receiptId: reviewId, verified: true));
            }
            catch (IndexedAutomaticOutcome result) { outcomes.Add(Outcome(result.Status)); }
            catch (OperationCanceledException)
            { cancelled = true; outcomes.Add(Outcome("cancelled", "cancelled")); }
            catch (NxException error)
            {
                if (error.Code == "cancelled") cancelled = true;
                outcomes.Add(Outcome(error.Code == "cancelled" ? "cancelled" : "native_verification_unavailable", SafeError(error.Code)));
            }
            catch (ServiceException error)
            {
                outcomes.Add(Outcome(error.Code switch
                {
                    ServiceError.Busy => "deferred_busy", ServiceError.RecoveryRequired => "recovery_required",
                    ServiceError.Conflict => "target_conflict", ServiceError.CorruptData => "source_unstable",
                    ServiceError.UnsafePath => "unsafe_source", ServiceError.UnverifiedSaveMapping => "identity_unverified",
                    ServiceError.NotFound => "target_allocation_unavailable", _ => "failed"
                }, error.Code.ToString().ToLowerInvariant()));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { outcomes.Add(Outcome("failed", "io_error")); }
            finally
            {
                if (reviewId is not null)
                {
                    // A completed apply already removes its review. Failed or
                    // cancelled reviews must not consume the bounded review slots.
                    try { DiscardIndexedImport(reviewId); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ServiceException) { }
                }
                progress?.Report(new(backend, title, "target_complete", outcomes.Count, selected.Length));
            }
        }
        // Preserve request order and the complete mixed-core report. Each
        // underlying transaction may have written a newer ledger meanwhile.
        var ordered = selected.Select(target => outcomes.Single(outcome => outcome.BackendId == target.Launch.BackendId &&
            outcome.ProfileId == target.ProfileId && outcome.TitleId == Ids.Hex(target.Observation.TitleId, 16, "Title ID") &&
            outcome.UserId == Ids.Hex(target.Observation.UserId, 32, "User ID"))).ToArray();
        using (var lease = _workspace.Acquire("save-migration"))
        {
            var state = ReadState(); state.LastOutcomes.Clear(); state.LastOutcomes.AddRange(ordered); WriteState(state);
        }
        return new(Array.AsReadOnly(ordered), cancelled, DateTimeOffset.UtcNow, Scope);
    }

    private sealed class SaveBatchProgress(IProgress<SaveMigrationProgress> target, int total) : IProgress<SaveMigrationProgress>
    {
        public void Report(SaveMigrationProgress value) => target.Report(value with { TotalTargets = total });
    }
}
