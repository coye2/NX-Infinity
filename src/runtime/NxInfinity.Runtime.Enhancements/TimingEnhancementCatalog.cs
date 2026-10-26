using System.Collections.ObjectModel;
using System.Text.Json;

namespace NxInfinity.Runtime.Enhancements;

public sealed record TimingPatchFile(string ArchivePath, string OutputName, string Sha256, int Bytes, string NsoBuildId);

/// <summary>Author-provided identity is not a retail qualification receipt.</summary>
public sealed record TimingEnhancementRecipe
{
    public required string Id { get; init; }
    public required string TitleId { get; init; }
    public required string GameName { get; init; }
    public required IReadOnlyList<string> Versions { get; init; }
    public required string BuildIdPrefix { get; init; }
    public string? ExactBuildId { get; init; }
    public required string Mechanism { get; init; }
    public bool Embedded { get; init; }
    public required string SourceUrl { get; init; }
    public required string IdentitySourceUrl { get; init; }
    public required string Author { get; init; }
    public required string Caution { get; init; }
    public string? ArchiveUrl { get; init; }
    public string? ArchiveSha256 { get; init; }
    public int ArchiveBytes { get; init; }
    public IReadOnlyList<TimingPatchFile> Files { get; init; } = [];

    public bool Matches(string? title, string? fullBuild, string? version) =>
        FrameRateCompatibilityCatalog.FullHex(fullBuild, 64) &&
        string.Equals(title, TitleId, StringComparison.OrdinalIgnoreCase) &&
        Versions.Contains(version, StringComparer.Ordinal) &&
        fullBuild!.StartsWith(BuildIdPrefix, StringComparison.OrdinalIgnoreCase) &&
        (ExactBuildId is null || string.Equals(fullBuild, ExactBuildId, StringComparison.OrdinalIgnoreCase));

    public FrameRateCompatibilityAssessment Assessment(string fullBuild, string version) => new(new()
    {
        Id = Id, TitleId = TitleId, GameName = GameName, DisplayVersion = version,
        UpstreamBuildIdPrefix = BuildIdPrefix, VerifiedBuildId = ExactBuildId,
        Status = FrameRateSupportState.ResearchOnly,
        Mechanism = Enum.Parse<FrameRateMechanism>(Mechanism),
        Summary = (Embedded ? "Source-built timing module included. " : "Version-specific author patch is downloaded and hash-checked on first use, then cached for offline use. ") + Caution,
        ApplyBlockReason = "Experimental integration available; retail qualification is incomplete. " + Caution,
        ExpectedOriginalProvenance = "The native mount is bound to the complete inspected executable ID. The author's NSO identifier is retained at its original length, not padded into a verified identity. Original hook bytes are unverified.",
        RedistributionPermissionVerified = Embedded,
        SourceReferences = [new(SourceUrl, Author, null, null, Embedded ? "GPL-2.0; corresponding source included." : "Fetched directly from the author for local use. The patch is not redistributed in NX binaries or source."),
            new(IdentitySourceUrl, Author, null, null, "Identity metadata source; not gameplay evidence.")],
        RequiredChecks = FrameRateCompatibilityCatalog.CommonValidationRequirements
    }, ExactBuildId is null ? FrameRateIdentityMatch.SourcePrefixOnly : FrameRateIdentityMatch.ExactBuild,
        "Not gameplay-qualified. " + Caution);
}

public static class TimingEnhancementCatalog
{
    public static IReadOnlyList<TimingEnhancementRecipe> Entries { get; } = Read();
    public static TimingEnhancementRecipe? Select(string? title, string? fullBuild, string? version)
    {
        var matches = Entries.Where(r => r.Matches(title, fullBuild, version)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
    public static bool IsOwnedId(string id) => id == "builtin-totk-dfps-100" || Entries.Any(r => r.Id == id);
    private static IReadOnlyList<TimingEnhancementRecipe> Read()
    {
        using var stream = typeof(TimingEnhancementCatalog).Assembly.GetManifestResourceStream("NxInfinity.Runtime.Enhancements.TimingEnhancements.json")
            ?? throw new InvalidDataException("Timing enhancement catalog is missing.");
        var entries = JsonSerializer.Deserialize<TimingEnhancementRecipe[]>(stream) ?? [];
        if (entries.Length == 0 || entries.Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Length)
            throw new InvalidDataException("Timing enhancement catalog contains missing or ambiguous recipes.");
        foreach (var r in entries)
        {
            if (!FrameRateCompatibilityCatalog.FullHex(r.TitleId, 16) || r.BuildIdPrefix.Length is not (40 or 64) ||
                !r.BuildIdPrefix.All(Uri.IsHexDigit) || r.Versions.Count == 0 || r.Id.Length > 64 ||
                !r.Id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
                throw new InvalidDataException("Timing enhancement identity is invalid.");
        }
        return new ReadOnlyCollection<TimingEnhancementRecipe>(entries.Select(r => r with
        { Versions = Array.AsReadOnly(r.Versions.ToArray()), Files = Array.AsReadOnly(r.Files.ToArray()) }).ToArray());
    }
}
