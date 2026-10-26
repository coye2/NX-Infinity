using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NxInfinity.Runtime.Services;

internal static partial class Program
{
    private const string Title = "0100000000000001";
    private const string OtherTitle = "0100000000000002";
    private static readonly GameBuildIdentity Binding = new(Title, new string('A', 64));
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NewLine = "\n",
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static int Main(string[] args)
    {
        if (args is ["--artwork-only"]) { LibraryArtworkIntegrity(); Console.WriteLine("PASS artwork integrity, quarantine repair and missing-file recovery"); return 0; }
        if (args.Length > 0 && args[0] == "--native-save-probe") return NativeSaveProbe(args);
        if (args is ["--hold-lock", var lockPath])
        {
            using var handle = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            Console.WriteLine("ready");
            Console.In.ReadLine();
            return 0;
        }
        var tests = new (string Name, Action Run)[]
        {
            ("profiles require an explicit route", ProfilesNeedSelection),
            ("global/title precedence survives reopening", ProfilePrecedence),
            ("experimental routes require opt-in after qualification changes", ExperimentalGuard),
            ("unavailable backends never silently fall back", UnavailableRoute),
            ("corrupt and duplicate-key profiles are preserved", CorruptProfile),
            ("profile writes honor a real second-process lock", ProfileProcessLock),
            ("mod install emits checked ownership and overlay paths", InstallMod),
            ("mods require exact full title and build identity", ExactModIdentity),
            ("mod and inbox traversal attempts are rejected", ModTraversal),
            ("symlinked mod files and directories are rejected", ModSymlinks),
            ("wrong mod hashes and unlisted files are rejected", ModCorruption),
            ("case-colliding mod paths are rejected", ModCaseCollision),
            ("script extensions and disguised executables are rejected", ModExecutables),
            ("IPS data is parsed and truncation is rejected", IpsValidation),
            ("mod conflict failures preserve selection and rollback history", ModConflictRollback),
            ("file/directory conflicts across mods are rejected", ModHierarchyConflict),
            ("installed content tampering blocks active overlays", InstalledModCorruption),
            ("corrupt rollback payloads do not change current revision", CorruptModRollback),
            ("malformed mod selection records fail as corrupt metadata", NullModSelection),
            ("mod mutations honor a second-process lock", ModProcessLock),
            ("payload limits stop oversized imports", PayloadLimits),
            ("save mappings require explicit adapter verification", SaveMappingGuard),
            ("save backups preserve bytes and empty directories", SaveBackupIntegrity),
            ("save title/user/backend/format mismatches preserve originals", SaveIdentityGuard),
            ("save restore and rollback preserve each original", SaveRestoreRollback),
            ("save operations honor a real worker-process lease", SaveProcessLock),
            ("save paths reject traversal and symlinked sources", SavePathSafety),
            ("corrupt and unlisted snapshot data cannot replace saves", SaveCorruption),
            ("save recovery handles a crash before the first rename", () => SaveRecovery(0)),
            ("save recovery handles a crash between directory renames", () => SaveRecovery(1)),
            ("save recovery handles a crash after replacement", () => SaveRecovery(2)),
            ("invalid pending paths stay blocked without moving data", InvalidRecovery),
            ("corrupt originals keep recovery blocked and backup intact", CorruptRecovery),
            ("enhancement catalogs are immutable and reopen with exact pins", EnhancementCatalog),
            ("research templates and unknown builds never become applicable", EnhancementResearchGuard),
            ("enhancements require exact verified title build backend revision", EnhancementIdentity),
            ("original four research catalog fingerprints and envelope bytes remain unchanged", EnhancementCanonicalCompatibility),
            ("historical Windows enhancement hashes remain readable and tampering is rejected", EnhancementWindowsCompatibility),
            ("core updates preserve immutable research catalogs across upgrades and downgrades", EnhancementCoreUpgrade),
            ("adapter and verified artifact bindings are exact and fail closed", EnhancementArtifactIdentity),
            ("legacy profiles and receipts remain readable without gaining executable authority", EnhancementLegacyReadability),
            ("an active session cannot be reused for another adapter or artifact identity", EnhancementActiveArtifactIdentity),
            ("hardware constraints require positive verified vendor device and driver matches", EnhancementHardware),
            ("enhancement classifications do not silently install unsupported changes", EnhancementClassification),
            ("enhancement dependencies and patch conflicts are enforced", EnhancementRequirements),
            ("active enhancement conflicts are symmetric and setting ownership cannot overlap", EnhancementActiveConflicts),
            ("experimental enhancement consent is explicit and receipt-bound", EnhancementOptIn),
            ("enhancement receipts preserve originals and recovery transitions", EnhancementReceipts),
            ("enhancement metadata corruption and links fail without rewriting", EnhancementCorruption),
            ("enhancement writes honor a real second-process lock", EnhancementProcessLock)
        };
        var passed = 0;
        var skipped = 0;
        foreach (var test in tests.Concat(LibraryTests))
        {
            try { test.Run(); Console.WriteLine("PASS " + test.Name); passed++; }
            catch (TestUnavailableException ex) { Console.WriteLine("SKIP " + test.Name + ": " + ex.Message); skipped++; }
            catch (Exception ex) { Console.Error.WriteLine("FAIL " + test.Name + "\n" + ex); return 1; }
        }
        if (OperatingSystem.IsLinux())
        {
            try { NativeFlockInterop(); Console.WriteLine("PASS Linux native flock interoperates with the .NET save lease"); passed++; }
            catch (Exception ex) { Console.Error.WriteLine("FAIL native flock interop\n" + ex); return 1; }
        }
        else Console.WriteLine("SKIP Linux native flock interoperability (not Linux)");
        Console.WriteLine($"{passed} service tests passed; {skipped} unavailable. Synthetic directory fixtures only; no gameplay or emulator save-format claim.");
        return 0;
    }

