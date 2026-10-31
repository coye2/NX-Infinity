using System;

namespace NxInfinity;

public sealed record ThemePreferences
{
    public int Version { get; init; } = 1;

    public ThemeChoice Desktop { get; init; } = new ThemeChoice();

    public ThemeChoice Home { get; init; } = new ThemeChoice("home");

    public string[] Favorites { get; init; } = Array.Empty<string>();

    public SavedTheme[] Saved { get; init; } = Array.Empty<SavedTheme>();
}
