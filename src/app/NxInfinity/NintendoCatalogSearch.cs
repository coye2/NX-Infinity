using System;
using System.Collections.Generic;

namespace NxInfinity;

public sealed record NintendoCatalogSearch(string Query, IReadOnlyList<NintendoCatalogGame> Results, int TotalResults, bool FromCache, DateTimeOffset RetrievedUtc);
