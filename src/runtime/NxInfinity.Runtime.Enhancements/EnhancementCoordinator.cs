using System.Runtime.CompilerServices;
using System.Text.Json;
using NxInfinity.Runtime.Services;

namespace NxInfinity.Runtime.Enhancements;

public sealed record EnhancementSelection(string ProfileId, string Version,
    IReadOnlyList<string>? SelectedChangeIds = null, bool AllowExperimental = false);

/// <summary>Caller supplies verified local observations. Absence never means a clean patch set.</summary>
public sealed record EnhancementEnvironment
{
    public EnhancementHardwareInfo? Hardware { get; init; }
    public bool HardwareVerified { get; init; }
    public IReadOnlyList<string> ActivePatchReceiptIds { get; init; } = [];
    public bool ActivePatchSetVerified { get; init; }
}

public sealed record EnhancementApplicationResult(EnhancementApplicationReceipt Receipt,
    IReadOnlyList<SettingState> EffectiveSettings)
{
    public bool GameplayVerified => false;
    public bool OfficialUpgradeParityVerified => false;
}

/// <summary>Applies exact-build scalar and typed native patch transactions; unsupported dynamic adapters remain explicit.</summary>
public sealed class EnhancementCoordinator
{
    private sealed class SessionState
    {
        internal string Id { get; } = Guid.NewGuid().ToString("N");
        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal List<EnhancementApplicationReceipt> Applied { get; } = [];
    }
    private static readonly ConditionalWeakTable<NxRuntime, SessionState> Sessions = new();
    private readonly NxRuntime _runtime;
    private readonly EnhancementStore _store;
    private readonly EnhancementEnvironment _environment;
    private readonly SessionState _session;

    public EnhancementCoordinator(NxRuntime runtime, EnhancementStore store, EnhancementEnvironment? environment = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        var supplied = environment ?? new EnhancementEnvironment();
        _environment = supplied with { ActivePatchReceiptIds = supplied.ActivePatchReceiptIds.ToArray() };
        _session = Sessions.GetOrCreateValue(runtime);
    }

    public string SessionId => _session.Id;
    public EnhancementContext CurrentContext
    {
        get
        {
            string[] active;
            lock (_session.Applied) active = _session.Applied.Select(x => x.Profile.ProfileId).ToArray();
            var content = _runtime.LoadedContent;
            return new EnhancementContext
            {
                TitleId = content?.TitleId, BuildId = content?.BuildId,
                BackendId = _runtime.Backend.BackendId, BackendRevision = _runtime.Backend.BackendRevision,
                BackendAdapterVersion = _runtime.Backend.AdapterVersion,
                BackendArtifactManifestSha256 = _runtime.BackendArtifactManifestSha256,
                IdentityVerified = FullHex(content?.TitleId, 16) && FullHex(content?.BuildId, 64),
                DisplayVersion = content?.DisplayVersion,
                GamePatchAdapters = new[] { "game_patch.module_bytes_v1", "game_patch.prepare", "game_patch.apply", "game_patch.get", "game_patch.rollback" }.All(_runtime.Supports) ? ["module_bytes_v1"] : [],
                Hardware = _environment.Hardware, HardwareVerified = _environment.HardwareVerified,
                ActiveProfileIds = active, ActivePatchReceiptIds = _environment.ActivePatchReceiptIds
            };
        }
    }

    public EnhancementPlan Evaluate(EnhancementSelection selection) => EvaluateBound(selection, CurrentContext);