    private sealed class TestUnavailableException(string message) : Exception(message);

    private static void CreateTestLink(string path, string target, bool directory = false)
    {
        try
        {
            if (directory) Directory.CreateSymbolicLink(path, target);
            else File.CreateSymbolicLink(path, target);
        }
        catch (Exception ex) when (OperatingSystem.IsWindows() &&
            ex is IOException or UnauthorizedAccessException && (ex.HResult & 0xffff) == 1314)
        {
            throw new TestUnavailableException("Windows symbolic-link privilege is unavailable; run this fixture in a developer-mode test environment.");
        }
    }

    private static BackendDefinition[] Catalog() => [new("qualified", "1.0", BackendQualification.Qualified), new("experimental", "2.0", BackendQualification.Experimental)];
    private static ProfileRouter Router(Fixture f) => new(f.Workspace, Catalog());
    private static void ProfilesNeedSelection()
    {
        using var f = new Fixture();
        Error(ServiceError.NotFound, () => Router(f).Resolve(Title));
        Error(ServiceError.InvalidInput, () => Router(f).SetTitleOverride("../escape", new("qualified")));
    }
    private static void ProfilePrecedence()
    {
        using var f = new Fixture();
        var router = Router(f);
        router.SetGlobal(new("qualified"));
        router.SetTitleOverride(Title, new("experimental", BackendSelectionMode.ExperimentalOptIn));
        router.SetGlobal(new("qualified"));
        router = Router(f);
        Check(router.Resolve(Title).Backend.Id == "experimental" && router.Resolve(Title).Source == "title");
        Check(router.Resolve(OtherTitle).Backend.Id == "qualified" && router.Resolve(OtherTitle).Source == "global");
        Check(router.ClearTitleOverride(Title));
        Check(router.Resolve(Title).Backend.Id == "qualified");
        Check(!router.ClearTitleOverride(Title));
    }
    private static void ExperimentalGuard()
    {
        using var f = new Fixture();
        var router = Router(f);
        Error(ServiceError.ExperimentalOptInRequired, () => router.SetGlobal(new("experimental")));
        router.SetGlobal(new("qualified"));
        var changed = new ProfileRouter(f.Workspace, [new("qualified", "1.1", BackendQualification.Experimental), new("new-good", "1.0", BackendQualification.Qualified)]);
        Error(ServiceError.ExperimentalOptInRequired, () => changed.Resolve(Title));
        changed.SetGlobal(new("qualified", BackendSelectionMode.ExperimentalOptIn));
        Check(changed.Resolve(Title).Backend.Id == "qualified");
    }
    private static void UnavailableRoute()
    {
        using var f = new Fixture();
        Router(f).SetGlobal(new("qualified"));
        var changed = new ProfileRouter(f.Workspace, [new("another", "1", BackendQualification.Qualified)]);
        var before = File.ReadAllText(Path.Combine(f.Root, "nx-services", "profiles.json"));
        Error(ServiceError.Unavailable, () => changed.Resolve(Title));
        Check(before == File.ReadAllText(Path.Combine(f.Root, "nx-services", "profiles.json")));
        Error(ServiceError.Unavailable, () => changed.SetGlobal(new("unknown")));
    }
    private static void CorruptProfile()
    {
        using var f = new Fixture();
        var path = Path.Combine(f.Root, "nx-services", "profiles.json");
        foreach (var invalid in new[] { "{broken", "{\"schemaVersion\":1,\"schemaVersion\":2,\"global\":null,\"titles\":{}}" })
        {
            File.WriteAllText(path, invalid);
            Error(ServiceError.CorruptData, () => Router(f).SetGlobal(new("qualified")));
            Check(File.ReadAllText(path) == invalid);
        }
    }
    private static void ProfileProcessLock()
    {
        using var f = new Fixture();
        var path = Path.Combine(f.Root, "nx-services", "locks", "profiles.lock");
        using (var child = HoldLock(path)) Error(ServiceError.Busy, () => Router(f).SetGlobal(new("qualified")));
        Router(f).SetGlobal(new("qualified"));
    }

