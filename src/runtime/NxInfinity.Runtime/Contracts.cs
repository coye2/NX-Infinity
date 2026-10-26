using System.Text.Json;
using System.Text.Json.Serialization;

namespace NxInfinity.Runtime;

public static class NxVersion
{
    public const int Protocol = 1;
    public const string Sdk = "0.3.2-dev";
}

public enum RuntimeState { Created, Initialized, Loaded, Running, Paused, Stopped, Faulted, Disposed }

public sealed record BackendLaunch
{
    // Only BackendInstallation can attach verified provenance. Record cloning
    // retains it; execution-affecting clones are checked before any process starts.
    [JsonIgnore]
    internal VerifiedBackendInstallation? VerifiedInstallation { get; init; }

    public required string BackendId { get; init; }
    public required string Revision { get; init; }
    public required string ExecutablePath { get; init; }
    public string[] Arguments { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public string? ExecutableSha256 { get; init; }
    /// <summary>Installation artifact hashes rechecked immediately before each process start.</summary>
    public IReadOnlyDictionary<string, string> ArtifactSha256 { get; init; } = new Dictionary<string, string>();
    public bool AllowUnqualified { get; init; }
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Optional deadline for load, content inspection, prerequisite state/import, and firmware inspection/recovery. Other controls retain CommandTimeout.</summary>
    public TimeSpan? ContentTimeout { get; init; }
    /// <summary>Optional deadline for native initialization; never used by stop, window or input commands.</summary>
    public TimeSpan? InitializationTimeout { get; init; }
}

public sealed record BackendInfo
{
    public int ProtocolVersion { get; init; }
    public string BackendId { get; init; } = "";
    public string BackendName { get; init; } = "";
    public string BackendRevision { get; init; } = "";
    public string AdapterVersion { get; init; } = "";
    public string Qualification { get; init; } = "unqualified";
    public string[] Capabilities { get; init; } = [];
    public string[] Limitations { get; init; } = [];
    /// <summary>
    /// Policies compiled into this worker. Installation verification binds the
    /// declaration to its artifact; it does not establish cache use, measured
    /// performance, game compatibility, or a validated FPS option.
    /// </summary>
    public IReadOnlyDictionary<string, string> PerformancePolicies { get; init; } = new Dictionary<string, string>();
}

public sealed record RuntimeOptions
{
    /// <summary>NX-owned base directory. Cores receive base/backends/id/profile.</summary>
    public required string DataRoot { get; init; }
    public string ProfileId { get; init; } = "default";
    public string GraphicsBackend { get; init; } = "vulkan";
    /// <summary>
    /// Explicit constructor-bound renderer mode. Null uses this exact core
    /// revision's persisted request, or its original default when none exists.
    /// A supplied value requires initialize.renderer_threading capability.
    /// This option never writes a persisted preference by itself.
    /// </summary>
    public string? RendererThreading { get; init; }
    public int Width { get; init; } = 1280;
    public int Height { get; init; } = 720;
    public string WindowTitle { get; init; } = "NX Infinity runtime reference client";
    public bool Headless { get; init; }
    /// <summary>Explicit recovery decision after inspection/backup; never defaulted on.</summary>
    public bool AcknowledgeUncleanShutdown { get; init; }
}

public sealed record ContentInfo
{
    public string? TitleId { get; init; }
    public string? Name { get; init; }
    public string? BuildId { get; init; }
    public string? Format { get; init; }
    public string? DisplayVersion { get; init; }
    public string? Kind { get; init; }
    public string? Publisher { get; init; }
    public bool IdentityVerified { get; init; }
    public string? BuildIdSource { get; init; }
    public string? RuntimeProgramId { get; init; }
    public string? NacpTitleId { get; init; }
    public string? NativeLoaderStatus { get; init; }
    public ContentArtwork? Artwork { get; init; }
    public ContentIssue[] Issues { get; init; } = [];
    public ContentEntry[] Entries { get; init; } = [];
}

public sealed record SettingSchema
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string Description { get; init; } = "";
    public string Kind { get; init; } = "";
    public JsonElement DefaultValue { get; init; }
    public double? Minimum { get; init; }
    public double? Maximum { get; init; }
    public JsonElement[] Values { get; init; } = [];
    public string Apply { get; init; } = "restart";
    public string Scope { get; init; } = "session";
    public bool Supported { get; init; }
    public string[] Dependencies { get; init; } = [];

