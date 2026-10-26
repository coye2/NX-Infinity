using System.Text.Json;

namespace NxInfinity.Runtime;

/// <summary>
/// Native cache-loader callbacks. Completion of this load does not mean that
/// every shader a game may encounter has been compiled. Recent build activity
/// is not an exact pending-job count.
/// </summary>
public sealed record ShaderCompilationSnapshot
{
    public int SchemaVersion { get; init; }
    public string Source { get; init; } = "";
    public bool? CacheRequested { get; init; }
    public bool CacheConsumerObserved { get; init; }
    public bool CacheLoadReturned { get; init; }
    public long CacheCallbackCount { get; init; }
    public string? CacheStage { get; init; }
    public long? CacheCompleted { get; init; }
    public long? CacheTotal { get; init; }
    public double? CacheLoadDurationMs { get; init; }
    public long? ActivityIndicator { get; init; }
    public string ActivityMeaning { get; init; } = "unavailable";
    public bool ExactPendingQueueKnown => false;
    public bool AllFutureGameShadersCompiled => false;
}

public sealed record ShaderCompilationResult
{
    public PerformanceAvailability Availability { get; init; }
    public string Code { get; init; } = "";
    public string Message { get; init; } = "";
    public ShaderCompilationSnapshot? Snapshot { get; init; }
}

/// <summary>Validates only bounded, path-free native shader/cache observations.</summary>
public static class ShaderCompilationTelemetry
{
    public static ShaderCompilationResult Read(TelemetrySnapshot telemetry)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        if (telemetry.BackendMetrics is null || !telemetry.BackendMetrics.TryGetValue("shaderCompilation", out var data))
            return Result(PerformanceAvailability.Unsupported, "shader_telemetry_unavailable", "This core has not supplied native shader-cache progress.");
        try
        {
            if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("schemaVersion", out var version) ||
                !version.TryGetInt32(out int schema) || schema != 1) return Invalid();
            string? source = Text(data, "source");
            if (source is not ("native_pipeline_cache_callback_and_shader_notify" or "native_shader_cache_state_changed")) return Invalid();
            string? stage = Text(data, "cacheStage");
            if (stage is not (null or "prepare" or "build" or "package" or "complete")) return Invalid();
            bool? requested = Boolean(data, "cacheRequested");
            bool observed = Boolean(data, "cacheConsumerObserved") ?? throw new FormatException();
            bool returned = Boolean(data, "cacheLoadReturned") ?? throw new FormatException();
            long callbacks = Count(data, "cacheCallbackCount") ?? throw new FormatException();
            long? completed = Count(data, "cacheCompleted"), total = Count(data, "cacheTotal");
            if ((completed is null) != (total is null) || completed > total || stage is not null && callbacks == 0) return Invalid();
            double? duration = Number(data, "cacheLoadDurationMs");
            if (duration is not null && !returned) return Invalid();
            long? activity = Count(data, "activityIndicator");
            string meaning = Text(data, "activityMeaning") ?? "unavailable";
            if (meaning is not ("unavailable" or "native_recent_build_activity_2s") || activity is not null && meaning == "unavailable") return Invalid();
            return new ShaderCompilationResult
            {
                Availability = PerformanceAvailability.Available, Code = "native_shader_cache_observation",
                Message = "Native cache-load progress; future shader encounters and exact pending queue length remain unknown.",
                Snapshot = new ShaderCompilationSnapshot
                {
                    SchemaVersion = schema, Source = source, CacheRequested = requested, CacheConsumerObserved = observed,
                    CacheLoadReturned = returned, CacheCallbackCount = callbacks, CacheStage = stage,
                    CacheCompleted = completed, CacheTotal = total, CacheLoadDurationMs = duration,
                    ActivityIndicator = activity, ActivityMeaning = meaning
                }
            };
        }
        catch (Exception error) when (error is FormatException or InvalidOperationException or OverflowException)
        { return Invalid(); }
    }

    private static ShaderCompilationResult Result(PerformanceAvailability availability, string code, string message) =>
        new() { Availability = availability, Code = code, Message = message };
    private static ShaderCompilationResult Invalid() => Result(PerformanceAvailability.Invalid,
        "shader_telemetry_invalid", "The core supplied an invalid or unsupported native shader-cache record.");
    private static string? Text(JsonElement data, string name)
    {
        if (!data.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new FormatException();
        return value.GetString();
    }
    private static bool? Boolean(JsonElement data, string name)
    {
        if (!data.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw new FormatException() };
    }
    private static long? Count(JsonElement data, string name)
    {
        if (!data.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long count) || count is < 0 or > 1_000_000_000_000)
            throw new FormatException();
        return count;
    }
    private static double? Number(JsonElement data, string name)
    {
        if (!data.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number) || !double.IsFinite(number) || number is < 0 or > 86_400_000)
            throw new FormatException();
        return number;
    }
}
