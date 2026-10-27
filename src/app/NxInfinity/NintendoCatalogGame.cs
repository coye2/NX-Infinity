using System;

namespace NxInfinity;

public sealed record NintendoCatalogGame(string CatalogKey, string DisplayTitle, string? Publisher, string? ReleaseDateText, string ProductUrl, string? SquareCoverUrl, string Source, DateTimeOffset RetrievedUtc, string CatalogResponseSha256, string? LandscapeUrl = null);
