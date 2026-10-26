namespace NxInfinity.Runtime.Enhancements;

public enum FrameRateSupportState { Supported, ResearchOnly, Unsupported }
public enum FrameRateMechanism { StaticExecutable, RuntimeDynamic, Mixed, GuestPlugin, Native60Baseline, Unknown }
public enum FrameRateIdentityMatch { Unknown, TitleOnly, SourcePrefixOnly, ExactBuild, Mismatch }
public enum FrameRateOriginalEvidence { NotProvided, UpstreamRestoreValuesOnly, VerifiedExactExecutable }

public sealed record FrameRateSourceReference(string Url, string Repository, string? Commit,
    string? BlobSha, string LicenseObservation);

/// <summary>
/// Source research is separate from an executable patch. A source's short build ID is never
/// padded or promoted into a verified full build ID. All initial entries are non-applicable.
/// </summary>
public sealed record FrameRateCompatibilityEntry
{
    public required string Id { get; init; }
    public required string TitleId { get; init; }
    public required string GameName { get; init; }
    public string? DisplayVersion { get; init; }
    public string? UpstreamBuildIdPrefix { get; init; }
    public string? VerifiedBuildId { get; init; }
    public int TargetFps { get; init; } = 60;
    public FrameRateSupportState Status { get; init; }
    public FrameRateMechanism Mechanism { get; init; }
    public required string Summary { get; init; }
    public required string ApplyBlockReason { get; init; }
    public FrameRateOriginalEvidence ExpectedOriginalEvidence { get; init; }
    public required string ExpectedOriginalProvenance { get; init; }
    public IReadOnlyList<FrameRateSourceReference> SourceReferences { get; init; } = [];
    public IReadOnlyList<string> RequiredChecks { get; init; } = [];

    // A future supported entry must provide independently reviewed evidence for all these gates.
    // Metadata eligibility never substitutes for the adapter's actual build/byte preflight.
    public bool RedistributionPermissionVerified { get; init; }
    public string? QualifiedPayloadId { get; init; }
    public string? PayloadSha256 { get; init; }
    public string? QualificationReportSha256 { get; init; }
    public bool NormalGameSpeedVerified { get; init; }
    public bool RequestedHardwareValidated { get; init; }
}

public sealed record FrameRateCompatibilityAssessment(FrameRateCompatibilityEntry Entry,
    FrameRateIdentityMatch IdentityMatch, string ApplyBlockReason)
{
    public bool CanApply => Entry.Status == FrameRateSupportState.Supported
        && Entry.TargetFps == 60
        // Only the pre-first-start static adapter is implemented. Metadata must
        // not make a dynamic or mixed recipe appear usable without its adapter.
        && Entry.Mechanism == FrameRateMechanism.StaticExecutable
        && IdentityMatch == FrameRateIdentityMatch.ExactBuild
        && FrameRateCompatibilityCatalog.FullHex(Entry.VerifiedBuildId, 64)
        && Entry.ExpectedOriginalEvidence == FrameRateOriginalEvidence.VerifiedExactExecutable
        && !string.IsNullOrWhiteSpace(Entry.ExpectedOriginalProvenance)
        && Entry.RedistributionPermissionVerified
        && !string.IsNullOrWhiteSpace(Entry.QualifiedPayloadId)
        && FrameRateCompatibilityCatalog.FullHex(Entry.PayloadSha256, 64)
        && FrameRateCompatibilityCatalog.FullHex(Entry.QualificationReportSha256, 64)
        && Entry.NormalGameSpeedVerified && Entry.RequestedHardwareValidated
        && string.IsNullOrWhiteSpace(Entry.ApplyBlockReason)
        && string.IsNullOrWhiteSpace(ApplyBlockReason);
}

