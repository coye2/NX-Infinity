using NxInfinity.Runtime.Services;

namespace NxInfinity.Runtime.Enhancements;

/// <summary>Owns the two-phase identity check needed for exact-build settings applied before load.</summary>
public sealed class EnhancementSession : IAsyncDisposable
{
    private readonly BackendLaunch _launch;
    private readonly RuntimeOptions _options;
    private readonly string _contentPath;
    private readonly EnhancementStore _store;
    private readonly EnhancementEnvironment _environment;
    private bool _disposed;
    private EnhancementSession(BackendLaunch launch, RuntimeOptions options, string contentPath, EnhancementStore store,
        EnhancementEnvironment environment, NxRuntime runtime, EnhancementCoordinator coordinator, EnhancementApplicationResult application)
    { _launch = launch; _options = options; _contentPath = contentPath; _store = store; _environment = environment; Runtime = runtime; Coordinator = coordinator; Application = application; }

    public NxRuntime Runtime { get; private set; }
    public EnhancementCoordinator Coordinator { get; private set; }
    public EnhancementApplicationResult Application { get; private set; }
    /// <summary>The current durable outcome, including rollback performed through the public coordinator.</summary>
    public EnhancementApplicationReceipt Receipt => _store.GetReceipt(Application.Receipt.ReceiptId);

    /// <summary>Returns loaded and paused. The caller starts gameplay explicitly.</summary>
    public static async Task<EnhancementSession> OpenAsync(BackendLaunch launch, RuntimeOptions options, string contentPath,
        EnhancementStore store, EnhancementSelection selection, EnhancementEnvironment? environment = null, CancellationToken ct = default)
    {
        var observedEnvironment = environment ?? new EnhancementEnvironment();
        EnhancementContext verified;
        var probe = await NxRuntime.ConnectAsync(launch, ct);
        try
        {
            await probe.InitializeAsync(options, ct);
            await probe.LoadAsync(contentPath, ct);
            var review = new EnhancementCoordinator(probe, store, observedEnvironment);
            var plan = review.Evaluate(selection);
            EnhancementCoordinator.RequireApplicable(plan);
            verified = review.CurrentContext;
        }
        finally { await probe.DisposeAsync(); }
        if (probe.Shutdown?.Graceful != true) throw new NxException("enhancement_recovery_required", "Identity probe did not shut down cleanly; a second worker was not launched.");

        var runtime = await NxRuntime.ConnectAsync(launch, ct);
        EnhancementApplicationResult? prepared = null;
        try
        {
            var coordinator = new EnhancementCoordinator(runtime, store, observedEnvironment);
            EnhancementCoordinator.RequireSameBackendIdentity(verified, coordinator.CurrentContext);
            await runtime.InitializeAsync(options, ct);
            // Selecting an explicitly declared timing-repair profile requests
            // its target on this new worker; no loaded bytes have changed yet.
            var selectedProfile = store.Evaluate(selection.ProfileId, selection.Version, verified, selection.SelectedChangeIds, selection.AllowExperimental);
            EnhancementCoordinator.RequireApplicable(selectedProfile);
            if (selectedProfile.Changes.Any(change => change.GamePatch?.TargetFrameRate == 60))
                await runtime.SetFrameRatePolicyAsync(60, ct);
            prepared = await coordinator.PrepareBeforeLoadAsync(selection, verified, ct);
            await runtime.LoadAsync(contentPath, ct);
            var confirmed = await coordinator.ConfirmLoadedAsync(prepared, ct);
            return new(launch, options, contentPath, store, observedEnvironment, runtime, coordinator, confirmed);
        }
        catch
        {
            await runtime.DisposeAsync();
            if (prepared is not null && store.GetReceipt(prepared.Receipt.ReceiptId).Status is EnhancementReceiptStatus.Prepared or EnhancementReceiptStatus.RecoveryRequired)
                store.RecordOutcome(prepared.Receipt.ReceiptId,
                    runtime.Shutdown?.Graceful == true ? EnhancementReceiptStatus.RolledBack : EnhancementReceiptStatus.RecoveryRequired,
                    runtime.Shutdown?.Graceful == true
                        ? "Prepared content verification failed; the never-started worker was disposed cleanly. Transient settings were discarded; persisted original preferences were unchanged."
                        : "Prepared content verification failed and worker shutdown was uncertain. Inspect the receipt and data recovery marker.");
            throw;
        }
    }

