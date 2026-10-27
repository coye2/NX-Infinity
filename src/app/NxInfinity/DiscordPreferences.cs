namespace NxInfinity;

public sealed record DiscordPreferences(bool Enabled = false, string ApplicationId = "", string LogoAsset = "nxi_logo", bool ShareGame = true, bool ShareLauncher = true);
