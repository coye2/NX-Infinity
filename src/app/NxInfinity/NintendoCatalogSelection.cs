using System;

namespace NxInfinity;

public sealed record NintendoCatalogSelection(int SchemaVersion, string EntryId, NintendoCatalogGame Game, string? ArtworkSha256, string? ArtworkMediaType, int? ArtworkWidth, int? ArtworkHeight, int? ArtworkBytes, DateTimeOffset? ArtworkRetrievedUtc, DateTimeOffset SelectedUtc);
