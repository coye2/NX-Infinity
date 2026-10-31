using System;
using System.Collections.Generic;

namespace NxInfinity;

internal sealed record GpuPerformancePreferences
{
    public int SchemaVersion { get; init; } = 1;

    public GpuPerformanceOptions Global { get; init; } = new GpuPerformanceOptions();

    public Dictionary<string, GpuPerformanceOptions> Games { get; init; } = new Dictionary<string, GpuPerformanceOptions>(StringComparer.Ordinal);
}
