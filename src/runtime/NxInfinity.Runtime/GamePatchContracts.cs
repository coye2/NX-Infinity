#nullable enable
using System;
using System.Collections.Generic;

namespace NxInfinity.Runtime;

/// <summary>One guarded write relative to the native loader's actual main-module image.</summary>
public sealed record GamePatchWrite
{
    public string Module { get; init; } = "main";
    public ulong Offset { get; init; }
    public string ExpectedHex { get; init; } = "";
    public string ReplacementHex { get; init; } = "";
}

/// <summary>
/// Executable patch for one exact loaded application/update/core. A declared
/// TargetFrameRate of 60 identifies a timing repair; null has no frame-policy effect. The
/// module_bytes_v1 adapter is restricted to a loaded worker which has never
/// started guest execution. Runtime dynamic patches require another adapter.
/// </summary>
public sealed record GamePatchRecipe
{
    public int SchemaVersion { get; init; } = 1;
    public string RecipeId { get; init; } = "";
    public string AdapterId { get; init; } = "module_bytes_v1";
    public string BackendId { get; init; } = "";
    public string BackendRevision { get; init; } = "";
    public string TitleId { get; init; } = "";
    public string BuildId { get; init; } = "";
    public string DisplayVersion { get; init; } = "";
    /// <summary>Null for a generic patch, or 60 for an explicitly declared timing repair.</summary>
    public int? TargetFrameRate { get; init; }
    /// <summary>original or swap_2_to_1; only applied native transactions can authorize this gate.</summary>
    public string FrameGate { get; init; } = "original";
    public IReadOnlyList<GamePatchWrite> Writes { get; init; } = [];
}

public sealed record GamePatchRequest(string TransactionId, string RecipeSha256, GamePatchRecipe Recipe);

/// <summary>Native acknowledgement, bound to a single process and its verified loaded module.</summary>
public sealed record GamePatchAcknowledgement
{
    public int SchemaVersion { get; init; } = 1;
    public string TransactionId { get; init; } = "";
    public string RecipeSha256 { get; init; } = "";
    public string RecipeId { get; init; } = "";
    public string AdapterId { get; init; } = "";
    public string BackendId { get; init; } = "";
    public string BackendRevision { get; init; } = "";
    public string TitleId { get; init; } = "";
    public string BuildId { get; init; } = "";
    public string DisplayVersion { get; init; } = "";
    public string Status { get; init; } = "";
    public string Boundary { get; init; } = "";
    public int? TargetFrameRate { get; init; }
    public string FrameGate { get; init; } = "original";
    public int WriteCount { get; init; }
    public int ByteCount { get; init; }
    public bool OriginalBytesVerified { get; init; }
    public bool ReadbackVerified { get; init; }
    public bool ExecutableInvalidationComplete { get; init; }
    public bool ActiveModsVerified { get; init; }
    public bool RollbackAvailable { get; init; }
    public long Generation { get; init; }
}

/// <summary>Requested policy, native effective gate and independent frame observations.</summary>
public sealed record FrameRatePolicyState
{
    public int SchemaVersion { get; init; } = 1;
    public int RequestedTarget { get; init; }
    public string EffectivePolicy { get; init; } = "original";
    public int? EffectiveTarget { get; init; }
    public string ReasonCode { get; init; } = "original_requested";
    public string? PatchTransactionId { get; init; }
    public string? TitleId { get; init; }
    public string? BuildId { get; init; }
    public bool GuestClockUnchanged { get; init; }
    public long GuestQueuedFrames { get; init; }
    public long ReplacedBeforeAcquire { get; init; }
    public long AcquiredGameBuffers { get; init; }
    public long CompositedGameFrames { get; init; }
    public long AppletAcquisitions { get; init; }
    public long HostPresentReturns { get; init; }
    public long HostPresents { get; init; }
    public long HostPresentFailures { get; init; }
    public long CorrelatedGamePresents { get; init; }
    public long UncorrelatedPresents { get; init; }
    public long CorrelationOverflows { get; init; }
    public double? MeasuredGameFps { get; init; }
    public double? MeasurementIntervalMs { get; init; }
    public long? MeasuredGameFrames { get; init; }
    public string MeasurementScope { get; init; } = "unavailable";
}