    public void Validate(JsonElement value)
    {
        bool valid = Kind switch
        {
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "number" => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) && double.IsFinite(n),
            "string" => value.ValueKind == JsonValueKind.String,
            "enum" => Values.Any(v => JsonElement.DeepEquals(v, value)),
            _ => false
        };
        if (!valid) throw new NxException("invalid_argument", $"Invalid {Kind} value for {Id}.");
        if (value.ValueKind == JsonValueKind.Number)
        {
            var n = value.GetDouble();
            if ((Minimum is { } min && n < min) || (Maximum is { } max && n > max))
                throw new NxException("invalid_argument", $"Value for {Id} is outside its advertised range.");
        }
        if (!Supported) throw new NxException("unsupported", $"{Id} is not implemented by this adapter.");
    }
}

/// <summary>Only EffectiveKnown+Applied can acknowledge a live core change.</summary>
public sealed record SettingState
{
    public string Id { get; init; } = "";
    public JsonElement? Requested { get; init; }
    public JsonElement? Persisted { get; init; }
    public JsonElement? Effective { get; init; }
    public bool EffectiveKnown { get; init; }
    public bool Applied { get; init; }
    public bool RequiresRestart { get; init; }
    /// <summary>Authoritative consumer/readback path, distinct from a requested value.</summary>
    public string? EffectiveSource { get; init; }
    public string? Reason { get; init; }
    public bool GameplayVerified { get; init; }
}

/// <summary>Null means unavailable. Frame-time meaning is specified by the backend.</summary>
public sealed record TelemetrySnapshot
{
    public string State { get; init; } = "unknown";
    public double? EmulationFps { get; init; }
    public double? FrameTimeMs { get; init; }
    public long? FrameCount { get; init; }
    public long? MemoryBytes { get; init; }
    public Dictionary<string, JsonElement> BackendMetrics { get; init; } = [];
    public DateTimeOffset? SampleTimeUtc { get; init; }
    public string Source { get; init; } = "unavailable";
}

public sealed record InputSnapshot
{
    public int Player { get; init; }
    public string[] Buttons { get; init; } = [];
    public double LeftX { get; init; }
    public double LeftY { get; init; }
    public double RightX { get; init; }
    public double RightY { get; init; }
    internal void Validate()
    {
        if (Player is < 0 or > 7 || Buttons.Length > 32 || Buttons.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 32))
            throw new NxException("invalid_argument", "Player must be 0..7; button names must be bounded nonempty strings.");
        foreach (var axis in new[] { LeftX, LeftY, RightX, RightY })
            if (!double.IsFinite(axis) || axis < -1 || axis > 1)
                throw new NxException("invalid_argument", "Stick axes must be finite values in [-1,1].");
    }
}

public sealed record InteractionRequest(string InteractionId, string Kind, JsonElement Details, int? TimeoutSeconds);
public sealed record InteractionResponse
{
    public required string InteractionId { get; init; }
    public bool Accepted { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Text { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? ButtonIndex { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? CursorBegin { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? CursorEnd { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Key { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public long? Generation { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Confirmed { get; init; }
}
public sealed record RuntimeEvent(string Name, JsonElement Data)
{
    public InteractionRequest? Interaction => Name == "interaction.request" ? NxJson.Read<InteractionRequest>(Data) : null;
}
public sealed record ShutdownReport(bool Graceful, bool ForcedTermination, string? Failure);
public sealed record CommandLatency(double MedianMs, double P95Ms, int Samples, string Measurement);

internal static class NxJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        MaxDepth = 32,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    internal static T Read<T>(JsonElement data) => data.Deserialize<T>(Options)
        ?? throw new NxException("protocol_mismatch", $"Missing {typeof(T).Name} result.");
}
