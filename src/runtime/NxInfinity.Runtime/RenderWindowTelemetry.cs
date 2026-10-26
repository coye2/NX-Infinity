using System.Text.Json;

namespace NxInfinity.Runtime;

/// <summary>Native window observations. A requested fullscreen value alone is not confirmation.</summary>
public static class RenderWindowTelemetry
{
    public static RenderWindowState ReadChangedEvent(RuntimeEvent observation)
    {
        if (observation.Name != "window.changed" || observation.Data.ValueKind != JsonValueKind.Object)
            throw new NxException("invalid_argument", "A native window.changed observation is required.");
        var data = observation.Data;
        if (data.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != data.EnumerateObject().Count() ||
            !data.TryGetProperty("width", out var width) || width.ValueKind != JsonValueKind.Number || !width.TryGetInt32(out _) ||
            !data.TryGetProperty("height", out var height) || height.ValueKind != JsonValueKind.Number || !height.TryGetInt32(out _) ||
            !data.TryGetProperty("fullscreen", out var fullscreen) || fullscreen.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !data.TryGetProperty("visible", out var visible) || visible.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new NxException("protocol_mismatch", "The native window observation is incomplete or invalid.");
        return Validate(NxJson.Read<RenderWindowState>(data));
    }

    internal static RenderWindowState Validate(RenderWindowState state)
    {
        if (state.Width is < 0 or > 16384 || state.Height is < 0 or > 16384 ||
            state.PixelWidth is < 0 or > 32768 || state.PixelHeight is < 0 or > 32768 ||
            state.FullscreenEffectiveSource is { } source && (source.Length > 128 || source.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '.' or '-'))) ||
            state.FullscreenConfirmed == true && state.FullscreenRequested is { } requested && requested != state.Fullscreen)
            throw new NxException("protocol_mismatch", "The core returned an invalid or contradictory window state.");
        return state;
    }
}
