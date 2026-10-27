namespace NxInfinity;

public sealed record HomePreferences
{
    public int SchemaVersion { get; init; } = 1;

    public bool StartInHome { get; init; }

    public string? SelectedEntryId { get; init; }

    public string Filter { get; init; } = "all";

    public bool SoundsEnabled { get; init; } = true;

    public double SoundVolume { get; init; } = 0.35;

    public bool StartupAnimation { get; init; } = true;

    public bool HomeFallbackChord { get; init; } = true;

    public string Language { get; init; } = "en";
}
