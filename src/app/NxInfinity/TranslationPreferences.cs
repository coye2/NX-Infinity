namespace NxInfinity;

public sealed record TranslationPreferences(bool TranslateLauncher = false, string SourceLanguage = "en", string TargetLanguage = "it", bool GameOverlay = false, int IntervalSeconds = 3, bool FullFrame = false);