    private EnhancementPlan EvaluateBound(EnhancementSelection selection, EnhancementContext context)
    {
        var plan = _store.Evaluate(selection.ProfileId, selection.Version, context, selection.SelectedChangeIds, selection.AllowExperimental);
        var issues = plan.Issues.ToList();
        bool nativePatchGuard = plan.Changes.Any(change => change.GamePatch is not null) &&
            new[] { "game_patch.prepare", "game_patch.apply", "game_patch.rollback", "game_patch.get", "game_patch.module_bytes_v1" }.All(_runtime.Supports);
        if (!_environment.ActivePatchSetVerified && !nativePatchGuard)
            issues.Add(new("patch_state_unknown", "Verify the active patch/mod set, including a deliberately verified unmodified baseline, before applying a profile."));
        lock (_session.Applied)
        {
            var occupied = _session.Applied.SelectMany(x => x.OriginalSettings).Select(x => x.SettingId).ToHashSet(StringComparer.Ordinal);
            if (plan.Changes.Any(x => x.Setting is { } setting && occupied.Contains(setting.SettingId)))
                issues.Add(new("active_setting_conflict", "Another active enhancement owns one of these setting values. Roll it back first."));
            if (_session.Applied.Any(receipt => receipt.NativePatches is { Count: > 0 }) && plan.Changes.Any(change => change.GamePatch is not null))
                issues.Add(new("active_game_patch_conflict", "A native timing repair already owns this loaded module. Restore the original session first."));
        }
        return plan with { Issues = issues };
    }

    public async Task<EnhancementApplicationResult> ApplyAsync(EnhancementSelection selection, CancellationToken ct = default)
    {
        await _session.Gate.WaitAsync(ct);
        try { return await ApplyBoundAsync(selection, CurrentContext, false, ct); }
        finally { _session.Gate.Release(); }
    }

    internal async Task<EnhancementApplicationResult> PrepareBeforeLoadAsync(
        EnhancementSelection selection, EnhancementContext verifiedContent, CancellationToken ct)
    {
        if (_runtime.State != RuntimeState.Initialized || !verifiedContent.IdentityVerified)
            throw new NxException("enhancement_identity_mismatch", "The pre-load preparation does not match this fresh runtime.");
        RequireSameBackendIdentity(verifiedContent, CurrentContext);
        await _session.Gate.WaitAsync(ct);
        try { return await ApplyBoundAsync(selection, verifiedContent, true, ct); }
        finally { _session.Gate.Release(); }
    }

    private async Task<EnhancementApplicationResult> ApplyBoundAsync(EnhancementSelection selection,
        EnhancementContext context, bool deferLoadedIdentityCheck, CancellationToken ct)
    {
        var plan = EvaluateBound(selection, context);
        RequireApplicable(plan);
        var patches = plan.Changes.Where(change => change.GamePatch is not null).Select(change => change.GamePatch!).ToArray();
        var changes = plan.Changes.Where(x => x.Setting is not null).Select(x => new RuntimeSettingChange(x.Setting!.SettingId, x.Setting.Value)).ToArray();
        await using var transaction = await _runtime.BeginSettingsTransactionAsync(changes, ct, allowEmpty: patches.Length != 0);
        await transaction.RequirePatchPolicyAsync(patches, ct);
        if (!deferLoadedIdentityCheck)
            foreach (var patch in patches) await transaction.PreparePatchAsync(patch, ct);
        // Durable originals precede the first core mutation. The SDK gate remains held.
        var receipt = _store.Prepare(plan, SessionId, transaction.Originals.Select(x =>
            new EnhancementOriginalSetting(x.Id, x.Effective!.Value, x.Persisted)).ToArray(), transaction.PatchAcknowledgements);
        try
        {
            await transaction.ApplyAsync(ct);
            if (!deferLoadedIdentityCheck)
            {
                if (patches.Length != 0)
                {
                    await transaction.ApplyPatchesAsync(ct);
                    receipt = _store.RecordNativePatches(receipt.ReceiptId, transaction.PatchAcknowledgements);
                }
                receipt = _store.RecordOutcome(receipt.ReceiptId, EnhancementReceiptStatus.Applied,
                    Evidence(transaction.Effective, "Loaded title/update/main build matched. Scalar settings and selected typed native patches acknowledged and read back; executable caches invalidated. This does not qualify game timing or rendering."));
                lock (_session.Applied) _session.Applied.Add(receipt);
            }
            transaction.Complete();
            return new(receipt, transaction.Effective);
        }
        catch
        {
            var patchesRestored = await transaction.RestorePatchesAsync();
            var settingsRestored = await transaction.RestoreAsync();
            var restored = patchesRestored && settingsRestored;
            _store.RecordOutcome(receipt.ReceiptId, restored ? EnhancementReceiptStatus.RolledBack : EnhancementReceiptStatus.RecoveryRequired,
                restored ? "Application failed; touched native bytes and scalar settings were verified at their original values. Persisted preferences were not changed."
                         : "Application or restoration was uncertain. Dispose this runtime and inspect the original-value receipt before recovery.");
            throw;
        }
    }

