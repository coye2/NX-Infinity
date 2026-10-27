using NxInfinity.Runtime;

namespace NxInfinity;

public sealed record InfinityPerformanceRun(PerformanceCaptureSummary Capture, string? TitleId, string? MainBuildId, long LaunchGeneration, string StartedBy, string? EndReason)
{
    public GraphicsDeviceResult? GraphicsDevice { get; init; }

    public ShaderCompilationResult? ShaderCompilation { get; init; }

    public string? DisplayVersion { get; init; }

    public FrameRatePolicyState? FrameRateAtStart { get; init; }

    public FrameRatePolicyState? FrameRateAtEnd { get; init; }
}
