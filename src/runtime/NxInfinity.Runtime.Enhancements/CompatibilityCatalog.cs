using NxInfinity.Runtime.Services;

namespace NxInfinity.Runtime.Enhancements;

/// <summary>A primary report or exact source reference. A report is not a qualified game fix.</summary>
public sealed record CompatibilityEvidence(string Url, string SourceKind, string Summary,
    string ReportState, DateTimeOffset? UpdatedUtc = null);

/// <summary>Research guidance only. There is deliberately no value, patch payload or apply operation.</summary>
public sealed record CompatibilityAction
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string Kind { get; init; } = "validation";
    public string Status { get; init; } = "unverified";
    public string Explanation { get; init; } = "";
    public string? SettingId { get; init; }
    public string? RequiredCapability { get; init; }
    public string? SourceAuditId { get; init; }
    public IReadOnlyList<string> EvidenceUrls { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ValidationNeeded { get; init; } = Array.Empty<string>();
    public bool CanApply => false;
    public bool ConfirmedFix => false;
}

/// <summary>Curated relevance data, not an affected-build range or an EnhancementProfile.</summary>
public sealed record CompatibilityIssue
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public IReadOnlyList<string> TitleIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> GameNames { get; init; } = Array.Empty<string>();
    /// <summary>Empty means the reported backend is unknown, never that all backends are affected.</summary>
    public IReadOnlyList<string> BackendIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ReportedPlatforms { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Symptoms { get; init; } = Array.Empty<string>();
    public string ReportedEnvironment { get; init; } = "";
    public string Status { get; init; } = "open_report";
    public IReadOnlyList<CompatibilityEvidence> Evidence { get; init; } = Array.Empty<CompatibilityEvidence>();
    public IReadOnlyList<string> ValidationNeeded { get; init; } = Array.Empty<string>();
    public IReadOnlyList<CompatibilityAction> Actions { get; init; } = Array.Empty<CompatibilityAction>();
    /// <summary>Review date in UTC, recorded to day precision. Not a live issue-tracker status.</summary>
    public DateTimeOffset CheckedUtc { get; init; }
    public bool ConfirmedFix => false;
    public bool CanApply => false;
}

/// <summary>Caller-supplied context for relevance. It cannot qualify an action by itself.</summary>
public sealed record CompatibilityQuery
{
    public string? TitleId { get; init; }
    public string? BuildId { get; init; }
    public bool IdentityVerified { get; init; }
    public string? BackendId { get; init; }
    public string? BackendRevision { get; init; }
    public string? AdapterVersion { get; init; }
    public string? Platform { get; init; }
    public string? GpuVendorId { get; init; }
    public string? GpuDeviceId { get; init; }
    public string? DriverVersion { get; init; }
    public bool HardwareVerified { get; init; }
}

public sealed record CompatibilityActionAssessment(CompatibilityAction Action, string Status,
    IReadOnlyList<string> BlockingReasons, bool? SourceRevisionMatchesAudit = null)
{
    public bool CanApply => false;
    public bool ConfirmedFix => false;
}

public sealed record CompatibilityAssessment(CompatibilityIssue Issue, string Relevance,
    IReadOnlyList<string> ContextNotes, IReadOnlyList<CompatibilityActionAssessment> ActionAssessments)
{
    public bool ConfirmedFix => false;
    public bool CanApply => false;
}

/// <summary>Presence of code at one full source revision, independent of game or runtime qualification.</summary>
public sealed record CompatibilitySourceAudit(string Id, string BackendId, string BackendRevision,
    string Summary, IReadOnlyList<string> SourceUrls, DateTimeOffset CheckedUtc)
{
    public bool SourcePresenceVerified => true;
    public bool RuntimeBehaviorVerified => false;
    public bool GameFixVerified => false;
    public bool CanApply => false;
}

/// <summary>
/// Offline, read-only research catalogue. Query performs no I/O and never writes settings, imports
/// content, selects another core, enables a mod or manufactures an applicable enhancement profile.
/// </summary>
public static class CompatibilityCatalog
{
    public const string CoverageNotice = "Curated user-priority and high-impact reports reviewed on 2026-10-19; " +
        "not a statistical ranking or a complete compatibility list. No listed workaround is a qualified NX game fix. " +
        "No matching report means this catalogue has no matching observation, not that a title or core is unaffected.";
    public const string QualificationNotice = "A report's display version, screenshot or issue closure does not establish " +
        "the exact main build, backend revision, NX adapter, active hardware, driver or sustained gameplay outcome.";
    public static DateTimeOffset CheckedUtc { get; } = new(2026, 10, 19, 0, 0, 0, TimeSpan.Zero);