    internal async Task<EnhancementApplicationResult> ConfirmLoadedAsync(EnhancementApplicationResult prepared, CancellationToken ct)
    {
        RequireSameIdentity(prepared.Receipt.Context, CurrentContext);
        var selected = prepared.Receipt.Profile.Changes.Where(x => prepared.Receipt.SelectedChangeIds.Contains(x.ChangeId)).ToArray();
        var effective = await ReadEffectiveAsync(_runtime, selected.Where(x => x.Setting is not null).Select(x => x.Setting!).ToArray(), ct);
        var patches = selected.Where(change => change.GamePatch is not null).Select(change => change.GamePatch!).ToArray();
        if (patches.Length != 0)
        {
            await using var transaction = await _runtime.BeginSettingsTransactionAsync([], ct, allowEmpty: true);
            try
            {
                await transaction.RequirePatchPolicyAsync(patches, ct);
                foreach (var patch in patches) await transaction.PreparePatchAsync(patch, ct);
                _store.RecordNativePatches(prepared.Receipt.ReceiptId, transaction.PatchAcknowledgements);
                await transaction.ApplyAsync(ct);
                await transaction.ApplyPatchesAsync(ct);
                _store.RecordNativePatches(prepared.Receipt.ReceiptId, transaction.PatchAcknowledgements);
                transaction.Complete();
            }
            catch
            {
                if (!await transaction.RestorePatchesAsync())
                    _store.RecordOutcome(prepared.Receipt.ReceiptId, EnhancementReceiptStatus.RecoveryRequired,
                        "Native repair application/restoration was uncertain. The worker is faulted and must be disposed before recovery.");
                throw;
            }
        }
        var receipt = _store.RecordOutcome(prepared.Receipt.ReceiptId, EnhancementReceiptStatus.Applied,
            Evidence(effective, "Exact title/update/main-build/core identity, effective settings and selected native byte repairs verified after the final never-started load. No gameplay/parity assertion."));
        lock (_session.Applied) _session.Applied.Add(receipt);
        return new(receipt, effective);
    }

    public async Task<EnhancementApplicationResult> RollbackAsync(string receiptId, CancellationToken ct = default)
    {
        await _session.Gate.WaitAsync(ct);
        try
        {
            EnhancementApplicationReceipt active;
            lock (_session.Applied)
                active = _session.Applied.LastOrDefault() ?? throw new NxException("enhancement_not_active", "No profile is active on this runtime.");
            if (active.ReceiptId != receiptId) throw new NxException("enhancement_rollback_order", "Rollback must use this runtime's most recent active receipt.");
            var receipt = _store.GetReceipt(receiptId);
            if (receipt.SessionId != SessionId || receipt.Status != EnhancementReceiptStatus.Applied)
                throw new NxException("enhancement_receipt_mismatch", "Receipt is not an applied transaction for this runtime.");
            RequireSameIdentity(receipt.Context, CurrentContext);
            if (receipt.NativePatches is { Count: > 0 } && _runtime.State != RuntimeState.Loaded)
                throw new NxException("game_patch_restart_required", "Executable repair rollback after first Start requires a fresh original session; live code replacement is not supported by module_bytes_v1.");
            var changes = receipt.OriginalSettings.Select(x => new RuntimeSettingChange(x.SettingId, x.Effective)).ToArray();
            await using var transaction = await _runtime.BeginSettingsTransactionAsync(changes, ct, allowEmpty: receipt.NativePatches is { Count: > 0 });
            var expected = receipt.Profile.Changes.Where(x => receipt.SelectedChangeIds.Contains(x.ChangeId) && x.Setting is not null).Select(x => x.Setting!).ToDictionary(x => x.SettingId);
            if (transaction.Originals.Any(x => !JsonElement.DeepEquals(x.Effective!.Value, expected[x.Id].Value)))
                throw new NxException("enhancement_modified", "A setting changed after profile application. Review it before replacing it with the saved original.");
            try
            {
                await transaction.ApplyAsync(ct);
                foreach (var patch in (receipt.NativePatches ?? []).Reverse())
                {
                    var recipe = receipt.Profile.Changes.Single(change => change.GamePatch?.RecipeId == patch.RecipeId).GamePatch!;
                    await transaction.RollbackExistingPatchAsync(recipe, patch, ct);
                }
                receipt = _store.RecordOutcome(receiptId, EnhancementReceiptStatus.RolledBack,
                    Evidence(transaction.Effective, "Original effective values and selected native bytes restored and read back. Executable caches invalidated; persisted preferences retained. Game saves are not rolled back."));
                transaction.Complete();
                lock (_session.Applied) _session.Applied.RemoveAt(_session.Applied.Count - 1);
                return new(receipt, transaction.Effective);
            }
            catch
            {
                var restored = await transaction.RestoreAsync();
                if (receipt.NativePatches is { Count: > 0 }) transaction.FaultOwner();
                _store.RecordOutcome(receiptId, EnhancementReceiptStatus.RecoveryRequired,
                    restored ? "Rollback failed; the prior enhanced values were recovered. Original-value rollback remains unconfirmed."
                             : "Rollback and recovery were uncertain; stop/dispose and review the original-value receipt.");
                throw;
            }
        }
        finally { _session.Gate.Release(); }
    }

