using System;
using System.Collections.Generic;

namespace NxInfinity;

internal sealed record FrameRatePreferences
{
    public int SchemaVersion { get; init; } = 1;

    public int GlobalTarget { get; init; }

    public Dictionary<string, int> Games { get; init; } = new Dictionary<string, int>(StringComparer.Ordinal);
}
