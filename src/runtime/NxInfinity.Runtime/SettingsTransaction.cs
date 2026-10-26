using System.Text.Json;

namespace NxInfinity.Runtime;

internal sealed record RuntimeSettingChange(string Id, JsonElement Value);

public sealed partial class NxRuntime
{
    internal async Task<SettingsTransactionLease> BeginSettingsTransactionAsync(
        IReadOnlyList<RuntimeSettingChange> changes, CancellationToken ct, bool allowEmpty = false)
    {
        await _operations.WaitAsync(ct);
        try
        {
            Require(RuntimeState.Initialized, RuntimeState.Loaded, RuntimeState.Paused);
            if (changes.Count > 128 || changes.Count == 0 && !allowEmpty || changes.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != changes.Count)
                throw new NxException("invalid_argument", "Use 1..128 distinct setting changes per enhancement transaction.");
            if (changes.Count != 0) await EnsureSchemasAsync(ct);
            foreach (var change in changes)
            {
                if (!_schema!.TryGetValue(change.Id, out var schema))
                    throw new NxException("unsupported", "An enhancement setting is not exposed by this exact adapter.");
                schema.Validate(change.Value);
                if (schema.Apply == "before_initialize")
                    throw new NxException("restart_required", "Constructor-bound settings require a new explicitly configured runtime; no values were changed.");
                if (schema.Apply is "before_load" or "restart" && State != RuntimeState.Initialized)
                    throw new NxException("restart_required", "This profile must be applied before loading in a fresh runtime; no values were changed.");
                if (schema.Apply == "paused" && State == RuntimeState.Running)
                    throw new NxException("invalid_state", "Pause before applying this profile.");
            }
            var originals = new List<SettingState>();
            foreach (var change in changes)
            {
                var state = await ReadSettingStateUnlockedAsync(change.Id, ct);
                if (!state.EffectiveKnown || state.Effective is null)
                    throw new NxException("effective_state_unknown", "The backend cannot supply a trustworthy original value for rollback.");
                if (state.RequiresRestart || state.Requested is { } requested && !JsonElement.DeepEquals(requested, state.Effective.Value))
                    throw new NxException("pending_setting", "Resolve existing pending setting changes before starting an enhancement transaction.");
                _schema![change.Id].Validate(state.Effective.Value);
                originals.Add(state);
            }
            return new SettingsTransactionLease(this, changes.Select(x => x with { Value = x.Value.Clone() }).ToArray(), originals);
        }
        catch { _operations.Release(); throw; }
    }

    internal sealed partial class SettingsTransactionLease : IAsyncDisposable
    {
        private readonly NxRuntime _owner;
        private readonly IReadOnlyList<RuntimeSettingChange> _changes;
        private readonly List<string> _touched = [];
        private bool _disposed;
        private bool _attempted;
        private bool _completed;
        private bool _restoreAttempted;
        private bool _restoreSucceeded;
        internal SettingsTransactionLease(NxRuntime owner, IReadOnlyList<RuntimeSettingChange> changes, IReadOnlyList<SettingState> originals)
        { _owner = owner; _changes = changes; Originals = originals; }
        internal IReadOnlyList<SettingState> Originals { get; }
        internal IReadOnlyList<SettingState> Effective { get; private set; } = [];

        internal async Task ApplyAsync(CancellationToken ct)
        {
            if (_disposed || _attempted) throw new NxException("invalid_state", "The transaction is closed or already used.");
            _attempted = true;
            _owner.Require(RuntimeState.Initialized, RuntimeState.Loaded, RuntimeState.Paused);
            var observed = new List<SettingState>();
            foreach (var change in _changes)
            {
                _touched.Add(change.Id); // A failed acknowledgment can still follow a mutation.
                var acknowledgment = NxJson.Read<SettingState>(await _owner.CallSupportedAsync("settings.set", new { id = change.Id, value = change.Value }, ct));
                ValidateAcknowledgment(change.Id, change.Value, acknowledgment);
                var readback = NxJson.Read<SettingState>(await _owner.CallSupportedAsync("settings.get", new { id = change.Id }, ct));
                ValidateAcknowledgment(change.Id, change.Value, readback);
                observed.Add(readback with { Persisted = Originals.Single(x => x.Id == change.Id).Persisted });
            }
            Effective = observed;
        }

        private static void ValidateAcknowledgment(string id, JsonElement expected, SettingState state)
        {
            ValidateSettingResult(id, state);
            if (!state.Applied || !state.EffectiveKnown || state.RequiresRestart || state.Effective is not { } effective ||
                !JsonElement.DeepEquals(expected, effective))
                throw new NxException("effective_state_mismatch", "The core did not confirm the complete requested enhancement value.");
        }

        internal async Task<bool> RestoreAsync()
        {
            if (_disposed) throw new NxException("invalid_state", "The settings lease is closed.");
            if (_restoreAttempted) return _restoreSucceeded;
            _restoreAttempted = true;
            bool restored = true;
            foreach (var id in _touched.AsEnumerable().Reverse())
            {
                var original = Originals.Single(x => x.Id == id);
                try
                {
                    var acknowledgment = NxJson.Read<SettingState>(await _owner.CallSupportedAsync("settings.set",
                        new { id, value = original.Effective!.Value }, CancellationToken.None));
                    ValidateAcknowledgment(id, original.Effective.Value, acknowledgment);
                    var readback = NxJson.Read<SettingState>(await _owner.CallSupportedAsync("settings.get", new { id }, CancellationToken.None));
                    ValidateAcknowledgment(id, original.Effective.Value, readback);
                }
                catch { restored = false; }
            }
            if (!restored) _owner.State = RuntimeState.Faulted;
            _restoreSucceeded = restored;
            return restored;
        }

        internal void Complete()
        {
            if (_disposed || _restoreAttempted || _patchRestoreAttempted || !_attempted || Effective.Count != _changes.Count ||
                _patchAcknowledgements.Count != _patchRequests.Count || _patchAcknowledgements.Any(ack => ack.Status != "applied"))
                throw new NxException("invalid_state", "Only a fully verified transaction can be completed.");
            _completed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            try
            {
                if (!_completed && _patchRequests.Count != 0) await RestorePatchesAsync();
                if (!_completed && _touched.Count != 0) await RestoreAsync();
            }
            finally { _disposed = true; _owner._operations.Release(); }
        }
    }
}