    internal static void RequireApplicable(EnhancementPlan plan)
    {
        if (!plan.Applicable) throw new NxException("enhancement_not_applicable", "Profile is blocked: " + string.Join(", ", plan.Issues.Select(x => x.Code).Distinct()));
    }
    internal static void RequireSameIdentity(EnhancementContext expected, EnhancementContext actual)
    {
        RequireSameBackendIdentity(expected, actual);
        if (!actual.IdentityVerified || !string.Equals(expected.TitleId, actual.TitleId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(expected.BuildId, actual.BuildId, StringComparison.OrdinalIgnoreCase) ||
            expected.DisplayVersion is not null && expected.DisplayVersion != actual.DisplayVersion)
            throw new NxException("enhancement_identity_mismatch", "The loaded title or full main build ID changed; the profile cannot apply.");
    }
    internal static void RequireSameBackendIdentity(EnhancementContext expected, EnhancementContext actual)
    {
        if (expected.BackendId != actual.BackendId || !FullHex(expected.BackendRevision, 40) ||
            !string.Equals(expected.BackendRevision, actual.BackendRevision, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(expected.BackendAdapterVersion) ||
            !string.Equals(expected.BackendAdapterVersion, actual.BackendAdapterVersion, StringComparison.Ordinal) ||
            !FullHex(expected.BackendArtifactManifestSha256, 64) ||
            !string.Equals(expected.BackendArtifactManifestSha256, actual.BackendArtifactManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new NxException("enhancement_identity_mismatch", "The backend source, exact NX adapter version, or SDK-verified installation manifest changed or is unavailable.");
    }
    internal static async Task<IReadOnlyList<SettingState>> ReadEffectiveAsync(NxRuntime runtime,
        IReadOnlyList<EnhancementSettingChange> expected, CancellationToken ct)
    {
        var observed = new List<SettingState>();
        foreach (var change in expected)
        {
            var state = await runtime.GetSettingAsync(change.SettingId, ct);
            if (!state.Applied || !state.EffectiveKnown || state.RequiresRestart || state.Effective is not { } actual ||
                !JsonElement.DeepEquals(actual, change.Value))
                throw new NxException("effective_state_mismatch", "The final loaded runtime did not retain the requested setting value.");
            observed.Add(state);
        }
        return observed;
    }
    private static bool FullHex(string? value, int length) => value is not null && value.Length == length && value.All(Uri.IsHexDigit) && value.Any(x => x != '0');
    private static string Evidence(IReadOnlyList<SettingState> states, string scope)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(states);
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(json));
        return scope + " Verified settings: " + states.Count + "; effective-state snapshot SHA256: " + hash +
            ". Every selected value equaled the profile's recorded desired value; values are returned to the caller in EffectiveSettings.";
    }
}
