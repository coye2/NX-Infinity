using System.Text.Json;

namespace NxInfinity.Runtime;

public sealed partial class NxRuntime
{
    private const int MaximumPerformancePreviews = 16;
    private const int MaximumPerformanceReceipts = 32;
    private readonly SemaphoreSlim _performanceOperations = new(1, 1);
    private readonly Dictionary<string, PerformancePlanPreview> _performancePlans = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PerformancePlanReceipt> _performanceReceipts = new(StringComparer.Ordinal);

    private sealed record PerformanceObjective(string Id, string Label, string SettingId, JsonElement Value, string Kind);
    private static readonly PerformanceObjective[] PerformanceObjectives =
    [
        new("shader_cache", "Shader cache", "graphics.shader_cache", JsonSerializer.SerializeToElement(true), "boolean"),
        new("cpu_cache", "CPU translation cache", "cpu.ptc", JsonSerializer.SerializeToElement(true), "boolean"),
        new("native_resolution", "Native 1× resolution", "graphics.resolution_scale", JsonSerializer.SerializeToElement(1), "number"),
        new("normal_game_clock", "Normal guest synchronization", "graphics.vsync", JsonSerializer.SerializeToElement("switch"), "enum")
    ];

    /// <summary>
    /// Audit this exact adapter's known cache, native-scale, and normal guest-sync
    /// settings. This is a read-only preview, not a game patch or FPS unlock.
    /// Refresh after pausing or changing any setting before applying the opaque ID.
    /// </summary>
    public async Task<PerformancePlanPreview> PreviewPerformancePlanAsync(CancellationToken cancellationToken = default)
    {
        await _performanceOperations.WaitAsync(cancellationToken);
        try
        {
            await _operations.WaitAsync(cancellationToken);
            try
            {
                Require(RuntimeState.Initialized, RuntimeState.Loaded, RuntimeState.Running, RuntimeState.Paused);
                bool readable = Supports("settings.list") && Supports("settings.get");
                if (readable) await EnsureSchemasAsync(cancellationToken);
                var entries = new List<PerformancePlanEntry>();
                foreach (var objective in PerformanceObjectives)
                    entries.Add(await PreviewPerformanceEntryAsync(objective, readable, cancellationToken));
                int changes = entries.Count(entry => entry.Included);
                var preview = new PerformancePlanPreview
                {
                    PlanId = Guid.NewGuid().ToString("N"), BackendId = Backend.BackendId,
                    BackendRevision = Backend.BackendRevision, AdapterVersion = Backend.AdapterVersion, CreatedUtc = DateTimeOffset.UtcNow, RuntimeState = State,
                    CanApply = changes != 0 && State is RuntimeState.Initialized or RuntimeState.Loaded or RuntimeState.Paused,
                    ChangeCount = changes, Entries = Array.AsReadOnly(entries.ToArray()),
                    Limitations = Array.AsReadOnly(new[]
                    {
                        "Only Included entries are proposed. Unsupported, unknown, dependent, or restart-only entries retain their reported status.",
                        "Apply and rollback affect this runtime only; saved preferences are preserved. A later runtime loads its existing saved preferences.",
                        "Pause and refresh the preview before applying to a running game. Before-load settings must be rolled back before content is loaded.",
                        "Cache use and native scale do not guarantee faster or correct gameplay. Normal guest synchronization is not a gameplay-speed qualification.",
                        "Title-specific rendering patches and FPS options require their separate exact-build compatibility and gameplay validation."
                    })
                };
                while (_performancePlans.Count >= MaximumPerformancePreviews)
                    _performancePlans.Remove(_performancePlans.First().Key);
                _performancePlans.Add(preview.PlanId, preview);
                return preview;
            }
            finally { _operations.Release(); }
        }
        finally { _performanceOperations.Release(); }
    }

