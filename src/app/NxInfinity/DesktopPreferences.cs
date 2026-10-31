using NxInfinity.Runtime.Services;

namespace NxInfinity;

public sealed record DesktopPreferences(int SchemaVersion = 1, bool Night = false, double LibraryFraction = 0.38, bool FavoritesOnly = false, LibrarySort Sort = LibrarySort.Name, string? SelectedEntryId = null);
