using System.Text.Json;

namespace NxInfinity.Runtime;

/// <summary>Reads the native policy and independent frame observations without treating a requested target as a measurement.</summary>
public static class FrameRatePolicyTelemetry
{
    /// <summary>Returns null when this backend supplies no frame-rate observation. Malformed observations fail closed.</summary>
    public static FrameRatePolicyState? Read(TelemetrySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.BackendMetrics is null)
            throw new NxException("protocol_mismatch", "Native telemetry metrics are absent.");
        if (!snapshot.BackendMetrics.TryGetValue("frameRate", out var value)) return null;
        try { return Validate(NxJson.Read<FrameRatePolicyState>(value)); }
        catch (JsonException e) { throw new NxException("protocol_mismatch", "Native frame-rate telemetry is malformed.", e); }
    }

    internal static FrameRatePolicyState Validate(FrameRatePolicyState state)
    {
        if (state is null || state.SchemaVersion != 1 || state.RequestedTarget is not (0 or 60) ||
            state.EffectivePolicy is not ("original" or "patched_60") || !state.GuestClockUnchanged ||
            string.IsNullOrEmpty(state.ReasonCode) || state.ReasonCode.Length > 128 ||
            string.IsNullOrEmpty(state.MeasurementScope) || state.MeasurementScope.Length > 256 ||
            state.PatchTransactionId is not null && !Guid.TryParseExact(state.PatchTransactionId, "N", out _) ||
            state.EffectivePolicy == "patched_60" && (state.RequestedTarget != 60 || state.EffectiveTarget != 60 ||
                state.PatchTransactionId is null || !FullHex(state.TitleId, 16) || !FullHex(state.BuildId, 64)) ||
            state.EffectivePolicy == "original" && state.EffectiveTarget is not null ||
            state.GuestQueuedFrames < 0 || state.ReplacedBeforeAcquire < 0 || state.AcquiredGameBuffers < 0 ||
            state.CompositedGameFrames < 0 || state.AppletAcquisitions < 0 || state.HostPresentReturns < 0 ||
            state.HostPresents < 0 || state.HostPresentFailures < 0 || state.HostPresents > state.HostPresentReturns || state.HostPresentFailures > state.HostPresentReturns ||
            state.CorrelatedGamePresents < 0 || state.UncorrelatedPresents < 0 || state.CorrelationOverflows < 0 ||
            state.CorrelatedGamePresents > state.HostPresents ||
            (state.MeasurementIntervalMs is null) != (state.MeasuredGameFrames is null) ||
            state.MeasurementIntervalMs is { } interval && (!double.IsFinite(interval) || interval <= 0) ||
            state.MeasuredGameFrames is < 0 ||
            state.MeasuredGameFps is { } fps && (!double.IsFinite(fps) || fps < 0 ||
                state.MeasurementIntervalMs is null || state.MeasuredGameFrames is null || state.MeasurementScope == "unavailable" ||
                Math.Abs(fps - state.MeasuredGameFrames.Value * 1000.0 / state.MeasurementIntervalMs.Value) > .001))
            throw new NxException("protocol_mismatch", "Native frame policy or independent frame observations are inconsistent.");
        return state;
    }

    private static bool FullHex(string? value, int length) => value?.Length == length && value.All(Uri.IsHexDigit) && value.Any(c => c != '0');
}
