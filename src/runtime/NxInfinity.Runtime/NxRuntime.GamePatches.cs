namespace NxInfinity.Runtime;

public sealed partial class NxRuntime
{
    public async Task<FrameRatePolicyState> GetFrameRatePolicyAsync(CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken);
        try
        {
            Require(RuntimeState.Initialized, RuntimeState.Loaded, RuntimeState.Running, RuntimeState.Paused);
            return FrameRatePolicyTelemetry.Validate(NxJson.Read<FrameRatePolicyState>(await CallSupportedAsync("frame_rate.get", null, cancellationToken)));
        }
        finally { _operations.Release(); }
    }

    public async Task<FrameRatePolicyState> SetFrameRatePolicyAsync(int target, CancellationToken cancellationToken = default)
    {
        if (target is not (0 or 60)) throw new NxException("invalid_argument", "Choose the original policy or a requested 60 FPS policy.");
        await _operations.WaitAsync(cancellationToken);
        try
        {
            Require(RuntimeState.Initialized, RuntimeState.Loaded, RuntimeState.Paused);
            var result = FrameRatePolicyTelemetry.Validate(NxJson.Read<FrameRatePolicyState>(await CallSupportedAsync("frame_rate.set", new { target }, cancellationToken)));
            if (result.RequestedTarget != target) throw new NxException("protocol_mismatch", "The native frame policy did not retain the requested target.");
            return result;
        }
        finally { _operations.Release(); }
    }

    internal sealed partial class SettingsTransactionLease
    {
        private readonly List<GamePatchRequest> _patchRequests = [];
        private bool _patchApplyAttempted;
        private readonly List<GamePatchAcknowledgement> _patchAcknowledgements = [];
        private bool _patchRestoreAttempted, _patchRestoreSucceeded;
        internal IReadOnlyList<GamePatchAcknowledgement> PatchAcknowledgements => _patchAcknowledgements.ToArray();
        internal void FaultOwner() => _owner.State = RuntimeState.Faulted;

        internal async Task RequirePatchPolicyAsync(IEnumerable<GamePatchRecipe> recipes, CancellationToken ct)
        {
            if (_disposed || _completed) throw new NxException("invalid_state", "The transaction is closed.");
            if (!recipes.Any(recipe => recipe.TargetFrameRate == 60)) return;
            var policy = FrameRatePolicyTelemetry.Validate(NxJson.Read<FrameRatePolicyState>(
                await _owner.CallSupportedAsync("frame_rate.get", null, ct)));
            if (policy.RequestedTarget != 60)
                throw new NxException("frame_rate_target_required", "This profile declares a 60 FPS timing repair. Request the 60 FPS policy before applying it; no settings or executable bytes were changed.");
        }

        internal async Task<GamePatchAcknowledgement> PreparePatchAsync(GamePatchRecipe supplied, CancellationToken ct)
        {
            if (_disposed || _completed) throw new NxException("invalid_state", "The transaction is closed.");
            _owner.Require(RuntimeState.Loaded);
            var recipe = GamePatchValidation.Canonicalize(supplied);
            var content = _owner.LoadedContent;
            if (Volatile.Read(ref _owner._everStarted) != 0)
                throw new NxException("game_patch_restart_required", "Static executable repairs require a fresh worker before its first guest start.");
            if (!new[] { "game_patch." + recipe.AdapterId, "game_patch.prepare", "game_patch.apply", "game_patch.get", "game_patch.rollback" }.All(_owner.Supports))
                throw new NxException("game_patch_adapter_unavailable", "This exact core does not implement the requested typed patch adapter.");
            if (_owner.Backend.BackendId != recipe.BackendId ||
                !string.Equals(_owner.Backend.BackendRevision, recipe.BackendRevision, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(content?.TitleId, recipe.TitleId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(content?.BuildId, recipe.BuildId, StringComparison.OrdinalIgnoreCase) || content?.DisplayVersion != recipe.DisplayVersion)
                throw new NxException("game_patch_identity_mismatch", "The loaded title, update, main executable or core differs from the exact patch recipe.");
            var request = new GamePatchRequest(Guid.NewGuid().ToString("N"), GamePatchValidation.Fingerprint(recipe), recipe);
            // Prepare is read-only. A clean native preflight rejection must keep
            // the original worker usable, without trying to undo a nonexistent ID.
            var response = await _owner.CallSupportedAsync("game_patch.prepare", request, ct);
            _patchRequests.Add(request);
            var ack = NxJson.Read<GamePatchAcknowledgement>(response);
            GamePatchValidation.ValidateAcknowledgement(request, ack, "prepared");
            _patchAcknowledgements.Add(ack);
            return ack;
        }

        internal async Task ApplyPatchesAsync(CancellationToken ct)
        {
            if (_disposed || _completed || _patchRestoreAttempted || _patchApplyAttempted) throw new NxException("invalid_state", "The patch transaction is closed or already applied.");
            _patchApplyAttempted = true;
            _owner.Require(RuntimeState.Loaded);
            await RequirePatchPolicyAsync(_patchRequests.Select(request => request.Recipe), ct);
            for (int i = 0; i < _patchRequests.Count; i++)
            {
                var request = _patchRequests[i];
                var ack = NxJson.Read<GamePatchAcknowledgement>(await _owner.CallSupportedAsync("game_patch.apply", new { transactionId = request.TransactionId }, ct));
                GamePatchValidation.ValidateAcknowledgement(request, ack, "applied");
                var observed = NxJson.Read<GamePatchAcknowledgement>(await _owner.CallSupportedAsync("game_patch.get", new { transactionId = request.TransactionId }, ct));
                GamePatchValidation.ValidateAcknowledgement(request, observed, "applied");
                if (observed.Generation != ack.Generation) throw new NxException("game_patch_acknowledgement_mismatch", "The native patch generation changed before readback.");
                _patchAcknowledgements[i] = observed;
            }
        }

        internal async Task<bool> RestorePatchesAsync()
        {
            if (_patchRestoreAttempted) return _patchRestoreSucceeded;
            _patchRestoreAttempted = true;
            bool restored = true;
            foreach (var request in _patchRequests.AsEnumerable().Reverse())
            {
                try
                {
                    var ack = NxJson.Read<GamePatchAcknowledgement>(await _owner.CallSupportedAsync("game_patch.rollback", new { transactionId = request.TransactionId }, CancellationToken.None));
                    GamePatchValidation.ValidateAcknowledgement(request, ack, "rolled_back");
                    var observed = NxJson.Read<GamePatchAcknowledgement>(await _owner.CallSupportedAsync("game_patch.get", new { transactionId = request.TransactionId }, CancellationToken.None));
                    GamePatchValidation.ValidateAcknowledgement(request, observed, "rolled_back");
                    if (observed.Generation != ack.Generation) throw new NxException("game_patch_acknowledgement_mismatch", "Native restoration changed before original-byte readback.");
                }
                catch { restored = false; }
            }
            if (!restored) _owner.State = RuntimeState.Faulted;
            return _patchRestoreSucceeded = restored;
        }

        internal async Task RollbackExistingPatchAsync(GamePatchRecipe recipe, GamePatchAcknowledgement applied, CancellationToken ct)
        {
            _owner.Require(RuntimeState.Loaded);
            if (Volatile.Read(ref _owner._everStarted) != 0)
                throw new NxException("game_patch_restart_required", "Static executable rollback after guest execution needs a fresh original worker.");
            var request = new GamePatchRequest(applied.TransactionId, applied.RecipeSha256, GamePatchValidation.Canonicalize(recipe));
            var current = NxJson.Read<GamePatchAcknowledgement>(await _owner.CallSupportedAsync("game_patch.get", new { transactionId = applied.TransactionId }, ct));
            GamePatchValidation.ValidateAcknowledgement(request, current, "applied");
            if (current.Generation != applied.Generation)
                throw new NxException("game_patch_acknowledgement_mismatch", "The native patch generation differs from the saved application receipt.");
            var ack = NxJson.Read<GamePatchAcknowledgement>(await _owner.CallSupportedAsync("game_patch.rollback", new { transactionId = applied.TransactionId }, ct));
            GamePatchValidation.ValidateAcknowledgement(request, ack, "rolled_back");
            var observed = NxJson.Read<GamePatchAcknowledgement>(await _owner.CallSupportedAsync("game_patch.get", new { transactionId = applied.TransactionId }, ct));
            GamePatchValidation.ValidateAcknowledgement(request, observed, "rolled_back");
            if (observed.Generation != ack.Generation)
                throw new NxException("game_patch_acknowledgement_mismatch", "Native rollback changed before original-byte readback.");
        }
    }
}