    private static void InstallMod()
    {
        using var f = new Fixture();
        Bundle(f, "pack", "textures", ("romfs/models/wood.bin", Bytes("wood")));
        var store = new ModStore(f.Workspace);
        var receipt = store.Install("pack", Binding, enable: true);
        var overlay = store.GetActiveOverlay(Binding);
        Check(overlay.Files.Count == 1 && overlay.Files[0].ModId == "textures" && File.ReadAllText(overlay.Files[0].SourcePath) == "wood");
        Check(store.List(Binding).Single().Receipt.ReceiptId == receipt.ReceiptId);
        Check(new ModStore(new NxWorkspace(f.Root)).GetActiveOverlay(Binding).RevisionId == overlay.RevisionId);
    }
    private static void ExactModIdentity()
    {
        using var f = new Fixture();
        Bundle(f, "pack", "textures", ("romfs/asset.bin", Bytes("data")));
        var store = new ModStore(f.Workspace);
        Error(ServiceError.Conflict, () => store.Install("pack", Binding with { TitleId = OtherTitle }));
        Error(ServiceError.Conflict, () => store.Install("pack", Binding with { BuildId = new string('B', 64) }));
        Error(ServiceError.InvalidInput, () => store.Install("pack", Binding with { BuildId = new string('A', 16) }));
        Check(store.List(Binding).Count == 0);
    }
    private static void ModTraversal()
    {
        using var f = new Fixture();
        Bundle(f, "pack", "textures", ("romfs/asset.bin", Bytes("data")));
        var path = Path.Combine(f.Workspace.ImportsPath, "pack", "manifest.json");
        var manifest = Read<ModManifest>(path);
        Write(path, manifest with { Files = [manifest.Files[0] with { Path = "romfs/../../escape.bin" }] });
        Error(ServiceError.UnsafePath, () => new ModStore(f.Workspace).Install("pack", Binding));
        Error(ServiceError.UnsafePath, () => new ModStore(f.Workspace).Install("../pack", Binding));
        foreach (var invalid in new[] { "romfs/CON.bin", "romfs/a:stream", "romfs/trailing.", "romfs\\escape.bin", "/absolute.bin" })
        {
            Write(path, manifest with { Files = [manifest.Files[0] with { Path = invalid }] });
            Error(ServiceError.UnsafePath, () => new ModStore(f.Workspace).Install("pack", Binding));
        }
    }
    private static void ModSymlinks()
    {
        using var f = new Fixture();
        Bundle(f, "pack", "textures", ("romfs/asset.bin", Bytes("data")));
        var payload = Path.Combine(f.Workspace.ImportsPath, "pack", "payload");
        var file = Path.Combine(payload, "romfs", "asset.bin");
        var outside = Path.Combine(f.Root, "untouched.bin");
        File.WriteAllText(outside, "data");
        File.Delete(file);
        CreateTestLink(file, outside);
        Error(ServiceError.UnsafePath, () => new ModStore(f.Workspace).Install("pack", Binding));
        File.Delete(file);
        Directory.Delete(Path.Combine(payload, "romfs"));
        var externalDirectory = Path.Combine(f.Root, "external-assets");
        Directory.CreateDirectory(externalDirectory);
        File.WriteAllText(Path.Combine(externalDirectory, "asset.bin"), "data");
        CreateTestLink(Path.Combine(payload, "romfs"), externalDirectory, directory: true);
        Error(ServiceError.UnsafePath, () => new ModStore(f.Workspace).Install("pack", Binding));
        Check(File.ReadAllText(outside) == "data");
    }
    private static void ModCorruption()
    {
        using var f = new Fixture();
        Bundle(f, "pack", "textures", ("romfs/asset.bin", Bytes("data")));
        var file = Path.Combine(f.Workspace.ImportsPath, "pack", "payload", "romfs", "asset.bin");
        File.WriteAllText(file, "evil");
        Error(ServiceError.CorruptData, () => new ModStore(f.Workspace).Install("pack", Binding));
        File.WriteAllText(file, "data");
        File.WriteAllText(Path.Combine(f.Workspace.ImportsPath, "pack", "extra.bin"), "extra");
        Error(ServiceError.CorruptData, () => new ModStore(f.Workspace).Install("pack", Binding));
    }
    private static void ModCaseCollision()
    {
        using var f = new Fixture();
        Bundle(f, "pack", "textures", ("romfs/asset.bin", Bytes("data")));
        var path = Path.Combine(f.Workspace.ImportsPath, "pack", "manifest.json");
        var manifest = Read<ModManifest>(path);
        Write(path, manifest with { Files = [manifest.Files[0], manifest.Files[0] with { Path = "romfs/ASSET.bin" }] });
        Error(ServiceError.CorruptData, () => new ModStore(f.Workspace).Install("pack", Binding));
    }
    private static void ModExecutables()
    {
        using var f = new Fixture();
        Bundle(f, "script", "scripts", ("romfs/start.ps1", Bytes("Write-Host danger")));
        Error(ServiceError.InvalidInput, () => new ModStore(f.Workspace).Install("script", Binding));
        Bundle(f, "elf", "elf", ("romfs/asset.bin", new byte[] { 0x7f, 0x45, 0x4c, 0x46, 0, 1 }));
        Error(ServiceError.InvalidInput, () => new ModStore(f.Workspace).Install("elf", Binding));
        Bundle(f, "shebang", "shebang", ("romfs/asset.bin", Bytes("#!/bin/sh\necho unsafe")));
        Error(ServiceError.InvalidInput, () => new ModStore(f.Workspace).Install("shebang", Binding));
    }
    private static void IpsValidation()
    {
        using var f = new Fixture();
        Bundle(f, "ips", "patch", ("exefs/test.ips", new byte[] { 80, 65, 84, 67, 72, 0, 0, 1, 0, 2, 10, 11, 69, 79, 70 }));
        var store = new ModStore(f.Workspace);
        Check(store.Install("ips", Binding).Files.Count == 1);
        Bundle(f, "bad-ips", "bad-patch", ("exefs/test.ips", Bytes("PATCH\0\0")));
        Error(ServiceError.CorruptData, () => store.Install("bad-ips", Binding));
    }
    private static void ModConflictRollback()
    {
        using var f = new Fixture();
        Bundle(f, "a", "a", ("romfs/asset.bin", Bytes("a")));
        Bundle(f, "b", "b", ("romfs/asset.bin", Bytes("b")));
        var store = new ModStore(f.Workspace);
        var first = store.Install("a", Binding, enable: true);
        store.Install("b", Binding);
        var before = store.Current(Binding)!;
        Error(ServiceError.Conflict, () => store.SetEnabled(Binding, "b", true));
        Check(store.Current(Binding)!.RevisionId == before.RevisionId);
        Check(store.GetConflicts(Binding, includeDisabled: true).Single().ModIds.Count == 2);
        store.SetEnabled(Binding, "a", false);
        store.SetEnabled(Binding, "b", true);
        Error(ServiceError.InvalidInput, () => store.SetOrder(Binding, ["b"]));
        store.SetOrder(Binding, ["b", "a"]);
        Check(store.List(Binding)[0].Selection.ModId == "b");
        store.Remove(Binding, "a");
        Check(Directory.Exists(Path.Combine(f.Root, "nx-services", "mods", "packages", first.ReceiptId)));
        store.RollbackTo(Binding, before.RevisionId);
        Check(store.GetActiveOverlay(Binding).Files.Single().ModId == "a");
    }
    private static void ModHierarchyConflict()
    {
        using var f = new Fixture();
        Bundle(f, "a", "a", ("romfs/branch", Bytes("a")));
        Bundle(f, "b", "b", ("romfs/branch/leaf.bin", Bytes("b")));
        var store = new ModStore(f.Workspace);
        store.Install("a", Binding, true);
        store.Install("b", Binding);
        Error(ServiceError.Conflict, () => store.SetEnabled(Binding, "b", true));
    }
    private static void InstalledModCorruption()
    {
        using var f = new Fixture();
        Bundle(f, "a", "a", ("romfs/asset.bin", Bytes("a")));
        var store = new ModStore(f.Workspace);
        store.Install("a", Binding, true);
        var file = store.GetActiveOverlay(Binding).Files.Single().SourcePath;
        File.WriteAllText(file, "b");
        Error(ServiceError.CorruptData, () => store.GetActiveOverlay(Binding));
        Error(ServiceError.CorruptData, () => store.List(Binding));
    }
    private static void CorruptModRollback()
    {
        using var f = new Fixture();
        Bundle(f, "a", "a", ("romfs/asset.bin", Bytes("a")));
        var store = new ModStore(f.Workspace);
        var receipt = store.Install("a", Binding, true);
        var old = store.Current(Binding)!;
        var current = store.Remove(Binding, "a");
        File.WriteAllText(Path.Combine(f.Root, "nx-services", "mods", "packages", receipt.ReceiptId, "payload", "romfs", "asset.bin"), "b");
        Error(ServiceError.CorruptData, () => store.RollbackTo(Binding, old.RevisionId));
        Check(store.Current(Binding)!.RevisionId == current.RevisionId);
    }
    private static void ModProcessLock()
    {
        using var f = new Fixture();
        var store = new ModStore(f.Workspace);
        Bundle(f, "a", "a", ("romfs/asset.bin", Bytes("a")));
        var path = Path.Combine(f.Root, "nx-services", "locks", "mods-" + Title.ToLowerInvariant() + "-" + Binding.BuildId.ToLowerInvariant() + ".lock");
        using (var child = HoldLock(path)) Error(ServiceError.Busy, () => store.Install("a", Binding));
        Check(store.Install("a", Binding).ModId == "a");
    }
    private static void NullModSelection()
    {
        using var f = new Fixture();
        Bundle(f, "a", "a", ("romfs/asset.bin", Bytes("a")));
        var store = new ModStore(f.Workspace);
        store.Install("a", Binding);
        var current = store.Current(Binding)!;
        var path = Path.Combine(f.Root, "nx-services", "mods", "builds", Title, Binding.BuildId, "revisions", current.RevisionId + ".json");
        Write(path, current with { Mods = [null!] });
        Error(ServiceError.CorruptData, () => store.List(Binding));
    }
    private static void PayloadLimits()
    {
        using var f = new Fixture(new StorageLimits(MaximumFileBytes: 2, MaximumTotalBytes: 3));
        Bundle(f, "a", "a", ("romfs/asset.bin", Bytes("four")));
        Error(ServiceError.CorruptData, () => new ModStore(f.Workspace).Install("a", Binding));
        var save = SaveFixture(f, "four");
        Error(ServiceError.InvalidInput, () => save.Store.CreateBackup(save.Mapping));
        Check(File.ReadAllText(save.LiveFile) == "four");
    }

