using System.Text.Json;
using NxInfinity.Runtime.Services;

internal static partial class Program
{
    private const string EnhancementFixtureSource = "https://example.invalid/owned-synthetic-enhancement-fixture";
    private static EnhancementChange SettingChange(string id = "volume", string setting = "audio.volume") => new()
    {
        ChangeId = id, Kind = EnhancementChangeKind.Settings, Status = EnhancementStatus.Qualified,
        Source = EnhancementFixtureSource, Evidence = "Synthetic service test qualification only; no gameplay.",
        Description = "Owned scalar-setting fixture, not a recommended game enhancement.",
        Setting = new(setting, JsonSerializer.SerializeToElement(0.5))
    };
    private static EnhancementProfile EnhancementDefinition() => new()
    {
        ProfileId = "fixture-profile", Version = "1", DisplayName = "Synthetic enhancement fixture",
        TitleId = Title, BuildId = Binding.BuildId, BackendId = "fixture", BackendRevision = new string('A', 40),
        BackendAdapterVersion = "fixture.1", BackendArtifactManifestSha256 = new string('C', 64),
        Status = EnhancementStatus.Qualified, Source = EnhancementFixtureSource,
        Evidence = "Owned synthetic metadata; qualified describes this fixture, not an emulator.",
        OriginalBehavior = "Original scalar remains 1.0 until a hypothetical test coordinator changes it.",
        RollbackNotes = "Restore the exact recorded original; this service does not mutate a runtime.",
        Changes = [SettingChange()]
    };
    private static EnhancementContext EnhancementSession() => new()
    {
        TitleId = Title, BuildId = Binding.BuildId, BackendId = "fixture", BackendRevision = new string('A', 40), IdentityVerified = true,
        BackendAdapterVersion = "fixture.1", BackendArtifactManifestSha256 = new string('C', 64)
    };
    private static EnhancementPlan EnhancementPlanFor(EnhancementStore store, EnhancementContext? context = null, bool experimental = false,
        IReadOnlyList<string>? selected = null) => store.Evaluate("fixture-profile", "1", context ?? EnhancementSession(), selected, experimental);
    private static EnhancementOriginalSetting[] EnhancementOriginals() => [new("audio.volume", JsonSerializer.SerializeToElement(1.0), JsonSerializer.SerializeToElement(0.75))];
    private static bool Issue(EnhancementPlan plan, string code) => plan.Issues.Any(i => i.Code == code);

    private static void EnhancementCatalog()
    {
        using var f = new Fixture();
        var store = new EnhancementStore(f.Workspace);
        var original = EnhancementDefinition();
        store.Put(original with { TitleId = Title.ToLowerInvariant(), BackendRevision = new string('a', 40) });
        store = new EnhancementStore(new NxWorkspace(f.Root));
        Check(store.List().Count == 1 && store.Get(original.ProfileId, original.Version).BackendRevision == new string('A', 40));
        var plan = EnhancementPlanFor(store);
        Check(plan.Applicable && plan.ProfileSha256.Length == 64 && plan.Changes.Single().Setting!.Value.GetDouble() == 0.5);
        store.Put(original);
        Error(ServiceError.Conflict, () => store.Put(original with { Status = EnhancementStatus.Experimental }));
        Check(EnhancementPlanFor(store).Applicable);
        store.Put(original with { Version = "2", Status = EnhancementStatus.Experimental });
        Check(store.List().Count == 2 && store.Get(original.ProfileId, "1").Status == EnhancementStatus.Qualified);
        var incoming = original with { Version = "3" };
        File.WriteAllText(Path.Combine(f.Workspace.ImportsPath, "enhancement.json"), JsonSerializer.Serialize(incoming, Json));
        Check(store.ImportFromInbox("enhancement.json").Version == "3");
        Error(ServiceError.UnsafePath, () => store.ImportFromInbox("../escape.json"));
        var unfinished = Path.Combine(f.Root, "nx-services", "enhancements", "profiles", original.ProfileId, ".write-" + new string('0', 32) + ".tmp");
        File.WriteAllText(unfinished, "unfinished write");
        Check(store.List().Count == 3);
    }