    private const string EdenRevision = "de7e6e01d43e8cf2ec193d74fa053b0d7949955f";
    private const string CitronRevision = "598b700884c7cd3bee0a25ce30d0cab066117c5b";
    private const string Smash = "01006A800016E000";
    private const string Tears = "0100F2C0115B6000";
    private const string Kart = "0100152000022000";
    private const string PaperMario = "0100ECD018EBE000";
    private const string Engage = "0100A6301214E000";
    private const string Tomodachi = "010051F0207B2000";
    private const string LetsGoPikachu = "010003F003A34000";

    private static readonly IReadOnlyList<string> RequiredValidation = Freeze(new[]
    {
        "Bind the full 16-hex title ID and 64-hex main build ID, complete update/DLC and patch set, full backend revision, NX adapter and worker identity, active GPU and driver.",
        "Compare the same reproducible scene with original behavior; verify rendering, gameplay speed against wall time, animation, physics, input, audio, saves and loading.",
        "Measure actual present intervals and sustained performance beyond the reported failure window. Preserve originals and verify rollback before qualifying any change."
    });

    public static IReadOnlyList<CompatibilitySourceAudit> SourceAudits { get; } = Freeze(new[]
    {
        new CompatibilitySourceAudit("eden-async-shader-default", "eden", EdenRevision,
            "The pinned source defaults asynchronous shader compilation to false. Its optional unfinished-pipeline path can skip draws; " +
            "NX preserves the default and exposes no asynchronous-shader control. This is an existing default, with no advertised consumer readback or title-fix qualification.",
            Freeze(new[] { EdenSource("common/settings.h", "L658"), EdenSource("video_core/renderer_vulkan/vk_pipeline_cache.cpp", "L783-L801"),
                EdenSource("video_core/renderer_vulkan/vk_rasterizer.cpp", "L245-L247") }), CheckedUtc),
        new CompatibilitySourceAudit("citron-async-shader-default", "citron", CitronRevision,
            "The pinned source defaults asynchronous shader compilation to false. Its optional unfinished-pipeline path can skip draws; " +
            "NX preserves the default and exposes no asynchronous-shader control. This is source behavior, not proof that any reported missing geometry is fixed.",
            Freeze(new[] { CitronSource("common/settings.h", "L497"), CitronSource("video_core/renderer_vulkan/vk_pipeline_cache.cpp", "L779-L796"),
                CitronSource("video_core/renderer_vulkan/vk_rasterizer.cpp", "L338-L340") }), CheckedUtc),
        new CompatibilitySourceAudit("eden-conditional-readback-flush", "eden", EdenRevision,
            "The exact Eden pin already contains a High-accuracy conditional FlushRegion before invalidation and a GPU-thread flush/wait path. " +
            "The missing rasterizer flush proposed in issue #425 is not missing at this pin; reapplying that fragment would not establish a game fix.",
            Freeze(new[] { EdenSource("video_core/renderer_vulkan/vk_rasterizer.cpp", "L845-L851"),
                EdenSource("video_core/gpu_thread.cpp", "L84-L95") }), CheckedUtc),
        new CompatibilitySourceAudit("citron-conditional-readback-flush", "citron", CitronRevision,
            "The exact Citron pin contains an Extreme-accuracy conditional rasterizer flush, while its GPU-thread path only invalidates. " +
            "The two cores therefore have different source paths and accuracy semantics; an Eden observation cannot qualify Citron.",
            Freeze(new[] { CitronSource("video_core/renderer_vulkan/vk_rasterizer.cpp", "L1043-L1049"),
                CitronSource("video_core/gpu_thread.cpp", "L161-L164") }), CheckedUtc)
    });

    public static IReadOnlyList<CompatibilityIssue> Issues { get; } = CreateIssues();