    /// <summary>Stops/disposes this worker and loads a new one with the captured original values. It does not start gameplay or restore saves.</summary>
    public async Task RestartOriginalAsync(CancellationToken ct = default)
    {
        var activeReceipt = Receipt;
        if (_disposed || activeReceipt.Status != EnhancementReceiptStatus.Applied)
            throw new NxException("invalid_state", "This enhanced session is closed or has already been rolled back.");
        if (_store.ListReceipts(Coordinator.SessionId).Any(x => x.ReceiptId != activeReceipt.ReceiptId &&
            x.Status is EnhancementReceiptStatus.Applied or EnhancementReceiptStatus.Prepared or EnhancementReceiptStatus.RecoveryRequired))
            throw new NxException("enhancement_rollback_order", "Roll back other active profiles and resolve pending receipts before restarting the original session.");
        if (Runtime.State is RuntimeState.Running or RuntimeState.Paused)
        {
            try { await Runtime.StopAsync(ct); }
            catch (NxException e) when (e.Code == "invalid_state" && Runtime.State == RuntimeState.Stopped) { }
        }
        await Runtime.DisposeAsync();
        if (Runtime.Shutdown?.Graceful != true)
        {
            _store.RecordOutcome(Receipt.ReceiptId, EnhancementReceiptStatus.RecoveryRequired, "Original restart requires recovery after an unclean shutdown.");
            throw new NxException("enhancement_recovery_required", "Review the interrupted data root before restarting.");
        }
        NxRuntime? fresh = null;
        try
        {
            fresh = await NxRuntime.ConnectAsync(_launch, ct);
            var originalCoordinator = new EnhancementCoordinator(fresh, _store, _environment);
            EnhancementCoordinator.RequireSameBackendIdentity(Receipt.Context, originalCoordinator.CurrentContext);
            await fresh.InitializeAsync(_options, ct);
            IReadOnlyList<SettingState> effective;
            await using (var restore = await fresh.BeginSettingsTransactionAsync(Receipt.OriginalSettings
                .Select(x => new RuntimeSettingChange(x.SettingId, x.Effective)).ToArray(), ct, allowEmpty: Receipt.NativePatches is { Count: > 0 }))
            {
                await restore.ApplyAsync(ct);
                effective = restore.Effective;
                restore.Complete();
            }
            await fresh.LoadAsync(_contentPath, ct);
            EnhancementCoordinator.RequireSameIdentity(Receipt.Context, originalCoordinator.CurrentContext);
            effective = await EnhancementCoordinator.ReadEffectiveAsync(fresh, Receipt.OriginalSettings
                .Select(x => new EnhancementSettingChange(x.SettingId, x.Effective)).ToArray(), ct);
            if (Receipt.NativePatches is { Count: > 0 })
            {
                // A fresh load discards transient writes, but same build metadata
                // alone does not prove originals: external mods may have changed.
                await using var originals = await fresh.BeginSettingsTransactionAsync([], ct, allowEmpty: true);
                foreach (var change in Receipt.Profile.Changes.Where(change => Receipt.SelectedChangeIds.Contains(change.ChangeId) && change.GamePatch is not null))
                    await originals.PreparePatchAsync(change.GamePatch!, ct);
                if (!await originals.RestorePatchesAsync())
                    throw new NxException("enhancement_recovery_required", "The fresh worker did not verify every original executable byte and native mod conflict check.");
            }
            var receipt = _store.RecordOutcome(Receipt.ReceiptId, EnhancementReceiptStatus.RolledBack,
                "A fresh paused runtime read back all captured original settings and loaded the same exact title/main build/core revision. Persisted preferences retained; no game-save rollback or gameplay assertion.");
            Runtime = fresh;
            Coordinator = originalCoordinator;
            Application = new(receipt, effective);
        }
        catch
        {
            if (fresh is not null) await fresh.DisposeAsync();
            _store.RecordOutcome(Receipt.ReceiptId, EnhancementReceiptStatus.RecoveryRequired, "Original-session restart could not be fully verified. Review the receipt and any runtime recovery marker.");
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await Runtime.DisposeAsync();
        // The caller may have applied other independent profiles through the public
        // coordinator. Every transient Applied receipt belongs to this disposed worker.
        foreach (var latest in _store.ListReceipts(Coordinator.SessionId)
            .Where(x => x.Status == EnhancementReceiptStatus.Applied).OrderByDescending(x => x.CreatedUtc))
        {
            _store.RecordOutcome(latest.ReceiptId,
                Runtime.Shutdown?.Graceful == true ? EnhancementReceiptStatus.RolledBack : EnhancementReceiptStatus.RecoveryRequired,
                Runtime.Shutdown?.Graceful == true
                    ? "Enhanced session disposed cleanly; its transient overrides ended. Original persisted preferences remain available. No game-save rollback assertion."
                    : "Enhanced session ended without verified clean shutdown. Original-value and runtime recovery receipts remain for review.");
        }
        Application = Application with { Receipt = _store.GetReceipt(Application.Receipt.ReceiptId) };
    }
}