    private static void EnhancementResearchGuard()
    {
        using var f = new Fixture();
        var store = new EnhancementStore(f.Workspace);
        var templates = EnhancementBaselines.CreateResearchTemplates();
        foreach (var template in templates)
        {
            Check(template.BuildId is null && template.BackendAdapterVersion is null && template.BackendArtifactManifestSha256 is null);
            Check(template.Changes.All(x => x.Setting is null && x.PatchReceiptId is null && x.ContentId is null));
            store.Put(template);
            var known = EnhancementSession() with { TitleId = template.TitleId, BackendId = template.BackendId, BackendRevision = template.BackendRevision };
            var plan = store.Evaluate(template.ProfileId, template.Version, known, ["settings-research"], allowExperimental: true);
            Check(!plan.Applicable && Issue(plan, "research_candidate") && Issue(plan, "build_mismatch_or_unknown"));
            Error(ServiceError.Unavailable, () => store.Prepare(plan, "session", []));
        }
        Check(store.List().Count == 2 && templates.All(x => x.BackendId == "nxi"));
        Check(EnhancementBaselines.CreateResearchTemplates(new string('b', 40)).All(x => x.BackendRevision == new string('b', 40)));
        Error(ServiceError.InvalidInput, () => store.Put(EnhancementDefinition() with { BuildId = null }));
        Error(ServiceError.InvalidInput, () => store.Put(EnhancementDefinition() with { BuildId = "0123456789ABCDEF" }));
        Error(ServiceError.InvalidInput, () => store.Put(EnhancementDefinition() with { BuildId = new string('0', 64) }));
        Error(ServiceError.InvalidInput, () => store.Put(EnhancementDefinition() with { TitleId = new string('0', 16) }));
    }

    private static void EnhancementIdentity()
    {
        using var f = new Fixture();
        var store = new EnhancementStore(f.Workspace); store.Put(EnhancementDefinition());
        foreach (var context in new[]
        {
            EnhancementSession() with { IdentityVerified = false }, EnhancementSession() with { TitleId = null },
            EnhancementSession() with { TitleId = OtherTitle }, EnhancementSession() with { BuildId = null },
            EnhancementSession() with { BuildId = "unknown" }, EnhancementSession() with { BuildId = new string('B', 64) },
            EnhancementSession() with { BackendId = "another" }, EnhancementSession() with { BackendRevision = new string('B', 40) }
        }) Check(!EnhancementPlanFor(store, context).Applicable);
        Check(EnhancementPlanFor(store, EnhancementSession() with { BuildId = Binding.BuildId.ToLowerInvariant() }).Applicable);
    }

    private static void EnhancementHardware()
    {
        using var f = new Fixture();
        var store = new EnhancementStore(f.Workspace);
        store.Put(EnhancementDefinition() with
        {
            Hardware = new() { VendorId = "10de", DeviceId = "0001", DriverVersions = ["1.2.3"] }
        });
        var matching = EnhancementSession() with { Hardware = new("10DE", "0001", "1.2.3"), HardwareVerified = true };
        Check(EnhancementPlanFor(store, matching).Applicable);
        Check(!EnhancementPlanFor(store, matching with { HardwareVerified = false }).Applicable);
        Check(!EnhancementPlanFor(store, matching with { Hardware = null }).Applicable);
        Check(!EnhancementPlanFor(store, matching with { Hardware = new("1002", "0001", "1.2.3") }).Applicable);
        Check(!EnhancementPlanFor(store, matching with { Hardware = new("10DE", "0002", "1.2.3") }).Applicable);
        Check(!EnhancementPlanFor(store, matching with { Hardware = new("10DE", "0001", "1.2.4") }).Applicable);
        Error(ServiceError.InvalidInput, () => store.Put(EnhancementDefinition() with { Version = "bad", Hardware = new() { DriverVersions = ["1.*"] } }));
        Error(ServiceError.InvalidInput, () => store.Put(EnhancementDefinition() with { Version = "bad", Hardware = new() { DeviceId = "0001" } }));
    }

    private static void EnhancementClassification()
    {
        using var f = new Fixture();
        var store = new EnhancementStore(f.Workspace);
        var changes = new List<EnhancementChange> { SettingChange() };
        foreach (var kind in new[] { EnhancementChangeKind.GamePatch, EnhancementChangeKind.UnsupportedPlatform, EnhancementChangeKind.SeparatelySuppliedContent })
            changes.Add(SettingChange(kind.ToString().ToLowerInvariant()) with { Kind = kind, Setting = null });
        store.Put(EnhancementDefinition() with { Changes = changes });
        var whole = EnhancementPlanFor(store);
        Check(!whole.Applicable && Issue(whole, "game_patch_adapter_unintegrated") && Issue(whole, "unsupported_platform") && Issue(whole, "separately_supplied_content"));
        Check(EnhancementPlanFor(store, selected: ["volume"]).Applicable);
        Check(!EnhancementPlanFor(store, selected: []).Applicable);
        Error(ServiceError.InvalidInput, () => store.Put(EnhancementDefinition() with
        { Version = "bad-object", Changes = [SettingChange() with { Setting = new("audio.volume", JsonSerializer.SerializeToElement(new { shell = "a command" })) }] }));
        Error(ServiceError.InvalidInput, () => store.Put(EnhancementDefinition() with
        { Version = "bad-command", Changes = [SettingChange() with { Setting = new("audio.volume", JsonSerializer.SerializeToElement("sh -c something")) }] }));
    }