    private static void SaveMappingGuard()
    {
        using var f = new Fixture();
        var s = SaveFixture(f, "old");
        Error(ServiceError.UnverifiedSaveMapping, () => s.Store.CreateBackup(s.Mapping with { Verification = SaveMappingVerification.Unverified }));
        Error(ServiceError.UnverifiedSaveMapping, () => s.Store.CreateBackup(s.Mapping with { VerificationEvidence = "" }));
    }
    private static void SaveBackupIntegrity()
    {
        using var f = new Fixture();
        var s = SaveFixture(f, "original");
        Directory.CreateDirectory(Path.Combine(s.LiveDirectory, "empty", "nested"));
        var backup = s.Store.CreateBackup(s.Mapping);
        Check(File.ReadAllText(Path.Combine(backup.DirectoryPath, "payload", "save.bin")) == "original");
        Check(backup.Manifest.Directories.Contains("empty/nested"));
        Check(s.Store.InspectBackup(backup.Manifest.BackupId).Manifest.Files.Single().Sha256 == Hash(Bytes("original")));
    }
    private static void SaveIdentityGuard()
    {
        using var f = new Fixture();
        var s = SaveFixture(f, "original");
        var backup = s.Store.CreateBackup(s.Mapping);
        var inbox = Path.Combine(f.Workspace.ImportsPath, "snapshot");
        CopyDirectory(backup.DirectoryPath, inbox);
        var path = Path.Combine(inbox, "manifest.json");
        foreach (var identity in new[] {
            s.Mapping.Identity with { TitleId = OtherTitle },
            s.Mapping.Identity with { UserId = new string('B', 32) },
            s.Mapping.Identity with { BackendId = "different" },
            s.Mapping.Identity with { FormatId = "different-format" },
            s.Mapping.Identity with { FormatVersion = "2" } })
        {
            Write(path, backup.Manifest with { Identity = identity });
            Error(ServiceError.Conflict, () => s.Store.ImportFromInbox(s.Mapping, "snapshot"));
            Check(File.ReadAllText(s.LiveFile) == "original");
        }
    }
    private static void SaveRestoreRollback()
    {
        using var f = new Fixture();
        var s = SaveFixture(f, "version-1");
        Directory.CreateDirectory(Path.Combine(s.LiveDirectory, "empty"));
        var backup = s.Store.CreateBackup(s.Mapping);
        File.WriteAllText(s.LiveFile, "version-2");
        var receipt = s.Store.RestoreBackup(s.Mapping, backup.Manifest.BackupId);
        Check(File.ReadAllText(s.LiveFile) == "version-1");
        Check(File.ReadAllText(Path.Combine(receipt.PreservedOriginalDirectory, "save.bin")) == "version-2");
        Check(Directory.Exists(Path.Combine(s.LiveDirectory, "empty")));
        s.Store.RollbackLastImport(s.Mapping);
        Check(File.ReadAllText(s.LiveFile) == "version-2");
        Check(s.Store.InspectBackup(receipt.PreservedBackupId).Manifest.Identity == s.Mapping.Identity);
        Check(!File.Exists(Path.Combine(s.Store.BackendDataRoot, SaveStore.PendingTransactionFileName)));
    }
    private static void SaveProcessLock()
    {
        using var f = new Fixture();
        var s = SaveFixture(f, "version-1");
        var backup = s.Store.CreateBackup(s.Mapping);
        using (var child = HoldLock(s.Store.RuntimeLockPath))
        {
            Error(ServiceError.Busy, () => s.Store.CreateBackup(s.Mapping));
            Error(ServiceError.Busy, () => s.Store.RestoreBackup(s.Mapping, backup.Manifest.BackupId));
            Error(ServiceError.Busy, () => s.Store.RecoverPendingImport());
        }
        Check(s.Store.CreateBackup(s.Mapping).Manifest.Files.Count == 1);
    }
    private static void SavePathSafety()
    {
        using var f = new Fixture();
        var s = SaveFixture(f, "original");
        Error(ServiceError.UnsafePath, () => new SaveStore(f.Workspace, "imports/unsafe"));
        Error(ServiceError.UnsafePath, () => new SaveStore(f.Workspace, "nx-services/unsafe"));
        Error(ServiceError.UnsafePath, () => s.Store.CreateBackup(s.Mapping with { RelativeDirectory = "../outside" }));
        Error(ServiceError.UnsafePath, () => s.Store.CreateBackup(s.Mapping with { RelativeDirectory = ".nx-saves/backups" }));
        var outside = Path.Combine(f.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "save.bin"), "untouched");
        CreateTestLink(Path.Combine(s.Store.BackendDataRoot, "symlink-save"), outside, directory: true);
        Error(ServiceError.UnsafePath, () => s.Store.CreateBackup(s.Mapping with { RelativeDirectory = "symlink-save" }));
        CreateTestLink(Path.Combine(s.LiveDirectory, "linked.bin"), Path.Combine(outside, "save.bin"));
        Error(ServiceError.UnsafePath, () => s.Store.CreateBackup(s.Mapping));
        Check(File.ReadAllText(Path.Combine(outside, "save.bin")) == "untouched");
    }
    private static void SaveCorruption()
    {
        using var f = new Fixture();
        var s = SaveFixture(f, "original");
        var backup = s.Store.CreateBackup(s.Mapping);
        var inbox = Path.Combine(f.Workspace.ImportsPath, "snapshot");
        CopyDirectory(backup.DirectoryPath, inbox);
        File.WriteAllText(Path.Combine(inbox, "payload", "save.bin"), "corrupt!");
        Error(ServiceError.CorruptData, () => s.Store.ImportFromInbox(s.Mapping, "snapshot"));
        File.WriteAllText(Path.Combine(inbox, "payload", "save.bin"), "original");
        File.WriteAllText(Path.Combine(inbox, "payload", "extra.bin"), "extra");
        Error(ServiceError.CorruptData, () => s.Store.ImportFromInbox(s.Mapping, "snapshot"));
        Check(File.ReadAllText(s.LiveFile) == "original");
    }
    private static void SaveRecovery(int phase)
    {
        using var f = new Fixture();
        var s = SaveFixture(f, "original");
        var backup = s.Store.CreateBackup(s.Mapping);
        var transaction = PreparePending(s, backup, phase);
        Error(ServiceError.RecoveryRequired, () => s.Store.CreateBackup(s.Mapping));
        var result = s.Store.RecoverPendingImport();
        Check(result.Recovered && result.TransactionId == transaction.Id);
        Check(File.ReadAllText(s.LiveFile) == "original");
        Check(File.ReadAllText(Path.Combine(transaction.Staged, "save.bin")) == "imported");
        Check(!File.Exists(Path.Combine(s.Store.BackendDataRoot, SaveStore.PendingTransactionFileName)));
        Check(!s.Store.RecoverPendingImport().Recovered);
    }
    private static void InvalidRecovery()
    {
        using var f = new Fixture();
        var s = SaveFixture(f, "original");
        var backup = s.Store.CreateBackup(s.Mapping);
        var transaction = PreparePending(s, backup, 0);
        var marker = Path.Combine(s.Store.BackendDataRoot, SaveStore.PendingTransactionFileName);
        var json = File.ReadAllText(marker).Replace("\"originalRelativePath\": \"" + transaction.OriginalRelative + "\"", "\"originalRelativePath\": \"../outside\"", StringComparison.Ordinal);
        File.WriteAllText(marker, json);
        Error(ServiceError.RecoveryRequired, () => s.Store.RecoverPendingImport());
        Check(File.Exists(marker) && File.ReadAllText(s.LiveFile) == "original");
    }
    private static void CorruptRecovery()
    {
        using var f = new Fixture();
        var s = SaveFixture(f, "original");
        var backup = s.Store.CreateBackup(s.Mapping);
        var transaction = PreparePending(s, backup, 1);
        File.WriteAllText(Path.Combine(transaction.Original, "save.bin"), "tampered");
        Error(ServiceError.CorruptData, () => s.Store.RecoverPendingImport());
        Check(File.Exists(Path.Combine(s.Store.BackendDataRoot, SaveStore.PendingTransactionFileName)));
        Check(File.ReadAllText(Path.Combine(backup.DirectoryPath, "payload", "save.bin")) == "original");
    }
    private static void NativeFlockInterop()
    {
        using var f = new Fixture();
        var s = SaveFixture(f, "original");
        var info = new ProcessStartInfo("python3") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("import fcntl,sys; f=open(sys.argv[1],'a+'); fcntl.flock(f,fcntl.LOCK_EX|fcntl.LOCK_NB); print('ready',flush=True); sys.stdin.readline()");
        info.ArgumentList.Add(s.Store.RuntimeLockPath);
        using (var child = StartHolder(info)) Error(ServiceError.Busy, () => s.Store.CreateBackup(s.Mapping));
        using var dotnetLease = new FileStream(s.Store.RuntimeLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var contender = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        contender.ArgumentList.Add("-c");
        contender.ArgumentList.Add("import fcntl,sys; f=open(sys.argv[1],'a+'); fcntl.flock(f,fcntl.LOCK_EX|fcntl.LOCK_NB)");
        contender.ArgumentList.Add(s.Store.RuntimeLockPath);
        using var process = Process.Start(contender)!;
        Check(process.WaitForExit(10000) && process.ExitCode != 0);
        Check(process.StandardError.ReadToEnd().Contains("BlockingIOError", StringComparison.Ordinal));
    }

    private sealed record SaveFixtureData(SaveStore Store, SaveDirectoryMapping Mapping, string LiveDirectory, string LiveFile);
    private static SaveFixtureData SaveFixture(Fixture f, string content)
    {
        var store = new SaveStore(f.Workspace, "backends/synthetic/profile");
        var identity = new SaveIdentity(Title, new string('A', 32), "synthetic", "opaque-test-directory", "1");
        var mapping = new SaveDirectoryMapping(identity, "saves/title-user", SaveMappingVerification.AdapterVerified,
            "Synthetic owned test fixture. No emulator save-format or gameplay verification.");
        var live = Path.Combine(store.BackendDataRoot, "saves", "title-user");
        Directory.CreateDirectory(live);
        var file = Path.Combine(live, "save.bin");
        File.WriteAllText(file, content);
        return new SaveFixtureData(store, mapping, live, file);
    }
    private sealed record TestTransaction(string Id, string Staged, string Original, string OriginalRelative);
    private static TestTransaction PreparePending(SaveFixtureData s, SaveBackup backup, int phase)
    {
        var id = Guid.NewGuid().ToString("N");
        var stagedRelative = "saves/.title-user.nx-import-" + id;
        var originalRelative = "saves/.title-user.nx-original-" + id;
        var staged = Path.Combine(s.Store.BackendDataRoot, stagedRelative.Replace('/', Path.DirectorySeparatorChar));
        var original = Path.Combine(s.Store.BackendDataRoot, originalRelative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(staged);
        File.WriteAllText(Path.Combine(staged, "save.bin"), "imported");
        Write(Path.Combine(s.Store.BackendDataRoot, SaveStore.PendingTransactionFileName), new
        {
            SchemaVersion = 1, TransactionId = id, s.Mapping, OriginalRelativePath = originalRelative,
            StagingRelativePath = stagedRelative, PreservedBackupId = backup.Manifest.BackupId,
            ImportedBackupId = Guid.NewGuid().ToString("N"), PreviousState = (object?)null
        });
        if (phase >= 1) Directory.Move(s.LiveDirectory, original);
        if (phase >= 2) Directory.Move(staged, s.LiveDirectory);
        return new TestTransaction(id, staged, original, originalRelative);
    }
    private static void Bundle(Fixture f, string bundleName, string modId, params (string Path, byte[] Bytes)[] files)
    {
        var root = Path.Combine(f.Workspace.ImportsPath, bundleName);
        Directory.CreateDirectory(root);
        var entries = new List<ContentFile>();
        foreach (var file in files)
        {
            var destination = Path.Combine(root, "payload", file.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllBytes(destination, file.Bytes);
            entries.Add(new ContentFile(file.Path, Hash(file.Bytes), file.Bytes.Length));
        }
        Write(Path.Combine(root, "manifest.json"), new ModManifest(1, modId, "1.0.0", Title, Binding.BuildId, ModStore.BundleFormat, entries));
    }
    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json)!;
    private static void Write<T>(string path, T value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed."); }
    private static void Error(ServiceError code, Action action)
    {
        try { action(); }
        catch (ServiceException ex) when (ex.Code == code) { return; }
        throw new InvalidOperationException("Expected ServiceException " + code);
    }
    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
    }
    private static IDisposable HoldLock(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var info = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet") info.ArgumentList.Add(typeof(Program).Assembly.Location);
        info.ArgumentList.Add("--hold-lock");
        info.ArgumentList.Add(path);
        return StartHolder(info);
    }
    private static IDisposable StartHolder(ProcessStartInfo info)
    {
        var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start the independent lock holder.");
        var ready = process.StandardOutput.ReadLineAsync();
        if (!ready.Wait(10000) || ready.Result != "ready")
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            var stderr = process.StandardError.ReadToEnd();
            process.Dispose();
            throw new InvalidOperationException("Lock holder did not become ready: " + stderr);
        }
        return new ProcessLease(process);
    }
    private sealed class ProcessLease(Process process) : IDisposable
    {
        public void Dispose()
        {
            if (!process.HasExited)
            {
                process.StandardInput.WriteLine("stop");
                process.StandardInput.Flush();
                if (!process.WaitForExit(10000)) process.Kill(entireProcessTree: true);
            }
            process.Dispose();
        }
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "nx-api-services-test-" + Guid.NewGuid().ToString("N"));
        public NxWorkspace Workspace { get; }
        public Fixture(StorageLimits? limits = null) => Workspace = new NxWorkspace(Root, limits);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }
}
