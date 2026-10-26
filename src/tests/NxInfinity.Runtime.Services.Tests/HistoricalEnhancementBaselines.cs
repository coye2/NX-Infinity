// Historical serialization fixtures only; these cores are not selectable or shipped.
namespace NxInfinity.Runtime.Services;

/// <summary>Non-applicable catalog entries. They contain no patch bytes, exact build claims, or proposed setting values.</summary>
public static class HistoricalEnhancementBaselines
{
    public const string ScarletTitleId = "0100A3D008C5C000";
    public const string VioletTitleId = "01008F6008C5E000";
    public const string CommunityIdentifierSource = "https://github.com/Gonzalooo/Scarlet-and-Violet-Randomizer";
    public const string NintendoUpdateSource = "https://en-americas-support.nintendo.com/app/answers/detail/a_id/60277/";

    public static IReadOnlyList<EnhancementProfile> CreateResearchTemplates()
    {
        var result = new List<EnhancementProfile>();
        foreach (var (title, name) in new[] { (ScarletTitleId, "scarlet"), (VioletTitleId, "violet") })
        foreach (var (backend, revision, settingsSource) in new[]
        {
            ("eden", "de7e6e01d43e8cf2ec193d74fa053b0d7949955f", "https://github.com/eden-emulator/mirror/blob/de7e6e01d43e8cf2ec193d74fa053b0d7949955f/src/common/settings.h"),
            ("ryujinx", "7d1fd99d3dc1c230ef7f823e96f187b3de619e4a", "https://starforgejo.dev/projects/Kenji-NX/src/commit/7d1fd99d3dc1c230ef7f823e96f187b3de619e4a/src/Ryujinx.UI.Common/Configuration/ConfigurationFileFormat.cs"),
            ("citron", "598b700884c7cd3bee0a25ce30d0cab066117c5b", "https://github.com/citron-neo/emulator/blob/598b700884c7cd3bee0a25ce30d0cab066117c5b/src/common/settings.h")
        })
            result.Add(new EnhancementProfile
            {
                ProfileId = "pokemon-" + name + "-" + backend + "-baseline", Version = "0.1.0-research",
                DisplayName = "Pokemon " + name + " / " + backend + " research baseline",
                TitleId = title, BuildId = null, BackendId = backend, BackendRevision = revision,
                Status = EnhancementStatus.ResearchCandidate, Source = CommunityIdentifierSource,
                Evidence = "Title identifier appears in a community mod-author README; it is not official Nintendo release metadata. No exact executable build or improved gameplay result is established.",
                OriginalBehavior = "Preserve the user's current settings and content. This template proposes no mutation and cannot be applied.",
                RollbackNotes = "No mutation is authorized by this template. A future reviewed exact-build profile must record actual original scalar settings before changing them.",
                Changes =
                [
                    new()
                    {
                        ChangeId = "settings-research", Kind = EnhancementChangeKind.Settings, Source = settingsSource,
                        Description = "Review the backend's exposed settings against an exact title/build before proposing a value.",
                        Evidence = "No setting value, measured improvement, target driver match, or effective game-output result is supplied."
                    },
                    new()
                    {
                        ChangeId = "game-patch-research", Kind = EnhancementChangeKind.GamePatch, Source = CommunityIdentifierSource,
                        Description = "Placeholder for a separately reviewed exact-build patch; no patch is included or activated.",
                        Evidence = "The source is used only as a community title-identifier reference. It does not qualify a patch or establish an FPS or rendering improvement."
                    },
                    new()
                    {
                        ChangeId = "switch2-platform-upgrade", Kind = EnhancementChangeKind.UnsupportedPlatform,
                        Status = EnhancementStatus.Unsupported, Source = NintendoUpdateSource,
                        Description = "Official Switch 2 update improvements are outside this Switch-runtime enhancement implementation.",
                        Evidence = "Official descriptions of image quality and smoother movement do not establish a numerical FPS/resolution target or an applicable emulator patch."
                    },
                    new()
                    {
                        ChangeId = "separate-content-verification", Kind = EnhancementChangeKind.SeparatelySuppliedContent, Source = NintendoUpdateSource,
                        Description = "Any needed title update or DLC must be supplied separately and its actual mounted identity verified.",
                        Evidence = "No update, DLC, game bytes, keys, firmware, content installer, or verified folder-version claim is supplied by this catalog."
                    }
                ]
            });
        return result;
    }
}
