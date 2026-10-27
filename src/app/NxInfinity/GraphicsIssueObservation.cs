using System;
using NxInfinity.Runtime;

namespace NxInfinity;

public sealed record GraphicsIssueObservation(DateTimeOffset ObservedUtc, string IssueId, string BackendId, string BackendRevision, string AdapterVersion, string? TitleId, string? MainBuildId, string? CaptureId, long? SegmentId, long? Sequence, double SnapshotRoundTripMs, GraphicsDeviceResult GraphicsDevice, ShaderCompilationResult ShaderCompilation)
{
    public string Scope => "Native snapshot requested after a manual issue marker. Sequence identifies the observation boundary, not the exact corrupted draw.";

    public bool RenderingFixVerified => false;
}