    private async Task<PerformancePlanEntry> PreviewPerformanceEntryAsync(PerformanceObjective objective, bool readable, CancellationToken ct)
    {
        var entry = new PerformancePlanEntry
        { ObjectiveId = objective.Id, Label = objective.Label, Requested = objective.Value.Clone() };
        if (!readable)
            return entry with { Status = PerformancePlanEntryStatus.Unsupported, Reason = "This adapter does not expose the settings schema and effective readback required for this audit." };
        // A semantic ID may be shared or explicitly namespaced by the exact core.
        // Never infer support from a related core's ID, label, or default value.
        var candidates = new[] { objective.SettingId, Backend.BackendId + "." + objective.SettingId }
            .Where(id => _schema!.ContainsKey(id)).Distinct(StringComparer.Ordinal).ToArray();
        if (candidates.Length == 0)
            return entry with { Status = PerformancePlanEntryStatus.Unsupported, Reason = "This exact adapter does not advertise a supported setting for this objective." };
        if (candidates.Length != 1)
            return entry with { Status = PerformancePlanEntryStatus.Unavailable, Reason = "Multiple advertised settings use this semantic; an adapter-specific mapping is required." };
        var schema = _schema![candidates[0]];
        entry = entry with { SettingId = schema.Id, ApplyPhase = schema.Apply };
        if (!schema.Supported || schema.Kind != objective.Kind && !(objective.Kind == "number" && schema.Kind == "integer"))
            return entry with { Status = PerformancePlanEntryStatus.Unsupported, Reason = "The advertised setting's support or value semantics do not match this bounded objective." };
        try { schema.Validate(objective.Value); }
        catch (NxException exception) when (exception.Code is "invalid_argument" or "unsupported")
        { return entry with { Status = PerformancePlanEntryStatus.Unsupported, Reason = "The requested baseline value is outside the setting's advertised values or range." }; }
        SettingState state;
        try { state = await ReadPerformanceSettingAsync(schema.Id, ct); }
        catch (NxException exception) when (exception.Code == "unsupported")
        { return entry with { Status = PerformancePlanEntryStatus.Unavailable, Reason = "This advertised setting cannot currently supply a native readback." }; }
        entry = entry with { Original = state };
        if (!state.EffectiveKnown || state.Effective is null)
            return entry with { Status = PerformancePlanEntryStatus.EffectiveStateUnknown, Reason = "The native consumer has not confirmed an effective original value; a rollback-safe change cannot be proposed." };
        try { schema.Validate(state.Effective.Value); }
        catch (NxException)
        { return entry with { Status = PerformancePlanEntryStatus.Unavailable, Reason = "The observed original value does not conform to the advertised schema." }; }
        if (state.RequiresRestart || state.Requested is { } requested && !JsonElement.DeepEquals(requested, state.Effective.Value))
            return entry with { Status = PerformancePlanEntryStatus.PendingSetting, Reason = "Resolve this setting's existing pending request, then refresh the preview." };
        if (JsonElement.DeepEquals(state.Effective.Value, objective.Value))
            return entry with { Status = PerformancePlanEntryStatus.AlreadyEffective, Reason = "Native readback already confirms the requested setting value; no write is proposed." };
        if (!Supports("settings.set"))
            return entry with { Status = PerformancePlanEntryStatus.Unsupported, Reason = "The adapter exposes readback but does not expose setting writes." };
        if (schema.Dependencies.Length != 0)
            return entry with { Status = PerformancePlanEntryStatus.DependencyUnverified, Reason = "This setting advertises dependencies without a verified generic dependency resolver." };
        if (schema.Apply == "before_initialize")
            return entry with { Status = PerformancePlanEntryStatus.RestartRequired, Reason = "This setting is bound at initialization and requires a separate explicit next-session initialization choice." };
        if (schema.Apply is "before_load" or "restart" && State != RuntimeState.Initialized)
            return entry with { Status = PerformancePlanEntryStatus.RestartRequired, Reason = "This setting can only be changed before loading content in a fresh initialized runtime." };
        return entry with
        {
            Status = PerformancePlanEntryStatus.Ready, Included = true,
            Reason = State == RuntimeState.Running ? "Pause and refresh the preview before applying this value." :
                "Ready for an explicit runtime-only transaction with effective readback and a preserved original value."
        };
    }

    /// <summary>
    /// Apply one reviewed opaque preview in this exact runtime. Original values
    /// are rechecked under the settings transaction lease before any write.
    /// Failed writes trigger verified restoration; an uncertain restoration faults
    /// the runtime and reports recovery_required. Disk preferences are not written.
    /// </summary>
    public async Task<PerformancePlanReceipt> ApplyPerformancePlanAsync(string planId, CancellationToken cancellationToken = default)
    {
        await _performanceOperations.WaitAsync(cancellationToken);
        try
        {
            if (string.IsNullOrWhiteSpace(planId) || !_performancePlans.Remove(planId, out var preview))
                throw new NxException("performance_plan_unknown", "This preview is absent, expired, already attempted, or belongs to another runtime. Refresh the preview.");
            if (!preview.CanApply)
                throw new NxException("performance_plan_not_applicable", "This preview has no eligible changes in its reviewed runtime state. Review its entry statuses and refresh it.");
            if (_performanceReceipts.Values.Count(receipt => !receipt.RolledBack) >= MaximumPerformanceReceipts)
                throw new NxException("performance_receipt_limit", "Rollback existing performance transactions before creating more active receipts in this runtime.");
            var changes = preview.Entries.Where(entry => entry.Included)
                .Select(entry => new RuntimeSettingChange(entry.SettingId!, entry.Requested)).ToArray();
            await using var transaction = await BeginSettingsTransactionAsync(changes, cancellationToken);
            if (State != preview.RuntimeState)
                throw new NxException("performance_preview_stale", "The runtime lifecycle changed after this preview. Refresh it before applying any value.");
            foreach (var entry in preview.Entries.Where(entry => entry.Original is not null))
            {
                var current = await ReadPerformanceSettingAsync(entry.SettingId!, cancellationToken);
                if (!SamePerformanceSetting(current, entry.Original!))
                    throw new NxException("performance_preview_stale", "A setting changed after this preview. No plan values were written; refresh the preview.");
            }
            try { await transaction.ApplyAsync(cancellationToken); }
            catch (Exception exception)
            {
                bool restored = await transaction.RestoreAsync();
                if (!restored)
                    throw new NxException("recovery_required", "The performance plan failed and the original runtime values could not all be verified restored. Dispose the faulted runtime before recovery.", exception);
                throw new NxException("performance_apply_restored", "The performance plan failed; every touched original runtime value was restored and read back. Refresh the preview before another attempt.", exception);
            }
            transaction.Complete();
            var receipt = new PerformancePlanReceipt
            {
                ReceiptId = Guid.NewGuid().ToString("N"), PlanId = preview.PlanId,
                BackendId = Backend.BackendId, BackendRevision = Backend.BackendRevision, AdapterVersion = Backend.AdapterVersion, AppliedUtc = DateTimeOffset.UtcNow,
                Originals = CopyPerformanceStates(transaction.Originals), Effective = CopyPerformanceStates(transaction.Effective)
            };
            foreach (var old in _performanceReceipts.Where(pair => pair.Value.RolledBack).Select(pair => pair.Key).ToArray())
                _performanceReceipts.Remove(old);
            _performanceReceipts.Add(receipt.ReceiptId, receipt);
            return receipt;
        }
        finally { _performanceOperations.Release(); }
    }