    /// <summary>
    /// Filters a supplied well-formed title hint and exact backend ID. Unknown identity stays explicit;
    /// platform differences remain visible instead of hiding a potentially useful report. Full context
    /// only improves attribution: every returned action remains blocked pending independent qualification.
    /// </summary>
    public static IReadOnlyList<CompatibilityAssessment> Query(CompatibilityQuery? context = null)
    {
        bool hasTitle = FullHex(context?.TitleId, 16);
        bool verifiedIdentity = hasTitle && context!.IdentityVerified && FullHex(context.BuildId, 64);
        bool hasBackend = !string.IsNullOrWhiteSpace(context?.BackendId);
        var selected = Issues.Where(issue =>
            (!hasTitle || issue.TitleIds.Contains(context!.TitleId!, StringComparer.OrdinalIgnoreCase)) &&
            (!hasBackend || issue.BackendIds.Count == 0 || issue.BackendIds.Contains(context!.BackendId!, StringComparer.Ordinal)));
        var result = new List<CompatibilityAssessment>();
        foreach (var issue in selected)
        {
            var notes = new List<string> { QualificationNotice };
            string relevance = context is null ? "catalog_overview" : verifiedIdentity ? "reported_relevance" : "title_identity_unknown";
            if (context is not null)
            {
                if (!hasTitle) notes.Add("No verified full title ID is available. These records are catalogue context, not a match to the current game.");
                else if (!verifiedIdentity) notes.Add("The title ID is a query hint; the full main build and verified content identity are missing. No exact game match is established.");
                else notes.Add("The supplied title identity selects related reports only. None supplies a qualified fix for this exact main build.");
                if (!hasBackend) notes.Add("No current backend ID is supplied. Reported backends must not be inferred from a related core.");
                if (!FullRevision(context.BackendRevision)) notes.Add("A full backend source revision is unavailable; display versions and short commits are insufficient.");
                if (!HasText(context.AdapterVersion)) notes.Add("The NX adapter version is unavailable; an upstream revision alone does not identify the running adapter.");
                if (!context.HardwareVerified || !HasText(context.GpuVendorId) || !HasText(context.GpuDeviceId) || !HasText(context.DriverVersion))
                    notes.Add("The active GPU and exact driver have not been completely verified. Reported hardware is not a detected local device.");
                else notes.Add("The caller supplied hardware identity; the catalogue still has no qualified result for that exact GPU and driver.");
                if (HasText(context.Platform) && issue.ReportedPlatforms.Count != 0 &&
                    !issue.ReportedPlatforms.Contains(context.Platform!, StringComparer.OrdinalIgnoreCase))
                    notes.Add("The supplied platform is outside this record's reported platforms. The symptom is not confirmed on the current platform.");
            }
            if (issue.BackendIds.Count == 0)
                notes.Add("The core for this investigation is unconfirmed. An empty backend list does not mean all cores are affected.");
            if (issue.Status == "user_report_unconfirmed")
                notes.Add("This is a user-described investigation priority. No matching primary report or exact affected title/core combination has been established.");
            if (issue.Status == "closed_report_not_qualified")
                notes.Add("The upstream issue is closed. Closure alone neither proves the installed worker includes a fix nor qualifies current gameplay.");
            var actions = issue.Actions.Select(action => AssessAction(action, context, verifiedIdentity)).ToArray();
            result.Add(new(issue, relevance, Freeze(notes), Freeze(actions)));
        }
        return Freeze(result);
    }

    private static CompatibilityActionAssessment AssessAction(CompatibilityAction action, CompatibilityQuery? context, bool verifiedIdentity)
    {
        var reasons = new List<string> { "No reviewed, exact-build and hardware-qualified enhancement profile is attached to this research action." };
        if (!verifiedIdentity) reasons.Add("Verified full title and main-build identity is required before a title-specific change.");
        if (!FullRevision(context?.BackendRevision) || !HasText(context?.AdapterVersion))
            reasons.Add("Exact backend source and NX adapter identity are required.");
        if (context?.HardwareVerified != true || !HasText(context.GpuVendorId) || !HasText(context.GpuDeviceId) || !HasText(context.DriverVersion))
            reasons.Add("The active device and exact driver require verification.");
        if (action.RequiredCapability is not null)
            reasons.Add("This catalogue does not establish that the running worker supports the required capability.");
        bool? matches = null;
        if (action.SourceAuditId is not null)
        {
            var audit = SourceAudits.Single(a => a.Id == action.SourceAuditId);
            if (FullRevision(context?.BackendRevision) && HasText(context?.BackendId))
                matches = audit.BackendId == context!.BackendId && audit.BackendRevision.Equals(context.BackendRevision, StringComparison.OrdinalIgnoreCase);
            reasons.Add(matches == true ? "The source revision matches the audit; source presence does not verify the running adapter or a game fix."
                : "The source audit is scoped to its full named revision; it cannot be inherited by another or unknown revision.");
        }
        reasons.Add("A measured original/changed comparison and verified rollback are still required; similar screenshots and normal FPS are insufficient.");
        return new(action, "research_only", Freeze(reasons), matches);
    }

