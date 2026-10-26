using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using NxInfinity.Runtime.Services;

internal static partial class Program
{
    private static readonly string LibraryRevision = new('B', 40);
    private static readonly (string Name, Action Run)[] LibraryTests =
    [
        ("launch version receipts survive reopening and reject equal-time replacements", LaunchVersionReceipt),
        ("launch version cache corruption cancellation and unsupported filesystems fall back", LaunchVersionFallback),
        ("Windows journal records reject truncated unknown and zero versions", LaunchVersionJournalParsing),
        ("automatic scans skip unchanged unidentified content; manual scans retry", StartupScanDoesNotRetryUnidentified),
        ("Windows file-version integration (explicitly skipped on other hosts)", WindowsLaunchVersionIntegration),
        ("library registration hashes explicit files without copying or inferring identity", LibraryRegister),
        ("library search favorites tags and settings survive reopening", LibraryMetadata),
        ("library settings are revision-bound scalar requests", LibrarySettings),
        ("library core observations require exact content binding and provenance", LibraryObservation),
        ("library launch records preserve failed and unfinished outcomes", LibraryLaunchHistory),
        ("library update and DLC associations do not install or infer compatibility", LibraryAssociations),
        ("library launch verification detects equal-size equal-time content replacement", LibraryChangedContent),
        ("library hashing cancellation preserves the index", LibraryCancellation),
        ("library metadata remains browsable when registered content disappears", LibraryMissing),
        ("library discovery is bounded and never follows links", LibraryDiscovery),
        ("library registrations reject traversal links and unsupported content", LibraryPaths),
        ("library corruption hashes and duplicate keys block writes without reset", LibraryCorruption),
        ("library writes honor a real second-process lock", LibraryProcessLock),
        ("library artwork is bounded copied and hash-checked", LibraryArtworkIntegrity),
        ("native save observations require exact backend pin existing directories and provenance", NativeSaveMapping),
        ("native mod plans expose only enabled owned hash-checked package directories", NativeModMounts),
        ("save backup catalogs verify exact identities payloads and the stopped-runtime lease", SaveBackupCatalog),
        ("save backup catalogs reject unexpected entries and bounded overflows", SaveBackupCatalogBounds)
    ];

