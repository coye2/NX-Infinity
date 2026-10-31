namespace NxInfinity;

public sealed record AvatarPreferences(int Version, string Active, AvatarProfile[] Profiles, string[] Favorites);