    private static IReadOnlyList<CompatibilityIssue> CreateIssues() => Freeze(new[]
    {
        Issue("eden-376-scarlet-buildings", "Scarlet: invisible city buildings",
            [EnhancementBaselines.ScarletTitleId], ["Pokémon Scarlet"], ["eden"], ["windows"], "open_report",
            ["City buildings become transparent or disappear; one report also describes slower rendering when facing them."],
            "Reported with Scarlet 3.0.1 and 4.0.0, Eden 0.2.0-rc1 through later 0.2.1 reports, Windows and Radeon RX 9070 / 9070 XT. Exact main builds and current NX behavior are unqualified.",
            [Report(376, "2026-09-27T20:39:24Z", "open", "Repeated AMD/Windows building reports; High GPU and Paranoid CPU settings did not consistently resolve them."),
                Comment(376, "4047920556", "High/Paranoid failure on RX 9070; a different emulator result is only a reporter comparison.")],
            [Action("scarlet-render-baseline", "Record the missing-building scene", "validation", "unverified",
                "Capture an original-settings comparison and active GPU identity. Higher accuracy is a reviewable experiment, not an established fix for this report.",
                [EdenIssue(376)], ["Revisit the same city and camera route, then test indoor/outdoor transitions and sustained traversal."], "eden.graphics.gpu_accuracy"),
             Action("scarlet-async-default", "Review the existing shader safeguard", "existing_native_default", "existing_default_only",
                "The pinned Eden adapter retains the native asynchronous-shader default. The optional skipped-draw source path is not evidence that it caused this issue.",
                [EdenSource("video_core/renderer_vulkan/vk_pipeline_cache.cpp", "L783-L801")], ["Obtain consumer observations before making a runtime or game-specific claim."], auditId: "eden-async-shader-default")],
            ["Do not infer that Violet, every AMD GPU or every Eden revision has the same cause."]),

        Issue("sv-tree-flicker-investigation", "Scarlet/Violet: tree flicker investigation",
            [EnhancementBaselines.ScarletTitleId, EnhancementBaselines.VioletTitleId], ["Pokémon Scarlet", "Pokémon Violet"], [], [], "user_report_unconfirmed",
            ["Tree flickering was described as a user priority; the precise scene and cause are unconfirmed."],
            "The user identifies an AMD Z1 Extreme device. Exact title/build, selected core, OS, driver, resolution, camera route and patch set are still needed; a device-family report does not qualify every S/V build or core.", [],
            [Action("sv-tree-capture", "Capture a reproducible tree-flicker sequence", "validation", "unverified",
                "Compare the same camera path at original native scale. Separate geometry loss, texture corruption, shadow instability and temporal aliasing before choosing a remedy.",
                [], ["Collect a short motion sequence plus actual intervals; a still image cannot distinguish these causes."])],
            ["Find a matching primary report or independently reproduce and diagnose the exact affected configuration."]),

        Issue("eden-10-smash-white-stages", "Smash Ultimate: white stages and glow",
            [Smash], ["Super Smash Bros. Ultimate"], ["eden"], ["windows", "linux"], "open_report",
            ["Some stages become white or glow over characters and the background."],
            "Reports emphasize Windows/Vulkan and AMD RX 6600 through newer Radeon models. OpenGL and one Linux nightly comparison were reported to avoid it; full game/build/driver combinations are incomplete.",
            [Report(10, "2026-09-19T18:02:03Z", "open", "The issue persists in later reports despite several accuracy, EDS and asynchronous-option experiments."),
                Comment(10, "3283565365", "The original reporter described a successful OpenGL comparison."),
                Comment(10, "4686467112", "One Linux nightly comparison was reported clear; it is not a full revision and hardware qualification."),
                IdentityReference(EdenIssue(694), "Smash title ID mapping only; the mod/network report is a separate failure.")],
            [Action("smash-api-comparison", "Review the reported graphics-API comparison", "reported_workaround", "not_supported_by_nx",
                "The reports justify a controlled API/platform comparison, not an automatic preset. NX's current Vulkan route does not expose this stock-frontend OpenGL switch.",
                [CommentUrl(10, "3283565365"), CommentUrl(10, "4686467112")], ["Require an actual supported API path and qualify the same stage, full build, device and driver."]),
             Action("smash-accuracy-review", "Review current accuracy and shader state", "settings_review", "contradictory_reports",
                "Extreme accuracy, EDS changes and asynchronous options failed for some reporters. Preserve the original state and do not purge saves or caches as a blanket fix.",
                [EdenIssue(10)], ["Separate this base-rendering symptom from mod-specific crashes."], "eden.graphics.gpu_accuracy")],
            ["Validate multiple affected stages and sustained matches, including animation speed, physics and save continuity."]),

        Issue("eden-411-smash-flicker-stutter", "Smash Ultimate: repeated-match flicker and stutter",
            [Smash], ["Super Smash Bros. Ultimate"], ["eden"], ["windows", "linux"], "open_report",
            ["Fighter or stage parts briefly turn black; first-use particle effects stutter again in a session."],
            "SSBU 13.0.4, Eden 0.2.0-rc2, Windows 11 / RTX 4060, with a Steam Deck comparison. The same eight-fighter Big Battlefield match was repeated three times; asynchronous settings were off and accuracy changes did not resolve it.",
            [Report(411, "2026-03-24T22:00:42Z", "open", "A repeatable stage/match report, separate from AMD white glow. The reviewed body has no native log attachment.")],
            [Action("smash-repeat-match", "Capture repeat-match rendering and native events", "validation", "unverified",
                "Repeat the same fighters and stage while recording actual shader activity, native intervals and graphics state. Existing disk-cache entries do not prove that all relevant native work is reused.",
                [EdenIssue(411)], ["Distinguish cache warmup, repeated native compilation, resource hazards and pacing; verify each hypothesis with native evidence."])],
            ["Do not classify all SSBU flicker, AMD white glow and NVIDIA crashes as one defect."]),

        Issue("eden-666-smash-native-crash", "Smash Ultimate: native access violation and macro assertion",
            [Smash], ["Super Smash Bros. Ultimate"], ["eden"], ["windows"], "open_report",
            ["Intermittent gameplay crashes have reported access-violation summaries, unmapped-memory messages and a Maxwell macro assertion."],
            "SSBU 13.0.2/13.0.4, unmodified Vulkan session, Windows 11, RTX 4070 Ti SUPER driver 616.92, short Eden build f6e7686038. Failure windows range from minutes to hours.",
            [Report(666, "2026-10-12T16:28:36Z", "open", "Two reported crash summaries share an access-violation bucket; an unsymbolized offset and a nearby assertion do not establish one root cause."),
                Comment(666, "6128346118", "A different title/Android configuration reports the same macro assertion and links a public log; that is not the same proven SSBU crash.")],
            [Action("smash-native-symbolized-crash", "Bind and symbolize the native failure", "native_fix_investigation", "unverified",
                "Match the crashing binary, symbols and command stream before changing macro execution or memory synchronization. Removing an assertion would conceal evidence without validating a repair.",
                [EdenIssue(666), EdenSource("video_core/engines/maxwell_3d.cpp", "L388-L401")],
                ["Obtain a symbolized stack and a bounded authored reproducer; validate the exact SSBU build beyond its reported failure window."])],
            ["This NVIDIA crash investigation remains separate from the user's AMD Z1 Extreme graphics symptoms."]),

        Issue("eden-288-violet-gym", "Violet: Alfornada gym fade-to-black crash",
            [EnhancementBaselines.VioletTitleId], ["Pokémon Violet"], ["eden"], ["windows", "linux"], "open_report",
            ["Starting the Alfornada gym test can fade to black and crash."],
            "Initial report: Violet 4.0.0, Eden 0.0.4-rc3, Windows 11 and RTX 4050 Laptop. Later reports include other NVIDIA devices and Linux.",
            [Report(288, "2026-08-12T13:32:39Z", "open", "The gym transition crashes; a comment reports a two-part handheld workaround."),
                Comment(288, "4741340627", "The reported workaround changes both emulated system mode and controller type to handheld.")],
            [Action("violet-gym-handheld", "Review both handheld prerequisites", "reported_workaround", "dependency_unverified",
                "The reported workaround requires BOTH docked-to-handheld mode and Pro-to-handheld controller type. The exposed docked toggle alone does not implement the complete workaround.",
                [CommentUrl(288, "4741340627")], ["Verify an actual native handheld controller-type route, original-value restoration, the full gym test and save/reload."],
                "eden.system.docked", "input.assign")],
            ["Do not present skipping the gym, editing a save or changing only system.docked as a verified repair."]),

        Issue("eden-656-violet-opus", "Violet: historical Opus title-screen crash",
            [EnhancementBaselines.VioletTitleId], ["Pokémon Violet"], ["eden"], ["windows"], "closed_report_not_qualified",
            ["A September nightly report describes a crash near title-screen audio startup."],
            "Violet 4.0.0 with DLC; Windows; short nightly revision ff8368c606. The report proposes an FFmpeg Opus buffer-offset/decoder-state defect. It does not bind the current NX worker.",
            [Report(656, "2026-09-15T02:47:10Z", "closed", "Historical audio regression report, with a reporter-proposed fix; closed status is not current-worker validation.")],
            [Action("violet-opus-provenance", "Audit the exact audio-decoder revision", "native_fix_investigation", "closed_report_not_qualified",
                "Check whether the diagnosed decoder code exists in the installed revision before testing a repair. Do not apply a source fragment solely because this issue was closed.",
                [EdenIssue(656)], ["Use an owned Opus decoding regression and then validate real title audio, loading, sustained play and save continuity."])],
            ["Establish full bad/fixed revision provenance and the exact NX adapter before classifying this regression as present or repaired."]),

        Issue("eden-658-lets-go-opus", "Let's Go Pikachu: historical pause/audio crash",
            [LetsGoPikachu], ["Pokémon: Let's Go, Pikachu!"], ["eden"], ["windows"], "closed_report_not_qualified",
            ["Pause/options can trigger an audio-decoder assertion after initial gameplay succeeds."],
            "Version 1.0.2, short nightly revision 5d150cac5c, Windows 11, Ryzen 7 5700G and RTX 5050; reported after an FFmpeg transition.",
            [Report(658, "2026-09-15T02:46:11Z", "closed", "Historical pause-triggered Opus failure; initial gameplay success did not establish stability.")],
            [Action("lets-go-pause-audio", "Validate pause and audio transitions", "native_fix_investigation", "closed_report_not_qualified",
                "Audit the installed decoder and reproduce the transition. An initial boot or screenshot does not cover the reported failure.",
                [EdenIssue(658)], ["Exercise pause, options, audio format transitions, saves and reloads after an owned decoder regression."])],
            ["Do not generalize a closed audio issue into a compatibility claim for other Pokémon titles."]),

        Issue("eden-266-paper-mario-readback", "Paper Mario TTYD: invisible characters and transitions",
            [PaperMario], ["Paper Mario: The Thousand-Year Door"], ["eden"], ["windows", "linux"], "open_report",
            ["Characters or transition graphics disappear while shadows or other layers remain."],
            "Eden 0.0.4/nightly reports on Linux/RX 7900 XTX and Windows/GTX 1080. Report #425 attributes some readback artifacts to a missing conditional flush.",
            [Report(266, "2026-04-07T21:42:34Z", "open", "Invisible characters and transition corruption; a later comment links the readback proposal."),
                Report(425, "2026-04-07T21:41:51Z", "open", "Proposed conditional rasterizer flush, with title ID and affected scenes."),
                SourceReference(EdenSource("video_core/renderer_vulkan/vk_rasterizer.cpp", "L845-L851"), "The exact NX Eden pin already contains the proposed conditional rasterizer flush.")],
            [Action("paper-mario-readback", "Verify the existing readback flush path", "source_audit", "already_present_at_pinned_source",
                "The relevant High-accuracy flush is already present at the audited Eden pin. A duplicate patch is not a meaningful fix; test the existing GPU readback path before proposing another change.",
                [EdenIssue(425), EdenSource("video_core/renderer_vulkan/vk_rasterizer.cpp", "L845-L851")],
                ["Add an authored GPU-write/CPU-readback regression, then verify the bridge, boat and affected cutscenes on exact retail builds."],
                "eden.graphics.gpu_accuracy", auditId: "eden-conditional-readback-flush")],
            ["A source-path match alone does not resolve the still-open title reports or validate Citron's different accuracy path."]),

        Issue("eden-466-tomodachi-device-loss", "Tomodachi Life: sustained Vulkan device loss",
            [Tomodachi], ["Tomodachi Life: Living the Dream"], ["eden"], ["windows"], "open_report",
            ["Longer sessions can crash with Vulkan device loss, sometimes around camera or broadcast interactions."],
            "Initial Windows 11 / RTX 4070 Ti SUPER report after roughly 80 minutes; other NVIDIA reports vary from minutes to over an hour. Short revision 4f4c298a39 is not a complete binding.",
            [Report(466, "2026-10-08T21:52:17Z", "open", "Repeated sustained-session device-loss reports; setting changes did not establish a consistent repair."),
                IdentityReference(EdenIssue(449), "Title ID/name mapping only; the earlier launch failure is a separate report.")],
            [Action("tomodachi-sustained", "Run a sustained device-loss investigation", "validation", "unverified",
                "Capture the actual Vulkan failure and resource behavior through the reported interaction sequence. No blanket cache deletion or GPU substitution is justified by these reports.",
                [EdenIssue(466)], ["Test beyond the longest relevant failure window, include camera interactions, and verify save continuity after a graceful exit."])],
            ["A five-second authored launch is insufficient to qualify an 80-minute failure."]),

        Issue("eden-512-engage-white-effects", "Fire Emblem Engage: white skill effects",
            [Engage], ["Fire Emblem Engage"], ["eden"], ["android"], "open_report",
            ["Some combat skills render white artifacts, including Shine and Lodestar Rush."],
            "Android handheld reports, including Ayn Thor and Retroid Pocket 6; short Eden revision 251a3470dc. Driver results differ by skill and device.",
            [Report(512, "2026-07-31T00:45:48Z", "open", "A driver comparison improved one effect for a reporter while another remained broken."),
                Comment(512, "5137849862", "Turnip T23 reportedly fixes Lodestar Rush on one configuration; Shine remains incorrect."),
                IdentityReference(EdenIssue(689), "Title ID/name mapping only; the Windows device-loss report is separate.")],
            [Action("engage-effects", "Compare individual effects and exact drivers", "reported_workaround", "contradictory_reports",
                "Do not generalize a driver result from one skill or Android device to all effects, Windows or NVIDIA. Record the original driver and scene before any independent comparison.",
                [EdenIssue(512), CommentUrl(512, "5137849862")], ["Validate both named skills, other rendering, speed and sustained battles on the exact active device."])],
            ["Changing the host driver is outside the catalogue's apply scope."]),

        Issue("eden-492-totk-foliage", "Tears of the Kingdom: disappearing foliage and black terrain",
            [Tears], ["The Legend of Zelda: Tears of the Kingdom"], ["eden"], ["android", "windows"], "open_report",
            ["Foliage can vanish after traversal; terrain or shadows can become black or corrupt while collision remains."],
            "Reports span Android/Adreno 740 and a Windows 11/RTX 2080 Ti configuration, with several game versions through 1.4.3. CPU ASTC comparisons are mixed and can reduce performance substantially.",
            [Report(492, "2026-10-08T04:05:57Z", "open", "Multiple devices and scenes remain affected; this is not an Android-only report."),
                Comment(492, "4882262220", "Windows/RTX 2080 Ti report with game 1.4.3."),
                Comment(492, "5382374278", "One reporter isolates a CPU-versus-GPU ASTC difference with a large performance cost."),
                Comment(492, "5555810007", "A later scene still fails with CPU decoding."),
                IdentityReference(EdenIssue(616), "Title ID/name mapping only; the controller-applet report is separate.")],
            [Action("totk-astc-comparison", "Investigate texture decoding per scene", "reported_workaround", "contradictory_reports",
                "CPU ASTC is not a universal fix: later reports contradict it, and a separate Mario Kart/RADV report crashes in a CPU-async configuration. NX does not expose this stock texture-decoder switch.",
                [CommentUrl(492, "5382374278"), CommentUrl(492, "5555810007"), EdenIssue(685)],
                ["Require supported native control/readback and compare the same traversal, terrain, visual state and sustained speed before enabling any option."])],
            ["Distinguish the original Android report, later Windows report and exploratory cross-core comments; none qualifies all cores."]),

        Issue("citron-170-gameplay-speed", "Mario Kart 8 Deluxe: normal FPS with slow gameplay",
            [Kart], ["Mario Kart 8 Deluxe"], ["citron"], [], "open_report",
            ["A reporter describes approximately half-speed gameplay while a counter displays 60 FPS."],
            "Citron-neo version/hardware details are incomplete; comparisons with an earlier build do not provide a full backend/main-build qualification.",
            [CitronReport(170, "2026-06-10T02:00:12Z", "open", "A normal displayed frame rate does not establish correct gameplay speed; no confirmed general repair appears in the reviewed discussion."),
                IdentityReference(EdenIssue(706), "Mario Kart title ID mapping only; this separate report does not transfer Eden behavior to Citron.")],
            [Action("kart-speed-check", "Measure guest time against wall time", "validation", "unverified",
                "Keep normal guest synchronization and clock behavior. An FPS unlock or an unbounded setting cannot validate speed and can change simulation behavior.",
                [CitronIssue(170)], ["Compare race timer, wall time, movement, animation, physics, input, audio, saves and loading alongside actual present intervals."])],
            ["Displayed FPS and similar screenshots cannot establish gameplay parity or a performance improvement."]),

        Issue("citron-53-bezel-black-rendering", "Bezel-engine titles: black textures",
            [], ["Even if TEMPEST", "Diabolik Lovers Grand Edition"], ["citron"], ["android"], "open_report",
            ["Some reported visual-novel scenes render black textures."],
            "Initial OnePlus 13 / Snapdragon 8 Elite report; later comments include another Snapdragon device and a PC comparison. Exact title IDs/builds and the driver-versus-emulator cause remain unconfirmed.",
            [CitronReport(53, "2026-03-22T16:39:33Z", "open", "A rendering report with disputed cause; a device or engine label is not proof that every related title is affected.")],
            [Action("bezel-render-capture", "Resolve the exact title and rendering cause", "native_fix_investigation", "unverified",
                "Establish a reproducible scene and inspect native rendering evidence before blaming a driver or automatically changing cores.",
                [CitronIssue(53)], ["Record the exact title/main build, active device, driver and backend revision; validate the full scene and transitions."])],
            ["No title-ID match is manufactured from a game name; these reports remain visible in the catalogue overview."]),

        Issue("citron-56-smash-wayland", "Smash Ultimate: NVIDIA/Wayland startup failure",
            [Smash], ["Super Smash Bros. Ultimate"], ["citron"], ["linux"], "open_report",
            ["A stock-frontend report describes a black screen and invalid timing after cache preparation."],
            "NVIDIA with Wayland; the reporter compares an earlier Citron version. A maintainer suggested X11, but the reviewed thread does not confirm a resolved exact configuration.",
            [CitronReport(56, "2026-03-22T09:20:24Z", "open", "Startup failure with an unconfirmed X11/session suggestion."),
                IdentityReference(EdenIssue(694), "Smash title ID mapping only; the Eden mod/network failure is separate.")],
            [Action("smash-session-comparison", "Verify native window and session behavior", "reported_workaround", "unverified",
                "NX uses its own native surface path. A stock Qt frontend suggestion is not an established NX workaround and does not justify silently changing session variables or deleting caches.",
                [CitronIssue(56)], ["Compare supported native window paths with the exact driver and build, then verify a sustained match and input."])],
            ["Keep this startup issue separate from Eden's white-stage reports."]),

        Issue("eden-694-smash-mod-network", "Smash Ultimate: mod-related network crash",
            [Smash], ["Super Smash Bros. Ultimate"], ["eden"], ["windows"], "open_report",
            ["A reported ARCropolis/Skyline session panics during a network/TLS path."],
            "Smash 13.0.5 with mods and airplane mode off, Windows 10 and RTX 3060 Ti. A reporter supplies full good/bad Eden revisions, but no full main-build or complete mod binding.",
            [Report(694, "2026-10-13T17:34:23Z", "open", "A mod/network regression report; its supplied revision comparison is not a qualified fix or a base-game rendering diagnosis.")],
            [Action("smash-mod-inventory", "Review the exact active mod set", "mod_conflict_review", "unverified",
                "Inventory mod versions and compare a separately preserved original session. Do not conflate this failure with white-stage rendering or bypass content and network entitlement checks.",
                [EdenIssue(694)], ["Reproduce the documented mod/network transition with exact revisions; verify save continuity and an original-settings comparison."])],
            ["A pair of source revisions is insufficient without exact executable, mod and hardware evidence."]),

        Issue("eden-685-kart-async-astc", "Mario Kart 8 Deluxe: CPU-async ASTC/RADV crash",
            [Kart], ["Mario Kart 8 Deluxe"], ["eden"], ["linux"], "open_report",
            ["A reported CPU-async texture-decoding configuration crashes shortly after startup."],
            "Bazzite/Fedora 44, Ryzen Z2 Extreme, Radeon 890M and RADV 26.2.2; September nightly short revisions. One GPU-decoding comparison lasted four minutes, which does not establish sustained stability.",
            [Report(685, "2026-09-29T00:01:02Z", "open", "Repeated CPU-async configuration failures; the reporter's inferred image-barrier cause lacks a Vulkan-validation confirmation."),
                IdentityReference(EdenIssue(706), "Mario Kart title ID mapping only; the save/user report is separate.")],
            [Action("kart-astc-native-trace", "Investigate the native texture-decoding failure", "native_fix_investigation", "unverified",
                "Obtain a native validation trace or an authored image/barrier regression before adopting the proposed cause. This report prevents treating CPU ASTC as a universal safe graphics fix.",
                [EdenIssue(685), EdenIssue(492)], ["Compare exact decoder settings and consumer readback, then validate sustained races, graphics, speed and saves."])],
            ["A four-minute non-crashing comparison and stripped-disassembly inference do not qualify a fix."])
    });