    /// <summary>
    /// Restore a receipt's exact transient originals in the same runtime. A later
    /// manual change or incompatible lifecycle blocks rollback before any write.
    /// Before-load settings require rollback while still initialized.
    /// </summary>
    public async Task<PerformancePlanReceipt> RollbackPerformancePlanAsync(string receiptId, CancellationToken cancellationToken = default)
    {
        await _performanceOperations.WaitAsync(cancellationToken);
        try
        {
            if (string.IsNullOrWhiteSpace(receiptId) || !_performanceReceipts.TryGetValue(receiptId, out var receipt))
                throw new NxException("performance_receipt_unknown", "This runtime does not own that performance receipt.");
            if (receipt.RolledBack) return receipt;
            var changes = receipt.Originals.Select(state => new RuntimeSettingChange(state.Id, state.Effective!.Value)).ToArray();
            await using var transaction = await BeginSettingsTransactionAsync(changes, cancellationToken);
            foreach (var current in transaction.Originals)
                if (!SamePerformanceSetting(current, receipt.Effective.Single(state => state.Id == current.Id)))
                    throw new NxException("performance_rollback_conflict", "A setting was changed after this plan. No rollback values were written; review the later change before restoring an original.");
            try { await transaction.ApplyAsync(cancellationToken); }
            catch (Exception exception)
            {
                bool restored = await transaction.RestoreAsync();
                if (!restored)
                    throw new NxException("recovery_required", "Rollback failed and the pre-rollback runtime values could not all be verified restored. Dispose the faulted runtime before recovery.", exception);
                throw new NxException("performance_rollback_restored", "Rollback failed; the pre-rollback applied state was restored and read back. The receipt remains active.", exception);
            }
            transaction.Complete();
            var restoredReceipt = receipt with
            { RolledBack = true, RolledBackUtc = DateTimeOffset.UtcNow, Restored = CopyPerformanceStates(transaction.Effective) };
            _performanceReceipts[receiptId] = restoredReceipt;
            return restoredReceipt;
        }
        finally { _performanceOperations.Release(); }
    }

    // Call only while the runtime's _operations lease is held.
    private async Task<SettingState> ReadPerformanceSettingAsync(string id, CancellationToken ct)
    {
        return CopyPerformanceState(await ReadSettingStateUnlockedAsync(id, ct));
    }

    private static bool SamePerformanceSetting(SettingState left, SettingState right) =>
        left.Id == right.Id && left.EffectiveKnown == right.EffectiveKnown && left.RequiresRestart == right.RequiresRestart &&
        SamePerformanceValue(left.Effective, right.Effective) && SamePerformanceValue(left.Requested, right.Requested) &&
        SamePerformanceValue(left.Persisted, right.Persisted);

    private static bool SamePerformanceValue(JsonElement? left, JsonElement? right) =>
        left is { } first && right is { } second ? JsonElement.DeepEquals(first, second) : left is null && right is null;

    private static SettingState CopyPerformanceState(SettingState state) => state with
    { Requested = state.Requested?.Clone(), Effective = state.Effective?.Clone(), Persisted = state.Persisted?.Clone() };

    private static IReadOnlyList<SettingState> CopyPerformanceStates(IEnumerable<SettingState> states) =>
        Array.AsReadOnly(states.Select(CopyPerformanceState).ToArray());
}
