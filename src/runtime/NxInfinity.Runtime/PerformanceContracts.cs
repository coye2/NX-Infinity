using System.Text.Json;

namespace NxInfinity.Runtime;

public enum PerformanceAvailability { Available, Unsupported, Invalid }

/// <summary>
/// Actual CPU-side intervals observed by a native presentation callback or by a
/// successful present-call return. They are neither FPS-derived estimates nor
/// hardware scanout/GPU execution timestamps.
/// </summary>
public sealed record NativePresentTiming
{
    public int SchemaVersion { get; init; }
    public string TimingSource { get; init; } = "";
    public string? MeasurementPoint { get; init; }
    public long SegmentId { get; init; }
    /// <summary>Number of recorded intervals in this active segment.</summary>
    public long Sequence { get; init; }
    /// <summary>Oldest first; interval numbers are Sequence-Count+1 through Sequence.</summary>
    public IReadOnlyList<double> IntervalsMs { get; init; } = Array.Empty<double>();
    public long TotalPresentedFrames { get; init; }
    public double? LastPresentAgeMs { get; init; }
    public bool? ShaderCacheEnabled { get; init; }
    public bool? CpuCacheEnabled { get; init; }
    public string RendererThreading { get; init; } = "unknown";
    public string Api { get; init; } = "unknown";
    public string MeasurementScope { get; init; } = "";
}

public sealed record PerformanceTelemetryResult
{
    public PerformanceAvailability Availability { get; init; }
    public string Code { get; init; } = "";
    public string Message { get; init; } = "";
    public NativePresentTiming? Packet { get; init; }
}

public sealed record PerformanceInterval(long SegmentId, long Sequence, double Milliseconds);

/// <summary>
/// Percentiles and hitch counts describe the retained rolling interval window;
/// loss/coverage counters describe the whole capture. The nearest-rank method is
/// used on actual intervals. Unknown values remain null.
/// </summary>
public sealed record PerformanceCaptureSummary
{
    public string CaptureId { get; init; } = "";
    public string BackendId { get; init; } = "";
    public string BackendRevision { get; init; } = "";
    public string AdapterVersion { get; init; } = "";
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset? LastObservedUtc { get; init; }
    public int Capacity { get; init; }
    public bool StartedFromBaseline { get; init; }
    public long? BaselineSegmentId { get; init; }
    public long? BaselineSequence { get; init; }
    public long BoundaryIntervalsExcluded { get; init; }
    public string? TimingSource { get; init; }
    public string? MeasurementPoint { get; init; }
    public string MeasurementScope { get; init; } = "";
    public string PercentileMethod { get; init; } = "nearest_rank";
    public PerformanceAvailability Availability { get; init; }
    public string Code { get; init; } = "";
    public string Message { get; init; } = "";
    public int SampleCount { get; init; }
    public long AcceptedIntervals { get; init; }
    public long EvictedIntervals { get; init; }
    /// <summary>Known interval history preceding the first observed active packet.</summary>
    public long InitialHistoryOmitted { get; init; }
    /// <summary>Known missing interval numbers after capture observation began.</summary>
    public long MissingIntervals { get; init; }
    /// <summary>Unobserved segment IDs; the number of intervals in them is unknown.</summary>
    public long SkippedSegments { get; init; }
    public long DuplicateIntervals { get; init; }
    public long StalePackets { get; init; }
    public long InvalidPackets { get; init; }
    public long UnsupportedPackets { get; init; }
    public long InactivePackets { get; init; }
    public long SegmentCount { get; init; }
    public bool CountersSaturated { get; init; }
    public long? LatestSegmentId { get; init; }
    public long? LatestSequence { get; init; }
    /// <summary>
    /// Detected missing or unmeasurable coverage, distinct from normal rolling
    /// eviction. False does not prove that unobserved prior-segment history was
    /// complete; an interval count cannot be inferred from inactive presentations.
    /// </summary>
    public bool HasGaps { get; init; }
    public IReadOnlyList<PerformanceInterval> Samples { get; init; } = Array.Empty<PerformanceInterval>();
    public double? P50Ms { get; init; }
    public double? P95Ms { get; init; }
    public double? P99Ms { get; init; }
    public double? P999Ms { get; init; }
    public double? MaxMs { get; init; }
    public double? MeanMs { get; init; }
    public int HitchesOver33Point3Ms { get; init; }
    public int HitchesOver33Ms { get; init; }
    public int HitchesOver50Ms { get; init; }
    public int HitchesOver100Ms { get; init; }
    public double? LastPresentAgeMs { get; init; }
    public long? TotalPresentedFrames { get; init; }
    public bool? ShaderCacheEnabled { get; init; }
    public bool? CpuCacheEnabled { get; init; }
    public string? RendererThreading { get; init; }
    public string? Api { get; init; }
    public bool PhysicalPresentationVerified => false;
    public bool RetailGameplayVerified => false;
}

public enum PerformancePlanEntryStatus
{
    Ready, AlreadyEffective, Unsupported, EffectiveStateUnknown, RestartRequired,
    DependencyUnverified, PendingSetting, Unavailable
}

/// <summary>An audit of one known setting semantic, using this adapter's actual schema and readback.</summary>
public sealed record PerformancePlanEntry
{
    public string ObjectiveId { get; init; } = "";
    public string Label { get; init; } = "";
    public string? SettingId { get; init; }
    public JsonElement Requested { get; init; }
    public SettingState? Original { get; init; }
    public string? ApplyPhase { get; init; }
    public PerformancePlanEntryStatus Status { get; init; }
    public bool Included { get; init; }
    public string Reason { get; init; } = "";
}

/// <summary>
/// A bounded, opaque, exact-runtime preview. Only Included entries are proposed
/// writes. Previewing does not change settings or persisted preferences.
/// </summary>
public sealed record PerformancePlanPreview
{
    public string PlanId { get; init; } = "";
    public string BackendId { get; init; } = "";
    public string BackendRevision { get; init; } = "";
    public string AdapterVersion { get; init; } = "";
    public DateTimeOffset CreatedUtc { get; init; }
    public RuntimeState RuntimeState { get; init; }
    public bool CanApply { get; init; }
    public int ChangeCount { get; init; }
    public IReadOnlyList<PerformancePlanEntry> Entries { get; init; } = Array.Empty<PerformancePlanEntry>();
    public IReadOnlyList<string> Limitations { get; init; } = Array.Empty<string>();
}

/// <summary>
/// A readback-verified settings transaction in this exact runtime only. Effective
/// retains the applied values; Restored is populated after a verified rollback.
/// No receipt changes disk preferences or proves a game/performance improvement.
/// </summary>
public sealed record PerformancePlanReceipt
{
    public string ReceiptId { get; init; } = "";
    public string PlanId { get; init; } = "";
    public string BackendId { get; init; } = "";
    public string BackendRevision { get; init; } = "";
    public string AdapterVersion { get; init; } = "";
    public DateTimeOffset AppliedUtc { get; init; }
    public string Scope { get; init; } = "runtime_only";
    public IReadOnlyList<SettingState> Originals { get; init; } = Array.Empty<SettingState>();
    public IReadOnlyList<SettingState> Effective { get; init; } = Array.Empty<SettingState>();
    public bool RolledBack { get; init; }
    public DateTimeOffset? RolledBackUtc { get; init; }
    public IReadOnlyList<SettingState> Restored { get; init; } = Array.Empty<SettingState>();
    public bool GameplayVerified => false;
    public bool PerformanceImprovementVerified => false;
}