    private static void EnhancementRequirements()
    {
        using var f = new Fixture();
        var store = new EnhancementStore(f.Workspace);
        var patch = new string('a', 32);
        store.Put(EnhancementDefinition() with
        {
            DependsOn = ["base-profile"], ConflictsWith = ["bad-profile", "patch:" + patch],
            Changes = [SettingChange("second", "system.docked") with { DependsOn = ["first"] }, SettingChange("first")]
        });
        Check(Issue(EnhancementPlanFor(store), "dependency_missing"));
        var context = EnhancementSession() with { ActiveProfileIds = ["base-profile"] };
        var valid = EnhancementPlanFor(store, context);
        Check(valid.Applicable && valid.Changes[0].ChangeId == "first");
        Check(Issue(EnhancementPlanFor(store, context, selected: ["second"]), "dependency_missing"));
        Check(Issue(EnhancementPlanFor(store, context with { ActiveProfileIds = ["base-profile", "bad-profile"] }), "conflict"));
        Check(Issue(EnhancementPlanFor(store, context with { ActivePatchReceiptIds = [patch] }), "conflict"));
        store.Put(EnhancementDefinition() with { Version = "patch-action", Changes = [SettingChange() with { ConflictsWith = ["patch:" + patch] }] });
        Check(Issue(store.Evaluate("fixture-profile", "patch-action", context with { ActivePatchReceiptIds = [patch] }), "conflict"));
        Error(ServiceError.InvalidInput, () => store.Put(EnhancementDefinition() with
        { Version = "cyclic", Changes = [SettingChange("a") with { DependsOn = ["b"] }, SettingChange("b", "system.docked") with { DependsOn = ["a"] }] }));
        store.Put(EnhancementDefinition() with { Version = "duplicate-setting", Changes = [SettingChange("a"), SettingChange("b")] });
        Check(Issue(store.Evaluate("fixture-profile", "duplicate-setting", context), "setting_conflict"));
    }

    private static void EnhancementOptIn()
    {
        using var f = new Fixture();
        var store = new EnhancementStore(f.Workspace);
        store.Put(EnhancementDefinition() with { Status = EnhancementStatus.Experimental, Changes = [SettingChange() with { Status = EnhancementStatus.Experimental }] });
        Check(Issue(EnhancementPlanFor(store), "experimental_opt_in_required"));
        Error(ServiceError.Unavailable, () => store.Prepare(EnhancementPlanFor(store), "session", EnhancementOriginals()));
        var plan = EnhancementPlanFor(store, experimental: true);
        var receipt = store.Prepare(plan, "session", EnhancementOriginals());
        Check(receipt.ExperimentalOptIn && receipt.Status == EnhancementReceiptStatus.Prepared && !receipt.GameplayVerified);
        Check(!EnhancementPlanFor(store).Applicable);
    }

    private static void EnhancementActiveConflicts()
    {
        using var f = new Fixture();
        var store = new EnhancementStore(f.Workspace);
        store.Put(EnhancementDefinition() with { ProfileId = "alpha", ConflictsWith = ["bravo"] });
        store.Put(EnhancementDefinition() with { ProfileId = "bravo", Changes = [SettingChange("scale", "graphics.scale")] });
        store.Put(EnhancementDefinition() with { ProfileId = "charlie" });
        var active = store.Prepare(store.Evaluate("alpha", "1", EnhancementSession()), "session", EnhancementOriginals());
        store.RecordOutcome(active.ReceiptId, EnhancementReceiptStatus.Applied, "Synthetic fixture only.");
        var bravo = store.Evaluate("bravo", "1", EnhancementSession());
        EnhancementOriginalSetting[] scale = [new("graphics.scale", JsonSerializer.SerializeToElement(1))];
        Error(ServiceError.Conflict, () => store.Prepare(bravo, "session", scale));
        Error(ServiceError.Conflict, () => store.Prepare(store.Evaluate("charlie", "1", EnhancementSession()), "session", EnhancementOriginals()));
        store.RecordOutcome(active.ReceiptId, EnhancementReceiptStatus.RolledBack, "Synthetic original restored.");
        Check(store.Prepare(bravo, "session", scale).Status == EnhancementReceiptStatus.Prepared);
    }

