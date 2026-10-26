using System.Text.Json;

namespace NxInfinity.Runtime;

/// <summary>Strict, bounded parsing of the native interval contract. There is no FPS fallback.</summary>
public static class PerformanceTelemetry
{
    public const int MaximumPacketIntervals = 512;
    public const double MaximumIntervalMs = 86_400_000;
    public const string MeasurementScope =
        "CPU-side native presentation intervals; not hardware scanout, GPU execution time, or gameplay-speed qualification. A null renderer does not establish physical presentation.";

    public static PerformanceTelemetryResult Read(TelemetrySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.BackendMetrics is null || !snapshot.BackendMetrics.TryGetValue("performance", out var value))
            return Unavailable("timing_unsupported", "This telemetry does not expose native present intervals; FPS samples are not substituted.");
        try
        {
            if (value.ValueKind != JsonValueKind.Object) return Invalid("timing_packet_invalid");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
                if (names.Count >= 32 || !names.Add(property.Name)) return Invalid("timing_packet_invalid");
            if (!Int64(value, "schemaVersion", out var version)) return Invalid("timing_packet_invalid");
            if (version != 1) return Unavailable("timing_schema_unsupported", "The native timing schema version requires SDK support.");
            if (!Token(value, "timingSource", out var source) || source is not ("present_callback" or "present_call_return"))
                return Invalid("timing_source_invalid");
            if (!Int64(value, "segmentId", out var segment) || segment < 0 ||
                !Int64(value, "sequence", out var sequence) || sequence < 0 ||
                !Int64(value, "totalPresentedFrames", out var frames) || frames < 0 || sequence > frames ||
                !value.TryGetProperty("intervalsMs", out var intervals) || intervals.ValueKind != JsonValueKind.Array ||
                intervals.GetArrayLength() > MaximumPacketIntervals || intervals.GetArrayLength() > sequence)
                return Invalid("timing_sequence_invalid");
            var samples = new double[intervals.GetArrayLength()];
            int index = 0;
            foreach (var interval in intervals.EnumerateArray())
            {
                if (interval.ValueKind != JsonValueKind.Number || !interval.TryGetDouble(out double number) ||
                    !double.IsFinite(number) || number <= 0 || number > MaximumIntervalMs)
                    return Invalid("timing_interval_invalid");
                samples[index++] = number;
            }
            if (!OptionalNumber(value, "lastPresentAgeMs", out double? age) ||
                !OptionalBoolean(value, "shaderCacheEnabled", out bool? shaderCache) ||
                !OptionalBoolean(value, "cpuCacheEnabled", out bool? cpuCache) ||
                !Token(value, "rendererThreading", out var threading) || !Token(value, "api", out var api))
                return Invalid("timing_metadata_invalid");
            string? measurementPoint = null;
            if (value.TryGetProperty("measurementPoint", out var point) && point.ValueKind != JsonValueKind.Null &&
                !Token(value, "measurementPoint", out measurementPoint)) return Invalid("timing_metadata_invalid");
            return new PerformanceTelemetryResult
            {
                Availability = PerformanceAvailability.Available, Code = "native_intervals", Message = MeasurementScope,
                Packet = new NativePresentTiming
                {
                    SchemaVersion = 1, TimingSource = source!, MeasurementPoint = measurementPoint, SegmentId = segment, Sequence = sequence,
                    IntervalsMs = Array.AsReadOnly(samples), TotalPresentedFrames = frames, LastPresentAgeMs = age,
                    ShaderCacheEnabled = shaderCache, CpuCacheEnabled = cpuCache,
                    RendererThreading = threading!, Api = api!, MeasurementScope = MeasurementScope
                }
            };
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException or FormatException or OverflowException)
        {
            return Invalid("timing_packet_invalid");
        }
    }

    internal static PerformanceTelemetryResult Invalid(string code) => new()
    {
        Availability = PerformanceAvailability.Invalid, Code = code,
        Message = "The native timing packet is inconsistent or outside its bounded contract. Its intervals were not accepted; coverage may be incomplete."
    };

    private static PerformanceTelemetryResult Unavailable(string code, string message) => new()
    { Availability = PerformanceAvailability.Unsupported, Code = code, Message = message };

    private static bool Int64(JsonElement value, string name, out long result)
    {
        result = 0;
        return value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out result);
    }

    private static bool Token(JsonElement value, string name, out string? result)
    {
        result = null;
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return false;
        result = property.GetString();
        return result is { Length: > 0 and <= 64 } && result.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');
    }

    private static bool OptionalBoolean(JsonElement value, string name, out bool? result)
    {
        result = null;
        if (!value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return true;
        if (property.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        result = property.GetBoolean(); return true;
    }

    private static bool OptionalNumber(JsonElement value, string name, out double? result)
    {
        result = null;
        if (!value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return true;
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetDouble(out double number) ||
            !double.IsFinite(number) || number < 0 || number > MaximumIntervalMs) return false;
        result = number; return true;
    }
}
