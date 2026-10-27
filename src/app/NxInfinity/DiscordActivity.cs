namespace NxInfinity;

public sealed record DiscordActivity(string details, string state, object assets, object? timestamps = null, int type = 0);