    private static GameLibraryEntry Register(GameLibraryStore store, string path, LibraryContentKind kind = LibraryContentKind.Game,
        string? name = null, string? related = null) => store.RegisterAsync(new(path, kind, name, related)).GetAwaiter().GetResult();
    private static string LibraryFile(Fixture f, string name = "Example [0100000000000001].nro", string bytes = "authored registration fixture")
    {
        // Deliberately outside nx-services/imports: a user-selected original remains read-only service input.
        var root = Path.Combine(f.Root, "user-content");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, name);
        File.WriteAllText(path, bytes);
        return path;
    }

    private static void LibraryRegister()
    {
        using var f = new Fixture();
        var path = LibraryFile(f);
        var store = new GameLibraryStore(f.Workspace);
        var progress = new InlineProgress<ContentHashProgress>();
        var entry = store.RegisterAsync(new(path), progress).GetAwaiter().GetResult();
        Check(entry.Content.Sha256 == Hash(File.ReadAllBytes(path)) && entry.Content.Size == new FileInfo(path).Length);
        Check(entry.Observations.Count == 0 && entry.Kind == LibraryContentKind.Game);
        Check(progress.Values[0].BytesRead == 0 && progress.Values[^1].BytesRead == entry.Content.Size);
        Check(Register(store, path).EntryId == entry.EntryId && store.List().Count == 1);
        Check(!Directory.EnumerateFiles(Path.Combine(f.Root, "nx-services"), "*.nro", SearchOption.AllDirectories).Any());
        var resolved = store.ResolveForLaunchAsync(entry.EntryId).GetAwaiter().GetResult();
        Check(resolved.AbsolutePath == path && resolved.VerifiedSha256 == entry.Content.Sha256);
        Check(store.Remove(entry.EntryId) && !store.Remove(entry.EntryId) && File.Exists(path));
    }

    private static void LibraryMetadata()
    {
        using var f = new Fixture();
        var store = new GameLibraryStore(f.Workspace);
        var a = Register(store, LibraryFile(f, "a.nro"), name: "Alpha");
        var b = Register(store, LibraryFile(f, "b.nro"), name: "Beta");
        store.SetMetadata(a.EntryId, "Alpha Game", ["Adventure", "Local"], "Private local note");
        store.SetFavorite(a.EntryId, true);
        store = new GameLibraryStore(new NxWorkspace(f.Root));
        Check(store.List(new(FavoritesOnly: true)).Single().EntryId == a.EntryId);
        Check(store.Search("alpha adventure").Single().EntryId == a.EntryId);
        Check(store.Search("PRIVATE").Single().EntryId == a.EntryId);
        Check(store.List(new(Offset: 1, Limit: 1)).Single().EntryId == b.EntryId);
        Error(ServiceError.InvalidInput, () => store.SetMetadata(a.EntryId, "Alpha", ["same", "SAME"]));
        Error(ServiceError.InvalidInput, () => store.List(new(Limit: 0)));
        Check(store.Get(a.EntryId).DisplayName == "Alpha Game");
    }

    private static void LibrarySettings()
    {
        using var f = new Fixture();
        var store = new GameLibraryStore(f.Workspace);
        var entry = Register(store, LibraryFile(f));
        using (var values = JsonDocument.Parse("{\"resolution_scale\":2,\"vsync\":true,\"language\":\"en-US\"}"))
            store.SetSettingRequests(entry.EntryId, "eden", LibraryRevision, values.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value));
        var same = new GameLibraryStore(f.Workspace).GetSettingRequests(entry.EntryId, "eden", LibraryRevision.ToLowerInvariant());
        Check(!same.RequiresReview && same.Stored!.Values["resolution_scale"].GetInt32() == 2);
        var changed = store.GetSettingRequests(entry.EntryId, "eden", new string('C', 40));
        Check(changed.RequiresReview && changed.Stored!.BackendRevision == LibraryRevision);
        Check(store.GetSettingRequests(entry.EntryId, "ryujinx", LibraryRevision).Stored is null);
        foreach (var value in new[] { "null", "{}", "[]", "1e9999", "\"C:\\\\private\\\\file\"", "\"$(run)\"" })
        {
            using var doc = JsonDocument.Parse(value);
            Error(ServiceError.InvalidInput, () => store.SetSettingRequests(entry.EntryId, "eden", LibraryRevision, new Dictionary<string, JsonElement> { ["unsafe"] = doc.RootElement }));
        }
        Check(store.GetSettingRequests(entry.EntryId, "eden", LibraryRevision).Stored!.Values.Count == 3);
    }

    private static void LibraryObservation()
    {
        using var f = new Fixture();
        var store = new GameLibraryStore(f.Workspace);
        var entry = Register(store, LibraryFile(f));
        var observation = new LibraryCoreObservation("eden", LibraryRevision, entry.Content.Sha256,
            TitleId: Title, BuildId: Binding.BuildId, DisplayName: "Core title", DisplayVersion: "1.0.0",
            LoaderAccepted: true, Provenance: "authored test adapter observation; not gameplay");
        Error(ServiceError.Conflict, () => store.AddCoreObservation(entry.EntryId, observation with { ContentSha256 = new string('c', 64) }));
        Error(ServiceError.InvalidInput, () => store.AddCoreObservation(entry.EntryId, observation with { Provenance = "" }));
        Error(ServiceError.InvalidInput, () => store.AddCoreObservation(entry.EntryId, observation with { TitleId = new string('0', 16) }));
        Check(store.Get(entry.EntryId).Observations.Count == 0);
        store.AddCoreObservation(entry.EntryId, observation);
        store.AddCoreObservation(entry.EntryId, observation with { DisplayVersion = "1.0.1" });
        Check(store.Get(entry.EntryId).Observations.Single().DisplayVersion == "1.0.1");
        Check(store.Search(Title).Single().EntryId == entry.EntryId);
        Check(store.Get(entry.EntryId).DisplayName != "Core title");
    }

    private static void LibraryLaunchHistory()
    {
        using var f = new Fixture();
        var store = new GameLibraryStore(f.Workspace, new(MaximumLaunchesPerEntry: 2));
        var entry = Register(store, LibraryFile(f));
        Check(store.Recent().Count == 0);
        var first = store.MarkLaunch(entry.EntryId, "eden", LibraryRevision);
        Error(ServiceError.Conflict, () => store.Remove(entry.EntryId));
        Check(new GameLibraryStore(f.Workspace, new(MaximumLaunchesPerEntry: 2)).Get(entry.EntryId).Launches.Single().Outcome == LibraryLaunchOutcome.Started);
        var failed = store.CompleteLaunch(entry.EntryId, first.LaunchId, LibraryLaunchOutcome.Failed, "guest_crash");
        Check(failed.ErrorCode == "guest_crash" && failed.EndedUtc is not null);
        Error(ServiceError.Conflict, () => store.CompleteLaunch(entry.EntryId, first.LaunchId, LibraryLaunchOutcome.Completed));
        var second = store.MarkLaunch(entry.EntryId, "eden", LibraryRevision);
        var third = store.MarkLaunch(entry.EntryId, "eden", LibraryRevision);
        Check(store.Get(entry.EntryId).Launches.All(l => l.LaunchId != first.LaunchId));
        Error(ServiceError.Busy, () => store.MarkLaunch(entry.EntryId, "eden", LibraryRevision));
        store.CompleteLaunch(entry.EntryId, second.LaunchId, LibraryLaunchOutcome.Interrupted, "frontend_restarted");
        store.CompleteLaunch(entry.EntryId, third.LaunchId, LibraryLaunchOutcome.Completed);
        Check(store.Recent().Single().EntryId == entry.EntryId);
        Check(store.Remove(entry.EntryId));
    }

    private static void LibraryAssociations()
    {
        using var f = new Fixture();
        var store = new GameLibraryStore(f.Workspace);
        var game = Register(store, LibraryFile(f, "game.xci"));
        var updatePath = LibraryFile(f, "update.nsp");
        var update = Register(store, updatePath, LibraryContentKind.Update, related: game.EntryId);
        Check(update.RelatedGameEntryId == game.EntryId && update.Observations.Count == 0);
        Error(ServiceError.Conflict, () => store.Remove(game.EntryId));
        Error(ServiceError.InvalidInput, () => store.ResolveForLaunchAsync(update.EntryId).GetAwaiter().GetResult());
        Error(ServiceError.InvalidInput, () => store.SetRelatedGame(game.EntryId, update.EntryId));
        store.SetRelatedGame(update.EntryId, null);
        Check(store.Remove(game.EntryId) && File.Exists(updatePath));
        Check(store.List(new(Kind: LibraryContentKind.Update)).Single().EntryId == update.EntryId);
    }

    private static void LibraryChangedContent()
    {
        using var f = new Fixture();
        var store = new GameLibraryStore(f.Workspace);
        var path = LibraryFile(f, bytes: "original");
        var entry = Register(store, path);
        var modified = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, "replaced");
        File.SetLastWriteTimeUtc(path, modified);
        var indexPath = Path.Combine(f.Root, "nx-services", "library", "index.json");
        var before = File.ReadAllBytes(indexPath);
        Error(ServiceError.Conflict, () => store.ResolveForLaunchAsync(entry.EntryId).GetAwaiter().GetResult());
        Error(ServiceError.Conflict, () => Register(store, path));
        Check(before.SequenceEqual(File.ReadAllBytes(indexPath)) && store.Get(entry.EntryId).Content.Sha256 == entry.Content.Sha256);
    }

    private static void LibraryCancellation()
    {
        using var f = new Fixture();
        var store = new GameLibraryStore(f.Workspace);
        var path = LibraryFile(f);
        using var canceled = new CancellationTokenSource();
        var progress = new InlineProgress<ContentHashProgress>(_ => canceled.Cancel());
        try { store.RegisterAsync(new(path), progress, canceled.Token).GetAwaiter().GetResult(); throw new Exception("Expected cancellation."); }
        catch (OperationCanceledException) { }
        Check(store.List().Count == 0 && File.Exists(path));
        Register(store, path);
        Check(store.List().Count == 1);
    }

    private static void LibraryMissing()
    {
        using var f = new Fixture();
        var store = new GameLibraryStore(f.Workspace);
        var path = LibraryFile(f);
        var entry = Register(store, path);
        File.Delete(path);
        Check(store.List().Single().EntryId == entry.EntryId);
        store.SetFavorite(entry.EntryId, true);
        Error(ServiceError.NotFound, () => store.ResolveForLaunchAsync(entry.EntryId).GetAwaiter().GetResult());
        Check(store.Get(entry.EntryId).Favorite);
    }

    private static void LibraryDiscovery()
    {
        using var f = new Fixture();
        var path = LibraryFile(f, "first.nro");
        var root = Path.GetDirectoryName(path)!;
        var nested = Path.Combine(root, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "second.XCI"), "candidate only");
        File.WriteAllText(Path.Combine(root, "prod.keys"), "not content");
        var store = new GameLibraryStore(f.Workspace);
        Check(store.Discover(root).Candidates.Count == 1);
        Check(store.Discover(root, recursive: true).Candidates.Count == 2 && store.List().Count == 0);
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "cycle"), root);
            File.CreateSymbolicLink(Path.Combine(root, "linked.nro"), path);
            var result = store.Discover(root, recursive: true);
            Check(result.Candidates.Count == 2 && result.Issues.Count == 2);
        }
        var bounded = new GameLibraryStore(f.Workspace, new(MaximumDiscoveryEntries: 1)).Discover(root, recursive: true);
        Check(bounded.Truncated);
    }

    private static void LibraryPaths()
    {
        using var f = new Fixture();
        var store = new GameLibraryStore(f.Workspace);
        var path = LibraryFile(f);
        Error(ServiceError.UnsafePath, () => Register(store, "relative.nro"));
        Error(ServiceError.UnsafePath, () => Register(store, Path.Combine(Path.GetDirectoryName(path)!, "..", "user-content", Path.GetFileName(path))));
        Error(ServiceError.InvalidInput, () => Register(store, LibraryFile(f, "prod.keys")));
        Error(ServiceError.InvalidInput, () => Register(store, LibraryFile(f, "empty.nro", "")));
        if (!OperatingSystem.IsWindows())
        {
            var link = Path.Combine(Path.GetDirectoryName(path)!, "linked.nro");
            File.CreateSymbolicLink(link, path);
            Error(ServiceError.UnsafePath, () => Register(store, link));
            var parent = Path.Combine(f.Root, "linked-parent");
            Directory.CreateSymbolicLink(parent, Path.GetDirectoryName(path)!);
            Error(ServiceError.UnsafePath, () => Register(store, Path.Combine(parent, Path.GetFileName(path))));
        }
        Check(store.List().Count == 0);
    }

    private static void LibraryCorruption()
    {
        using var f = new Fixture();
        var store = new GameLibraryStore(f.Workspace);
        var entry = Register(store, LibraryFile(f), name: "Original");
        var path = Path.Combine(f.Root, "nx-services", "library", "index.json");
        var good = File.ReadAllText(path);
        foreach (var bad in new[] { good.Replace("Original", "Modified"), "{\"index\":null,\"index\":null,\"sha256\":null}", "{broken" })
        {
            File.WriteAllText(path, bad);
            Error(ServiceError.CorruptData, () => store.SetFavorite(entry.EntryId, true));
            Check(File.ReadAllText(path) == bad);
        }
        File.WriteAllText(path, good);
        Check(store.Get(entry.EntryId).DisplayName == "Original");
    }

    private static void LibraryProcessLock()
    {
        using var f = new Fixture();
        var store = new GameLibraryStore(f.Workspace);
        var entry = Register(store, LibraryFile(f));
        using (HoldLock(Path.Combine(f.Root, "nx-services", "locks", "library.lock")))
        {
            Error(ServiceError.Busy, () => store.SetFavorite(entry.EntryId, true));
            Error(ServiceError.Busy, () => store.List());
        }
        store.SetFavorite(entry.EntryId, true);
        Check(store.Get(entry.EntryId).Favorite);
    }

    private static void LibraryArtworkIntegrity()
    {
        using var f = new Fixture();
        var store = new GameLibraryStore(f.Workspace);
        var entry = Register(store, LibraryFile(f));
        var png = AuthoredPng();
        var expected = png.ToArray();
        var metadata = store.SetArtwork(entry.EntryId, png);
        png[^1] ^= 0xff;
        var owned = store.GetArtwork(entry.EntryId)!;
        Check(owned.Bytes.SequenceEqual(expected) && metadata.Width == 1 && metadata.Height == 1);
        owned.Bytes[0] = 0;
        Check(store.GetArtwork(entry.EntryId)!.Bytes.SequenceEqual(expected));
        using (var held = new FileStream(Path.Combine(f.Root, "nx-services", "locks", "library.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            Check(store.ReadArtwork(metadata).Bytes.SequenceEqual(expected));
        Error(ServiceError.InvalidInput, () => store.ReadArtwork(metadata with { Bytes = int.MaxValue }));
        Error(ServiceError.InvalidInput, () => store.SetArtwork(entry.EntryId, Bytes("<svg onload='run'/>")));
        Error(ServiceError.InvalidInput, () => store.SetArtwork(entry.EntryId, png));
        Error(ServiceError.InvalidInput, () => store.SetArtwork(entry.EntryId, AuthoredPng(9000)));
        var saved = Directory.GetFiles(Path.Combine(f.Root, "nx-services", "library", "artwork"), "*.png").Single();
        File.WriteAllBytes(saved, png);
        Error(ServiceError.CorruptData, () => store.GetArtwork(entry.EntryId));
        store.SetArtwork(entry.EntryId, expected);
        Check(store.GetArtwork(entry.EntryId)!.Bytes.SequenceEqual(expected));
        var quarantine = Directory.GetFiles(Path.Combine(Path.GetDirectoryName(saved)!, "quarantine"), "*.bin");
        Check(quarantine.Length == 1 && File.ReadAllBytes(quarantine[0]).SequenceEqual(png));
        File.WriteAllBytes(saved, [1, 2, 3]);
        store.SetArtwork(entry.EntryId, expected);
        Check(store.GetArtwork(entry.EntryId)!.Metadata == metadata && store.GetArtwork(entry.EntryId)!.Bytes.SequenceEqual(expected));
        Check(Directory.GetFiles(Path.Combine(Path.GetDirectoryName(saved)!, "quarantine"), "*.bin").Length == 2);
        File.Delete(saved);
        store.SetArtwork(entry.EntryId, expected);
        Check(store.GetArtwork(entry.EntryId)!.Bytes.SequenceEqual(expected));
        Check(store.ClearArtwork(entry.EntryId).Artwork is null && store.GetArtwork(entry.EntryId) is null);
    }

    private static void NativeSaveMapping()
    {
        var observation = new NativeSaveObservation("eden", LibraryRevision, Title, new string('1', 32),
            "0000000000000001", "account", "eden-native", "1", "nand/user/save/account", true, true, "actual adapter query copied by SDK caller");
        var mapping = NativeSaveMappings.FromNativeObservation(observation, "eden", LibraryRevision);
        Check(mapping.Identity.TitleId == Title && mapping.Verification == SaveMappingVerification.AdapterVerified && mapping.VerificationEvidence.Contains(LibraryRevision));
        Error(ServiceError.Conflict, () => NativeSaveMappings.FromNativeObservation(observation, "eden", new string('C', 40)));
        Error(ServiceError.Conflict, () => NativeSaveMappings.FromNativeObservation(observation, "ryujinx", LibraryRevision));
        Error(ServiceError.UnverifiedSaveMapping, () => NativeSaveMappings.FromNativeObservation(observation with { Existing = false }, "eden", LibraryRevision));
        Error(ServiceError.UnverifiedSaveMapping, () => NativeSaveMappings.FromNativeObservation(observation with { AdapterVerified = false }, "eden", LibraryRevision));
        Error(ServiceError.UnsafePath, () => NativeSaveMappings.FromNativeObservation(observation with { RelativeDirectory = "../outside" }, "eden", LibraryRevision));
        Error(ServiceError.InvalidInput, () => NativeSaveMappings.FromNativeObservation(observation with { Provenance = "" }, "eden", LibraryRevision));
        Error(ServiceError.InvalidInput, () => NativeSaveMappings.FromNativeObservation(observation with { UserId = new string('0', 32) }, "eden", LibraryRevision));
        Check(NativeSaveMappings.FromNativeObservation(observation with { Type = "device", UserId = new string('0', 32) }, "eden", LibraryRevision).Identity.UserId == new string('0', 32));
    }

    private static void NativeModMounts()
    {
        using var f = new Fixture();
        Bundle(f, "enabled", "enabled", ("romfs/marker.txt", Bytes("authored marker")));
        Bundle(f, "disabled", "disabled", ("romfs/other.txt", Bytes("disabled marker")));
        var store = new ModStore(f.Workspace);
        var receipt = store.Install("enabled", Binding, enable: true);
        store.Install("disabled", Binding);
        var plan = store.GetActiveMounts(Binding);
        var mount = plan.Mounts.Single();
        Check(plan.Binding == Binding && plan.RevisionId == store.Current(Binding)!.RevisionId);
        Check(mount.ReceiptId == receipt.ReceiptId && mount.ModId == "enabled");
        Check(File.ReadAllText(Path.Combine(mount.AbsoluteDirectory, "romfs", "marker.txt")) == "authored marker");
        Error(ServiceError.InvalidInput, () => store.GetActiveMounts(Binding with { BuildId = new string('0', 64) }));
        File.WriteAllText(Path.Combine(mount.AbsoluteDirectory, "romfs", "marker.txt"), "changed payload");
        Error(ServiceError.CorruptData, () => store.GetActiveMounts(Binding));
    }

    private static void SaveBackupCatalog()
    {
        using var f = new Fixture();
        var store = new SaveStore(f.Workspace, "backends/eden/library-test");
        var identity = new SaveIdentity(Title, new string('1', 32), "eden", "authored-fixture", "1");
        var mapping = new SaveDirectoryMapping(identity, "saves/one", SaveMappingVerification.AdapterVerified, "synthetic fixture only");
        var live = Path.Combine(store.BackendDataRoot, "saves", "one");
        Directory.CreateDirectory(live); File.WriteAllText(Path.Combine(live, "data.bin"), "first");
        var backup = store.CreateBackup(mapping);
        Check(store.ListBackups().Single().Manifest.BackupId == backup.Manifest.BackupId);
        Check(store.ListBackups(identity).Single().Manifest.Identity == identity);
        Check(store.ListBackups(identity with { BackendId = "ryujinx" }).Count == 0);
        Check(store.ListBackups(identity with { UserId = new string('2', 32) }).Count == 0);
        using (HoldLock(store.RuntimeLockPath)) Error(ServiceError.Busy, () => store.ListBackups(identity));
        File.WriteAllText(Path.Combine(backup.DirectoryPath, "payload", "data.bin"), "tampered");
        Error(ServiceError.CorruptData, () => store.ListBackups(identity));
        Check(File.ReadAllText(Path.Combine(live, "data.bin")) == "first");
    }

    private static void SaveBackupCatalogBounds()
    {
        using var f = new Fixture();
        var store = new SaveStore(f.Workspace, "backends/eden/library-test");
        var directory = Path.Combine(store.BackendDataRoot, ".nx-saves", "backups");
        var unexpected = Path.Combine(directory, "unowned");
        Directory.CreateDirectory(unexpected);
        Error(ServiceError.CorruptData, () => store.ListBackups());
        Directory.Delete(unexpected);
        Directory.CreateDirectory(Path.Combine(directory, Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Path.Combine(directory, Guid.NewGuid().ToString("N")));
        var limited = new SaveStore(new NxWorkspace(f.Root, new(MaximumFiles: 1)), "backends/eden/library-test");
        Error(ServiceError.CorruptData, () => limited.ListBackups());
    }

    private static byte[] AuthoredPng(int width = 1)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), 1);
        header[8] = 8; header[9] = 6;
        PngChunk(output, "IHDR", header);
        using var payload = new MemoryStream();
        using (var zlib = new ZLibStream(payload, CompressionLevel.SmallestSize, leaveOpen: true)) zlib.Write(new byte[] { 0, 20, 80, 240, 255 });
        PngChunk(output, "IDAT", payload.ToArray());
        PngChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void PngChunk(Stream output, string type, byte[] payload)
    {
        var length = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(length, payload.Length); output.Write(length);
        var bytes = Bytes(type).Concat(payload).ToArray(); output.Write(bytes);
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320U);
        }
        BinaryPrimitives.WriteUInt32BigEndian(length, ~crc); output.Write(length);
    }

    private sealed class InlineProgress<T>(Action<T>? changed = null) : IProgress<T>
    {
        public List<T> Values { get; } = [];
        public void Report(T value) { Values.Add(value); changed?.Invoke(value); }
    }
}
