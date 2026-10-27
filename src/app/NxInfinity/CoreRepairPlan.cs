using System.Collections.Generic;

namespace NxInfinity;

public sealed record CoreRepairPlan(string Target, string Source, string ManifestSha256, string BackendId, string Revision, IReadOnlyList<string> ChangedFiles);