/// <summary>
/// Offline title/build research used by the launcher to explain the 60 FPS preference.
/// No network access, executable payload, downloaded plugin or memory write occurs here.
/// </summary>
public static class FrameRateCompatibilityCatalog
{
    public static DateTimeOffset CheckedUtc { get; } = new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);
    public const string CoverageNotice = "60 FPS requires a game-specific timing patch for some games. " +
        "A frame-rate counter alone does not verify normal gameplay speed. " +
        "These source entries have no qualified NX retail payload; Original behavior remains active when a patch is unavailable.";
    public const string WarehouseRevision = "f7047cacf31350650b7bedc2ab6fe00df2dac523";
    public const string OptimizerRevision = "bd98bb3c9ee7eb16c2dd3c51a8c6ecf168330cf3";
    public const string KirbyStaticRevision = "f4e4c431bd91af791fa3d0773787129598a51587";
    public const string ObservedTotk100BuildId = "082CE09B06E33A123CB1E2770F5F9147709033DB000000000000000000000000";
    private const string Kirby = "01004D300C5AE000";
    private const string Tears = "0100F2C0115B6000";
    private const string Scarlet = "0100A3D008C5C000";
    private const string Violet = "01008F6008C5E000";
    private const string WarehouseLicense = "No project license or permission notice was found in the inspected repository root/config files; redistribution permission is not established.";
    private const string MissingOriginals = "The inspected source provides replacement behavior without original-byte preconditions. No owned executable bytes were available to verify those preconditions.";

    public static IReadOnlyList<string> CommonValidationRequirements { get; } = Freeze(new[]
    {
        "Bind title ID, full loaded main build ID, update/DLC set, module hashes, patch set, backend revision, adapter and worker identity.",
        "Compare an identical reproducible scene against Original behavior for at least five minutes of wall time; verify simulation speed, input, animation, physics and audio separately from presentation FPS.",
        "Repeat with cold caches, warm caches and a restarted process; preserve the original caches and record which state was used.",
        "Run independent RTX 3050 and RTX 4060 Ti checks with GPU model, driver, clocks, resolution and settings recorded. Unavailable hardware remains untested.",
        "Verify loading, transitions, save/reload and disabling the patch. A preflight or synthetic fixture cannot qualify retail gameplay."
    });

    public static IReadOnlyList<FrameRateCompatibilityEntry> Entries { get; } = CreateEntries();

    public static IReadOnlyList<FrameRateCompatibilityAssessment> Query(string? titleId = null, string? fullBuildId = null)
    {
        if (titleId is not null && !FullHex(titleId, 16)) return [];
        bool hasBuild = FullHex(fullBuildId, 64);
        var selected = Entries.Where(entry => titleId is null || string.Equals(entry.TitleId, titleId, StringComparison.OrdinalIgnoreCase));
        return Freeze(selected.Select(entry =>
        {
            var match = titleId is null ? FrameRateIdentityMatch.Unknown : !hasBuild ? FrameRateIdentityMatch.TitleOnly :
                entry.VerifiedBuildId is { } verified ? string.Equals(verified, fullBuildId, StringComparison.OrdinalIgnoreCase)
                    ? FrameRateIdentityMatch.ExactBuild : FrameRateIdentityMatch.Mismatch :
                entry.UpstreamBuildIdPrefix is { } prefix ? fullBuildId!.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    ? FrameRateIdentityMatch.SourcePrefixOnly : FrameRateIdentityMatch.Mismatch : FrameRateIdentityMatch.Unknown;
            string reason = match == FrameRateIdentityMatch.Mismatch ? "This source targets a different executable build. " + entry.ApplyBlockReason :
                match == FrameRateIdentityMatch.SourcePrefixOnly ? "Only the upstream build-ID prefix matches; the complete executable and original bytes are unverified. " + entry.ApplyBlockReason :
                entry.ApplyBlockReason;
            return new FrameRateCompatibilityAssessment(entry, match, reason);
        }));
    }

    private static IReadOnlyList<FrameRateCompatibilityEntry> CreateEntries()
    {
        var entries = new List<FrameRateCompatibilityEntry>
        {
            Candidate("kirby-100-dynamic", Kirby, "Kirby and the Forgotten Land", "1.0.0", "D9BA7DB72FFAFECD",
                FrameRateMechanism.RuntimeDynamic,
                "Upstream changes a live interval field and dynamic-resolution timing through pointer chains. It is a dynamic game-speed research lead.",
                "Runtime pointer validation, original-value evidence, reuse permission and normal-speed retail checks are missing.",
                Warehouse(Kirby, "D9BA7DB72FFAFECD", "f476b0bcc83b2748bb8a84c659ea8ab5b32addf7"),
                ["Check movement, jumps, Mouthful Mode, distant NPC animation, menus and cutscenes at sustained and fluctuating frame rates."]),
            Candidate("kirby-110-dynamic", Kirby, "Kirby and the Forgotten Land", "1.1.0", "A6CE40DC3AEDB1BE",
                FrameRateMechanism.RuntimeDynamic,
                "Upstream uses version-specific live pointer chains for interval and dynamic-resolution timing.",
                "Original-value evidence, reuse permission and retail checks are missing. Upstream warns that forced triple buffering can exhaust memory on 4 GB Switch hardware; this is not an NX hardware result.",
                Warehouse(Kirby, "A6CE40DC3AEDB1BE", "d8bf55e0f86b794d7631b1c317f19c4331d11a9b"),
                ["Verify the 1.1.0 build independently; do not reuse 1.0.0 pointer offsets or infer emulator behavior from console buffering tests."]),
            Candidate("kirby-100-static", Kirby, "Kirby and the Forgotten Land", "1.0.0", "D9BA7DB72FFAFECD07F5F5535DEC05734E59BCE1",
                FrameRateMechanism.StaticExecutable,
                "The original author's static 60 FPS archive contains two executable replacements. A separate Full variant also changes distant animation.",
                "No expected-original bytes or author reuse license are supplied. The author's 30 FPS Full archive is an animation patch, not an inverse of the two 60 FPS writes.",
                Source("theboy181/switch-ptchtxt-mods", KirbyStaticRevision,
                    "Kirby and the Forgotten Land/[01004D300C5AE000]/1.0.0/60fps.rar", "b2646f5fe8446a57184b51fc26b6e21f50e117c4",
                    "No license found in the original author repository. An aggregator's MIT license does not establish permission for the identical third-party archive."),
                ["Capture original instruction bytes from the exact owned executable before implementing any guarded static patch."]),
            new FrameRateCompatibilityEntry
            {
                Id = "totk-100-observed", TitleId = Tears, GameName = "The Legend of Zelda: Tears of the Kingdom", DisplayVersion = "1.0.0",
                UpstreamBuildIdPrefix = "082CE09B06E33A12", VerifiedBuildId = ObservedTotk100BuildId,
                Status = FrameRateSupportState.ResearchOnly, Mechanism = FrameRateMechanism.GuestPlugin,
                Summary = "The source-built DFPS++ module supports 1.0.0 and is bundled as an experimental launcher enhancement for this full observed build. It includes timing, presentation, Ultrahand and cutscene hooks.",
                ApplyBlockReason = "Experimental module available in the desktop test build when 60 FPS is selected. It is not qualified: original hook bytes, correct-speed gameplay and sustained hardware performance remain unverified.",
                RedistributionPermissionVerified = true,
                ExpectedOriginalProvenance = "Full build ID observed in the supplied 2026-10-22 validation report; no executable bytes or game-speed validation were supplied.",
                SourceReferences = Freeze(new[] { new FrameRateSourceReference("https://github.com/MaxLastBreath/TOTK-DFPS/tree/6597364efc5dc1efa01a43e702130d94140449f7", "MaxLastBreath/TOTK-DFPS", "6597364efc5dc1efa01a43e702130d94140449f7", null, "GPL-2.0 source, full corresponding modified source and license are included. This is the archived DFPS++ implementation, not the newer UltraCam binary."), Source("MaxLastBreath/nx-optimizer", OptimizerRevision,
                    "src/PatchInfo/Tears Of The Kingdom/PatchInfo.json", "b25d5484efc097d078d9ae59ef373aaf70648ae7", "CC BY-NC 2.0 repository notice; binary plugin reuse and exact-build support remain separate questions."),
                    WarehouseReadme() }),
                RequiredChecks = Checks(["Obtain the original executable from this owned build and reproduce the existing core exit before timing-patch qualification."])
            }
        };
        foreach (var (version, build, blob) in new[]
        {
            ("1.2.1", "9B4E43650501A4D4", "417fd10061bb282603faa0bc9e12b05b5e5cf6aa"),
            ("1.4.0", "6265F94D606242CE", "91e9e6c49d8995eaa5f67afd5b5b47f512301d58"),
            ("1.4.1", "965EAB9CEB8EB867", "c2861d774d621050afda09114269749a34eb657a"),
            ("1.4.2", "5CB42B1CF25469FB", "bd8fc21e07eff1648f630752e30819ef964e6bf6"),
            ("1.4.3", "277178B7DBA1B6D4", "572f359091cd872503b2dec7d4b48edc7524f08f")
        })
            entries.Add(Candidate("totk-" + version.Replace(".", "") + "-warehouse", Tears,
                "The Legend of Zelda: Tears of the Kingdom", version, build, FrameRateMechanism.Mixed,
                "Upstream combines startup executable changes, live timing data and cutscene handling; it also lists an NPDM add-on.",
                "Expected-original bytes, full build identity, reusable payload and emulator-specific timing/cutscene qualification are missing. A static FPS-cap-only conversion is incomplete.",
                Warehouse(Tears, build, blob),
                ["Verify Ultrahand aiming, physics, menus and prerendered cutscene timing; preserve every dependency or reject the patch."]));

        foreach (var (title, name, build, blob) in new[]
        {
            (Scarlet, "Pokemon Scarlet", "421C5411B487EB4D", "f472fc1a876a792d64711de66b83391fdbe09d16"),
            (Violet, "Pokemon Violet", "709BFD6611529864", "945c035b195f4a448816c1bc6dcc73d899a0b9ad")
        })
        {
            entries.Add(new FrameRateCompatibilityEntry
            {
                Id = title.ToLowerInvariant() + "-400-warehouse", TitleId = title, GameName = name, DisplayVersion = "4.0.0",
                UpstreamBuildIdPrefix = build, Status = FrameRateSupportState.Unsupported, Mechanism = FrameRateMechanism.Unknown,
                Summary = "Warehouse lists this build without an available FPSLocker config and identifies gameplay-speed, physics, UI and buffering concerns.",
                ApplyBlockReason = "No upstream config or verified complete 60 FPS correction is available for this entry. A frame-cap change alone does not address the listed timing problems.",
                ExpectedOriginalProvenance = "No replacement/original byte pairs are supplied by the inspected Warehouse entry.",
                SourceReferences = Freeze(new[] { WarehouseReadme() }),
                RequiredChecks = Checks(["Verify overworld traversal, battles, menus, animation, physics, audio and save/reload independently of the FPS display."])
            });
            entries.Add(Candidate(title.ToLowerInvariant() + "-301-plugin", title, name, "3.0.1", null,
                FrameRateMechanism.GuestPlugin,
                "A separate NX Optimizer source advertises a 3.0.1 DynamicFPS guest plugin; its metadata does not supply a full build ID or guarded byte operations.",
                "A precompiled subsdk plugin is not a reviewed static/runtime patch adapter. Exact build identity, original bytes, permission review and normal-speed testing are incomplete.",
                Source("MaxLastBreath/nx-optimizer", OptimizerRevision, "src/PatchInfo/" + name + "/PatchInfo.json", blob,
                    "CC BY-NC 2.0 repository notice. No plugin implementation or binary payload is copied into NX."),
                ["Keep the 3.0.1 plugin claim separate from Warehouse's 4.0.0 entry; an identical short build prefix would not establish complete identity or timing correctness."]));
        }
        entries.Add(Baseline("smash-native60", "01006A800016E000", "Super Smash Bros. Ultimate",
            "Preserve Original synchronization for this native-60 regression baseline. A 30-to-60 patch is not applicable.",
            [Issue("eden-emulator/Issue-Reports", 411), Issue("citron-neo/emulator", 56)],
            ["Measure match timer against wall time and repeat the same multi-fighter stage across cold, warm and restarted sessions; check effects, flicker, input and audio."]));
        entries.Add(Baseline("kart-native60", "0100152000022000", "Mario Kart 8 Deluxe",
            "Preserve Original synchronization for this native-60 regression baseline. Upstream has a report of a 60 FPS counter with slow gameplay.",
            [Issue("citron-neo/emulator", 170), Issue("eden-emulator/Issue-Reports", 685)],
            ["Compare race timer, lap duration and movement against wall time in the same single-player race; include loading, item effects, audio, save/reload and repeated launches."]));
        return Freeze(entries);
    }

    private static FrameRateCompatibilityEntry Candidate(string id, string title, string name, string version,
        string? prefix, FrameRateMechanism mechanism, string summary, string blocked,
        FrameRateSourceReference source, string[] checks) => new()
    {
        Id = id, TitleId = title, GameName = name, DisplayVersion = version, UpstreamBuildIdPrefix = prefix,
        Status = FrameRateSupportState.ResearchOnly, Mechanism = mechanism, Summary = summary,
        ApplyBlockReason = blocked, ExpectedOriginalEvidence = FrameRateOriginalEvidence.NotProvided,
        ExpectedOriginalProvenance = MissingOriginals, SourceReferences = Freeze(new[] { source }), RequiredChecks = Checks(checks)
    };
    private static FrameRateCompatibilityEntry Baseline(string id, string title, string name, string summary,
        FrameRateSourceReference[] sources, string[] checks) => new()
    {
        Id = id, TitleId = title, GameName = name, Status = FrameRateSupportState.ResearchOnly,
        Mechanism = FrameRateMechanism.Native60Baseline, Summary = summary,
        ApplyBlockReason = "Use Original behavior. No unlock patch is required; native speed and rendering regression tests remain unexecuted on the requested hardware.",
        ExpectedOriginalProvenance = "No game executable or full build identity was supplied for this regression title; no bytes are to be patched.",
        SourceReferences = Freeze(sources), RequiredChecks = Checks(checks)
    };
    private static FrameRateSourceReference Warehouse(string title, string prefix, string blob) => Source(
        "masagrator/FPSLocker-Warehouse", WarehouseRevision,
        "SaltySD/plugins/FPSLocker/patches/" + title + "/" + prefix + ".yaml", blob, WarehouseLicense);
    private static FrameRateSourceReference WarehouseReadme() => Source("masagrator/FPSLocker-Warehouse", WarehouseRevision,
        "README.md", "49ac9327d1fee445f8c9343d8314d9684884118a", WarehouseLicense);
    private static FrameRateSourceReference Source(string repo, string revision, string path, string blob, string license) =>
        new("https://github.com/" + repo + "/blob/" + revision + "/" + string.Join("/", path.Split('/').Select(Uri.EscapeDataString)), repo, revision, blob, license);
    private static FrameRateSourceReference Issue(string repo, int id) => new("https://github.com/" + repo + "/issues/" + id,
        repo, null, null, "Public upstream issue: observation only, not a reusable patch or an NX validation result.");
    private static IReadOnlyList<string> Checks(string[] specific) => Freeze(CommonValidationRequirements.Concat(specific));
    private static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values) => Array.AsReadOnly(values.ToArray());
    internal static bool FullHex(string? text, int length) => text?.Length == length && text.Any(c => c != '0') && text.All(Uri.IsHexDigit);
}