    private static CompatibilityIssue Issue(string id, string title, string[] titleIds, string[] gameNames,
        string[] backendIds, string[] platforms, string status, string[] symptoms, string environment,
        CompatibilityEvidence[] evidence, CompatibilityAction[] actions, string[] validation) => new()
    {
        Id = id, Title = title, TitleIds = Freeze(titleIds), GameNames = Freeze(gameNames), BackendIds = Freeze(backendIds),
        ReportedPlatforms = Freeze(platforms), Status = status, Symptoms = Freeze(symptoms), ReportedEnvironment = environment,
        Evidence = Freeze(evidence), Actions = Freeze(actions), ValidationNeeded = Freeze(RequiredValidation.Concat(validation)), CheckedUtc = CheckedUtc
    };

    private static CompatibilityAction Action(string id, string label, string kind, string status, string explanation,
        string[] urls, string[] validation, string? settingId = null, string? capability = null, string? auditId = null) => new()
    {
        Id = id, Label = label, Kind = kind, Status = status, Explanation = explanation, SettingId = settingId,
        RequiredCapability = capability, SourceAuditId = auditId, EvidenceUrls = Freeze(urls), ValidationNeeded = Freeze(validation)
    };
    private static CompatibilityEvidence Report(int number, string updated, string state, string summary) =>
        new(EdenIssue(number), "upstream_issue", summary, state, ParseDate(updated));
    private static CompatibilityEvidence CitronReport(int number, string updated, string state, string summary) =>
        new(CitronIssue(number), "upstream_issue", summary, state, ParseDate(updated));
    private static CompatibilityEvidence Comment(int issue, string comment, string summary) =>
        new(CommentUrl(issue, comment), "upstream_comment", summary, "reporter_observation");
    private static CompatibilityEvidence IdentityReference(string url, string summary) => new(url, "title_identity_reference", summary, "identity_label_only");
    private static CompatibilityEvidence SourceReference(string url, string summary) => new(url, "pinned_upstream_source", summary, "source_presence_only");
    private static string EdenIssue(int issue) => $"https://github.com/eden-emulator/Issue-Reports/issues/{issue}";
    private static string CitronIssue(int issue) => $"https://github.com/citron-neo/emulator/issues/{issue}";
    private static string CommentUrl(int issue, string comment) => EdenIssue(issue) + "#issuecomment-" + comment;
    private static string EdenSource(string path, string anchor) => $"https://github.com/eden-emulator/mirror/blob/{EdenRevision}/src/{path}#{anchor}";
    private static string CitronSource(string path, string anchor) => $"https://github.com/citron-neo/emulator/blob/{CitronRevision}/src/{path}#{anchor}";
    private static DateTimeOffset ParseDate(string value) => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
    private static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values) => Array.AsReadOnly(values.ToArray());
    private static bool FullRevision(string? value) => FullHex(value, 40) || FullHex(value, 64);
    private static bool FullHex(string? value, int length) => value?.Length == length && value.Any(c => c != '0') && value.All(Uri.IsHexDigit);
    private static bool HasText(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && !value.Any(char.IsControl);
}
