using System.Security.Cryptography;
using System.Text.Json;
using NxInfinity.Runtime.Services;

internal static partial class Program
{
    // These hashes were recorded before the recovery. The test creates new local
    // serialization and compares it with the recorded originals; it does not
    // reconstruct or claim recovery of a lost historical test report.
    private static readonly (string Id, string ProfileSha256, string EnvelopeSha256)[] CanonicalEnhancements =
    [
        ("pokemon-scarlet-eden-baseline", "ae6bb99a3495c9fa5c800492e1bac7a72739134f575c992113d33e9a95ed7c60", "07c6d8a02558f0d39d18a5488edeea2a11ae42b7f7cf398b135c197bea30d77b"),
        ("pokemon-scarlet-ryujinx-baseline", "609454fb1e17486cc25156f21f07513da6d0d722fac64af87ea889708740eb37", "09a64000bb0db7fe22897550fb0b87471db1f5f9689f2cbc9ca1bd4d46000a82"),
        ("pokemon-violet-eden-baseline", "165b56e59cde1a7da06c0e52c1823b581001f93d98f34a49ba5cfd83987563a6", "c85c9fb98b97d93b730475cfc9012156702941737c302f582365ed759bb4901d"),
        ("pokemon-violet-ryujinx-baseline", "bfc3ecd43cc25ea29453f80e22287f328335612d3142abc1dbef24cafbff4164", "ae4ba6d8d6901c395bb3a17bbb926f9428ceeb2b3091dfc562fef55022dc582e")
    ];