    private static void EnhancementReceipts()
    {
        using var f = new Fixture();
        var store = new EnhancementStore(f.Workspace); store.Put(EnhancementDefinition());
        var plan = EnhancementPlanFor(store);
        Error(ServiceError.InvalidInput, () => store.Prepare(plan, "session", []));
        Error(ServiceError.InvalidInput, () => store.Prepare(plan, "session", [new("wrong.setting", JsonSerializer.SerializeToElement(1))]));
        var receipt = store.Prepare(plan, "session", EnhancementOriginals());
        Error(ServiceError.RecoveryRequired, () => store.Prepare(plan, "session", EnhancementOriginals()));
        var applied = store.RecordOutcome(receipt.ReceiptId, EnhancementReceiptStatus.Applied, "Fixture coordinator attestation only.");
        Error(ServiceError.Unavailable, () => store.Prepare(plan, "session", EnhancementOriginals()));
        var recovery = store.RecordOutcome(receipt.ReceiptId, EnhancementReceiptStatus.RecoveryRequired, "Synthetic rollback needs recovery.");
        Error(ServiceError.RecoveryRequired, () => store.Prepare(plan, "session", EnhancementOriginals()));
        var rolledBack = store.RecordOutcome(receipt.ReceiptId, EnhancementReceiptStatus.RolledBack, "Fixture original state acknowledged, no emulator.");
        store = new EnhancementStore(new NxWorkspace(f.Root));
        var saved = store.GetReceipt(receipt.ReceiptId);
        Check(saved.Status == EnhancementReceiptStatus.RolledBack && saved.History.Count == 4 && !saved.GameplayVerified);
        Check(saved.OriginalSettings[0].Effective.GetDouble() == 1 && saved.OriginalSettings[0].Persisted!.Value.GetDouble() == .75);
        Check(saved.Profile.OriginalBehavior == plan.Profile.OriginalBehavior && saved.ProfileSha256 == plan.ProfileSha256);
        Error(ServiceError.Conflict, () => store.RecordOutcome(receipt.ReceiptId, EnhancementReceiptStatus.Applied, "Cannot reapply a rolled-back receipt."));
        Check(store.ListReceipts("session").Count == 1);
        var next = store.Prepare(plan, "session", EnhancementOriginals());
        store.RecordOutcome(next.ReceiptId, EnhancementReceiptStatus.Failed, "No mutation was attempted by this fixture.");
    }

    private static void EnhancementCorruption()
    {
        using var f = new Fixture();
        var store = new EnhancementStore(f.Workspace); store.Put(EnhancementDefinition());
        var profilePath = Path.Combine(f.Root, "nx-services", "enhancements", "profiles", "fixture-profile", "1.json");
        var original = File.ReadAllText(profilePath);
        File.WriteAllText(profilePath, original.Replace("Synthetic enhancement fixture", "Corrupted enhancement fixture"));
        var corrupted = File.ReadAllText(profilePath);
        Error(ServiceError.CorruptData, () => EnhancementPlanFor(store));
        Error(ServiceError.CorruptData, () => store.Put(EnhancementDefinition()));
        Check(File.ReadAllText(profilePath) == corrupted);
        File.WriteAllText(profilePath, original);
        var receipt = store.Prepare(EnhancementPlanFor(store), "session", EnhancementOriginals());
        var receiptPath = Path.Combine(f.Root, "nx-services", "enhancements", "receipts", receipt.ReceiptId + ".json");
        var receiptBytes = File.ReadAllText(receiptPath);
        File.WriteAllText(receiptPath, receiptBytes.Replace("\"effective\": 1", "\"effective\": 2"));
        Error(ServiceError.CorruptData, () => store.GetReceipt(receipt.ReceiptId));
        Error(ServiceError.CorruptData, () => store.RecordOutcome(receipt.ReceiptId, EnhancementReceiptStatus.Applied, "Corrupt originals must be preserved and blocked."));
        File.WriteAllText(receiptPath, receiptBytes);
        Error(ServiceError.InvalidInput, () => store.Get("../escape", "1"));
        if (!OperatingSystem.IsWindows())
        {
            var copy = Path.Combine(f.Root, "profile-copy.json"); File.WriteAllText(copy, original);
            File.Delete(profilePath); File.CreateSymbolicLink(profilePath, copy);
            Error(ServiceError.UnsafePath, () => store.Get("fixture-profile", "1"));
            File.Delete(profilePath); File.WriteAllText(profilePath, original);
        }
    }

    private static void EnhancementProcessLock()
    {
        using var f = new Fixture();
        var store = new EnhancementStore(f.Workspace);
        var path = Path.Combine(f.Root, "nx-services", "locks", "enhancements.lock");
        using (var child = HoldLock(path)) Error(ServiceError.Busy, () => store.Put(EnhancementDefinition()));
        store.Put(EnhancementDefinition());
        using (var child = HoldLock(path)) Error(ServiceError.Busy, () => EnhancementPlanFor(store));
    }
}