    private static string EnhancementHash<T>(T value) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Json)));

    private static void EnhancementCanonicalCompatibility()
    {
        using var f = new Fixture();
        var store = new EnhancementStore(f.Workspace);
        var templates = HistoricalEnhancementBaselines.CreateResearchTemplates();
        foreach (var expected in CanonicalEnhancements)
        {
            var template = templates.Single(x => x.ProfileId == expected.Id);
            store.Put(template);
            var plan = store.Evaluate(template.ProfileId, template.Version, EnhancementSession(), allowExperimental: true);
            Check(plan.ProfileSha256 == expected.ProfileSha256);
            var path = Path.Combine(f.Root, "nx-services", "enhancements", "profiles", expected.Id, "0.1.0-research.json");
            var bytes = File.ReadAllBytes(path);
            Check(Convert.ToHexStringLower(SHA256.HashData(bytes)) == expected.EnvelopeSha256);
            using var json = JsonDocument.Parse(bytes);
            var value = json.RootElement.GetProperty("profile");
            Check(!value.TryGetProperty("backendAdapterVersion", out _) && !value.TryGetProperty("backendArtifactManifestSha256", out _));
            var reopened = new EnhancementStore(new NxWorkspace(f.Root));
            reopened.Put(template);
            Check(File.ReadAllBytes(path).SequenceEqual(bytes));
            Check(reopened.Get(expected.Id, "0.1.0-research").BuildId is null);
        }
    }

    private static void EnhancementCoreUpgrade()
    {
        using var f = new Fixture();
        var store = new EnhancementStore(f.Workspace);
        var first = EnhancementBaselines.CreateResearchTemplates(new string('a', 40));
        var legacy = first.Select(p => p with { Version = "0.1.0-research" }).ToArray();
        foreach (var p in legacy) store.Put(p);
        var oldBytes = Directory.GetFiles(Path.Combine(f.Root, "nx-services", "enhancements", "profiles"), "*.json", SearchOption.AllDirectories)
            .ToDictionary(p => p, File.ReadAllBytes);
        foreach (var revision in new[] { new string('a', 40), new string('b', 40), new string('a', 40) })
            foreach (var profile in EnhancementBaselines.CreateResearchTemplates(revision)) store.Put(profile);
        Check(store.List().Count == 6);
        Check(oldBytes.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)));
        Check(store.List().All(p => p.Status == EnhancementStatus.ResearchCandidate && p.BuildId is null));
    }

    private static void EnhancementWindowsCompatibility()
    {
        using var f = new Fixture();
        var store = new EnhancementStore(f.Workspace);
        var profile = store.Put(EnhancementDefinition());
        var windows = new JsonSerializerOptions(Json) { NewLine = "\r\n" };
        string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, windows)));
        var profilePath = Path.Combine(f.Root, "nx-services", "enhancements", "profiles", profile.ProfileId, profile.Version + ".json");
        var original = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, sha256 = Hash(profile), profile }, windows);
        File.WriteAllBytes(profilePath, original);
        var reopened = new EnhancementStore(new NxWorkspace(f.Root));
        reopened.Put(profile);
        Check(File.ReadAllBytes(profilePath).SequenceEqual(original));
        var plan = reopened.Evaluate(profile.ProfileId, profile.Version, EnhancementSession());
        Check(plan.ProfileSha256 == EnhancementHash(profile));
        var receipt = reopened.Prepare(plan, "windows-compatibility", EnhancementOriginals());
        receipt = receipt with { ProfileSha256 = Hash(profile) };
        var receiptPath = Path.Combine(f.Root, "nx-services", "enhancements", "receipts", receipt.ReceiptId + ".json");
        File.WriteAllBytes(receiptPath, JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, sha256 = Hash(receipt), receipt }, windows));
        Check(reopened.GetReceipt(receipt.ReceiptId).ProfileSha256 == Hash(profile));
        Check(reopened.RecordOutcome(receipt.ReceiptId, EnhancementReceiptStatus.RolledBack, "Historical Windows fixture rollback.").Status == EnhancementReceiptStatus.RolledBack);
        Check(!File.ReadAllText(receiptPath).Contains('\r'));
        var changed = profile with { DisplayName = profile.DisplayName + " tampered" };
        File.WriteAllBytes(profilePath, JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, sha256 = Hash(profile), profile = changed }, windows));
        Error(ServiceError.CorruptData, () => reopened.Get(profile.ProfileId, profile.Version));
    }

    private static void EnhancementArtifactIdentity()
    {
        using var f = new Fixture();
        var store = new EnhancementStore(f.Workspace);
        store.Put(EnhancementDefinition());
        Check(EnhancementPlanFor(store, EnhancementSession() with { BackendArtifactManifestSha256 = new string('c', 64) }).Applicable);
        foreach (var adapter in new string?[] { null, "", "Fixture.1", "fixture.2" })
            Check(Issue(EnhancementPlanFor(store, EnhancementSession() with { BackendAdapterVersion = adapter }), "backend_adapter_mismatch_or_unknown"));
        foreach (var manifest in new string?[] { null, "unknown", new('0', 64), new('D', 64) })
            Check(Issue(EnhancementPlanFor(store, EnhancementSession() with { BackendArtifactManifestSha256 = manifest }), "backend_artifact_manifest_mismatch_or_unknown"));
        Error(ServiceError.InvalidInput, () => store.Put(EnhancementDefinition() with { Version = "bad-adapter", BackendAdapterVersion = "fixture version" }));
        Error(ServiceError.InvalidInput, () => store.Put(EnhancementDefinition() with { Version = "long-adapter", BackendAdapterVersion = new string('a', 129) }));
        Error(ServiceError.InvalidInput, () => store.Put(EnhancementDefinition() with { Version = "bad-digest", BackendArtifactManifestSha256 = new string('0', 64) }));
        Error(ServiceError.InvalidInput, () => store.Put(EnhancementDefinition() with { Version = "short-digest", BackendArtifactManifestSha256 = "ABCD" }));
        store.Put(EnhancementDefinition() with { Version = "unbound", BackendAdapterVersion = null, BackendArtifactManifestSha256 = null });
        var unbound = store.Evaluate("fixture-profile", "unbound", EnhancementSession(), allowExperimental: true);
        Check(Issue(unbound, "backend_adapter_unbound") && Issue(unbound, "backend_artifact_manifest_unbound"));
        Error(ServiceError.Unavailable, () => store.Prepare(unbound, "session", EnhancementOriginals()));
    }

    private static void EnhancementLegacyReadability()
    {
        using var f = new Fixture();
        var store = new EnhancementStore(f.Workspace);
        var profile = store.Put(EnhancementDefinition() with { BackendAdapterVersion = null, BackendArtifactManifestSha256 = null });
        var context = EnhancementSession() with { BackendAdapterVersion = null, BackendArtifactManifestSha256 = null };
        var now = DateTimeOffset.Parse("2026-10-19T00:00:00Z");
        var receipt = new EnhancementApplicationReceipt
        {
            ReceiptId = Guid.NewGuid().ToString("N"), SessionId = "legacy-fixture", Profile = profile,
            ProfileSha256 = EnhancementHash(profile), Context = context, SelectedChangeIds = ["volume"],
            OriginalSettings = EnhancementOriginals(), Status = EnhancementReceiptStatus.Applied,
            CreatedUtc = now, UpdatedUtc = now.AddSeconds(1), History =
            [
                new(EnhancementReceiptStatus.Prepared, now, "Authored legacy-format fixture; no emulator mutation."),
                new(EnhancementReceiptStatus.Applied, now.AddSeconds(1), "Authored old-format receipt; no historical execution claim.")
            ]
        };
        var path = Path.Combine(f.Root, "nx-services", "enhancements", "receipts", receipt.ReceiptId + ".json");
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, sha256 = EnhancementHash(receipt), receipt }, Json));
        var reopened = new EnhancementStore(new NxWorkspace(f.Root));
        var read = reopened.GetReceipt(receipt.ReceiptId);
        Check(read.Profile.BackendAdapterVersion is null && read.Context.BackendArtifactManifestSha256 is null && !read.GameplayVerified);
        Check(!reopened.Evaluate(profile.ProfileId, profile.Version, EnhancementSession(), allowExperimental: true).Applicable);
        Check(reopened.RecordOutcome(read.ReceiptId, EnhancementReceiptStatus.RolledBack, "Authored legacy receipt transition only.").Status == EnhancementReceiptStatus.RolledBack);
    }

    private static void EnhancementActiveArtifactIdentity()
    {
        foreach (var alterAdapter in new[] { true, false })
        {
            using var f = new Fixture();
            var store = new EnhancementStore(f.Workspace);
            var first = store.Put(EnhancementDefinition() with { ProfileId = "first" });
            var original = EnhancementSession();
            var receipt = store.Prepare(store.Evaluate(first.ProfileId, first.Version, original), "same-session", EnhancementOriginals());
            store.RecordOutcome(receipt.ReceiptId, EnhancementReceiptStatus.Applied, "Authored context guard fixture.");
            var changed = alterAdapter ? original with { BackendAdapterVersion = "fixture.2" } :
                original with { BackendArtifactManifestSha256 = new string('D', 64) };
            var second = store.Put(EnhancementDefinition() with
            {
                ProfileId = "second", BackendAdapterVersion = changed.BackendAdapterVersion,
                BackendArtifactManifestSha256 = changed.BackendArtifactManifestSha256,
                Changes = [SettingChange("scale", "graphics.scale")]
            });
            var plan = store.Evaluate(second.ProfileId, second.Version, changed);
            Check(plan.Applicable);
            EnhancementOriginalSetting[] originals = [new("graphics.scale", JsonSerializer.SerializeToElement(1))];
            Error(ServiceError.Conflict, () => store.Prepare(plan, "same-session", originals));
            Check(store.ListReceipts("same-session").Count == 1);
            Check(store.Prepare(plan, "new-session", originals).Status == EnhancementReceiptStatus.Prepared);
        }
    }
}
