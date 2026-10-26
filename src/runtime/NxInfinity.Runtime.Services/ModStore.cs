using System.Buffers.Binary;
using System.Text;

namespace NxInfinity.Runtime.Services;

public sealed record GameBuildIdentity(string TitleId, string BuildId)
{
    public GameBuildIdentity Canonical() => new(Ids.Hex(TitleId, 16, "Title ID"), Ids.Hex(BuildId, 64, "Full build ID"));
}

public sealed record ModManifest(int SchemaVersion, string ModId, string Version, string TitleId, string BuildId,
    string Format, IReadOnlyList<ContentFile> Files);
public sealed record ModReceipt(int SchemaVersion, string ReceiptId, string ModId, string Version, GameBuildIdentity Binding,
    string Format, DateTimeOffset InstalledUtc, IReadOnlyList<ContentFile> Files);
public sealed record ModSelection(string ModId, string ReceiptId, bool Enabled, int Order);
public sealed record ModRevision(int SchemaVersion, string RevisionId, string? ParentRevisionId, GameBuildIdentity Binding,
    IReadOnlyList<ModSelection> Mods, DateTimeOffset CreatedUtc);
public sealed record InstalledMod(ModSelection Selection, ModReceipt Receipt);
public sealed record ModConflict(string Path, IReadOnlyList<string> ModIds);
public sealed record OverlayFile(string TargetPath, string SourcePath, string Sha256, long Size, string ModId);
public sealed record ModOverlayPlan(GameBuildIdentity Binding, string? RevisionId, IReadOnlyList<OverlayFile> Files);
public sealed record ModNativeMount(string ModId, string ReceiptId, string AbsoluteDirectory, int Order,
    IReadOnlyList<ContentFile> Files);
public sealed record ModNativeMountPlan(GameBuildIdentity Binding, string? RevisionId, IReadOnlyList<ModNativeMount> Mounts);
internal sealed record ModPointer(int SchemaVersion, string RevisionId);

/// <summary>Build-bound mod storage. Produces a checked overlay plan; an emulator adapter must apply it.</summary>
public sealed class ModStore
{
    public const string BundleFormat = "nx-overlay-v1";
    private readonly NxWorkspace _workspace;
    private readonly string _root;
    private static readonly HashSet<string> DisallowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".exe", ".dll", ".com", ".scr", ".bat", ".cmd", ".ps1", ".psm1", ".psd1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".sh", ".bash", ".zsh", ".py", ".pyc", ".pl", ".rb", ".msi", ".msp", ".so", ".dylib", ".lnk", ".url", ".desktop", ".reg", ".nro", ".nso", ".kip" };

    public ModStore(NxWorkspace workspace)
    {
        _workspace = workspace;
        _root = Paths.EnsureDirectory(workspace.DataPath, "mods");
        Paths.EnsureDirectory(_root, "packages");
        Paths.EnsureDirectory(_root, "staging");
        Paths.EnsureDirectory(_root, "builds");
    }

    public ModReceipt Install(string incomingBundleRelativePath, GameBuildIdentity binding, bool enable = false)
    {
        binding = binding.Canonical();
        var bundle = _workspace.ResolveImportDirectory(incomingBundleRelativePath);
        var manifest = StoreIo.ReadJson<ModManifest>(Paths.Under(bundle, "manifest.json"));
        ValidateManifest(manifest, binding);
        var payload = Paths.Under(bundle, "payload");
        var expectedBundleFiles = manifest.Files.Select(f => "payload/" + f.Path).Append("manifest.json").ToHashSet(StringComparer.Ordinal);
        if (!Paths.EnumerateFiles(bundle, _workspace.Limits with { MaximumFiles = _workspace.Limits.MaximumFiles + 1 }).ToHashSet(StringComparer.Ordinal).SetEquals(expectedBundleFiles))
            throw new ServiceException(ServiceError.CorruptData, "The bundle contains missing or unlisted files.");
        StoreIo.VerifyTree(payload, manifest.Files, _workspace.Limits);
        using var lease = Acquire(binding);
        var current = ReadUnlocked(binding);
        if (current?.Mods.Any(m => m.ModId == manifest.ModId) == true)
            throw new ServiceException(ServiceError.Conflict, "This mod ID is already installed for the exact build; remove it before installing another version.");
        var receiptId = Guid.NewGuid().ToString("N");
        var staging = Paths.EnsureDirectory(_root, "staging/" + receiptId);
        var stagingPayload = Paths.EnsureDirectory(staging, "payload");
        long total = 0;
        foreach (var file in manifest.Files)
        {
            var copied = StoreIo.CopyHashed(Paths.Under(payload, file.Path), Paths.Under(stagingPayload, file.Path), file.Path,
                _workspace.Limits, _workspace.Limits.MaximumTotalBytes - total);
            if (copied.Size != file.Size || !copied.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new ServiceException(ServiceError.CorruptData, "The mod changed during import or its content hash is wrong.");
            total += copied.Size;
            ValidatePayload(Paths.Under(stagingPayload, file.Path), file.Path);
        }
        var receipt = new ModReceipt(1, receiptId, manifest.ModId, manifest.Version, binding, manifest.Format, DateTimeOffset.UtcNow,
            manifest.Files.Select(f => f with { Sha256 = f.Sha256.ToLowerInvariant() }).ToArray());
        StoreIo.AtomicJson(Paths.Under(staging, "receipt.json"), receipt);
        var installed = Paths.Under(_root, "packages/" + receiptId);
        Directory.Move(staging, installed);
        var selections = current?.Mods.ToList() ?? [];
        selections.Add(new ModSelection(receipt.ModId, receiptId, enable, selections.Count));
        ValidateConflictFree(selections);
        Commit(binding, selections, current?.RevisionId);
        return receipt;
    }

    public ModRevision? Current(GameBuildIdentity binding)
    {
        binding = binding.Canonical();
        using var lease = Acquire(binding);
        return ReadUnlocked(binding);
    }

    public IReadOnlyList<InstalledMod> List(GameBuildIdentity binding)
    {
        binding = binding.Canonical();
        using var lease = Acquire(binding);
        var current = ReadUnlocked(binding);
        return current?.Mods.OrderBy(m => m.Order).Select(m => new InstalledMod(m, ReadReceipt(m, binding, verifyPayload: true))).ToArray() ?? [];
    }

    public ModRevision SetEnabled(GameBuildIdentity binding, string modId, bool enabled)
    {
        binding = binding.Canonical();
        Ids.Slug(modId);
        using var lease = Acquire(binding);
        var current = RequiredCurrent(binding);
        if (!current.Mods.Any(m => m.ModId == modId)) throw new ServiceException(ServiceError.NotFound, "The mod is not installed for this build.");
        var selections = current.Mods.Select(m => m.ModId == modId ? m with { Enabled = enabled } : m).ToArray();
        foreach (var selection in selections) ReadReceipt(selection, binding, verifyPayload: true);
        ValidateConflictFree(selections);
        return Commit(binding, selections, current.RevisionId);
    }

    public ModRevision SetOrder(GameBuildIdentity binding, IReadOnlyList<string> orderedModIds)
    {
        binding = binding.Canonical();
        using var lease = Acquire(binding);
        var current = RequiredCurrent(binding);
        if (orderedModIds is null || orderedModIds.Count != current.Mods.Count || orderedModIds.Distinct(StringComparer.Ordinal).Count() != current.Mods.Count ||
            !orderedModIds.ToHashSet(StringComparer.Ordinal).SetEquals(current.Mods.Select(m => m.ModId)))
            throw new ServiceException(ServiceError.InvalidInput, "Mod order must name every installed mod exactly once.");
        var existing = current.Mods.ToDictionary(m => m.ModId, StringComparer.Ordinal);
        var selections = orderedModIds.Select((id, index) => existing[id] with { Order = index }).ToArray();
        foreach (var selection in selections) ReadReceipt(selection, binding, verifyPayload: true);
        ValidateConflictFree(selections);
        return Commit(binding, selections, current.RevisionId);
    }

    public ModRevision Remove(GameBuildIdentity binding, string modId)
    {
        binding = binding.Canonical();
        Ids.Slug(modId);
        using var lease = Acquire(binding);
        var current = RequiredCurrent(binding);
        if (!current.Mods.Any(m => m.ModId == modId)) throw new ServiceException(ServiceError.NotFound, "The mod is not installed for this build.");
        var selections = current.Mods.Where(m => m.ModId != modId).OrderBy(m => m.Order).Select((m, i) => m with { Order = i }).ToArray();
        return Commit(binding, selections, current.RevisionId);
    }

    public IReadOnlyList<ModConflict> GetConflicts(GameBuildIdentity binding, bool includeDisabled = false)
    {
        binding = binding.Canonical();
        using var lease = Acquire(binding);
        var current = ReadUnlocked(binding);
        return FindConflicts(current?.Mods ?? [], includeDisabled);
    }

    public ModRevision RollbackTo(GameBuildIdentity binding, string revisionId)
    {
        binding = binding.Canonical();
        revisionId = Ids.GuidId(revisionId);
        using var lease = Acquire(binding);
        var current = RequiredCurrent(binding);
        var target = ReadRevision(binding, revisionId);
        foreach (var selection in target.Mods) ReadReceipt(selection, binding, verifyPayload: true);
        ValidateConflictFree(target.Mods);
        return Commit(binding, target.Mods, current.RevisionId);
    }

    public ModOverlayPlan GetActiveOverlay(GameBuildIdentity binding)
    {
        binding = binding.Canonical();
        using var lease = Acquire(binding);
        var current = ReadUnlocked(binding);
        if (current is null) return new ModOverlayPlan(binding, null, []);
        ValidateConflictFree(current.Mods);
        var files = new List<OverlayFile>();
        foreach (var selected in current.Mods.Where(m => m.Enabled).OrderBy(m => m.Order))
        {
            var receipt = ReadReceipt(selected, binding, verifyPayload: true);
            foreach (var file in receipt.Files)
                files.Add(new OverlayFile(file.Path, Paths.Under(_root, "packages/" + selected.ReceiptId + "/payload/" + file.Path), file.Sha256, file.Size, selected.ModId));
        }
        return new ModOverlayPlan(binding, current.RevisionId, files);
    }

    /// <summary>
    /// Returns checked enabled package directories for a native adapter. The adapter must independently verify
    /// the loaded title/build and selected backend pin before mounting; this plan does not establish consumption.
    /// </summary>
    public ModNativeMountPlan GetActiveMounts(GameBuildIdentity binding)
    {
        binding = binding.Canonical();
        if (binding.TitleId.All(c => c == '0') || binding.BuildId.All(c => c == '0'))
            throw new ServiceException(ServiceError.InvalidInput, "Native mounts require a known nonzero title and full build ID.");
        using var lease = Acquire(binding);
        var current = ReadUnlocked(binding);
        if (current is null) return new(binding, null, []);
        ValidateConflictFree(current.Mods);
        var mounts = new List<ModNativeMount>();
        foreach (var selection in current.Mods.Where(m => m.Enabled).OrderBy(m => m.Order))
        {
            var receipt = ReadReceipt(selection, binding, verifyPayload: true);
            mounts.Add(new(selection.ModId, selection.ReceiptId,
                Paths.Under(_root, "packages/" + selection.ReceiptId + "/payload"), selection.Order, receipt.Files));
        }
        return new(binding, current.RevisionId, mounts.ToArray());
    }

    private FileStream Acquire(GameBuildIdentity binding) => _workspace.Acquire("mods-" + binding.TitleId.ToLowerInvariant() + "-" + binding.BuildId.ToLowerInvariant());
    private string BuildRoot(GameBuildIdentity binding) => Paths.EnsureDirectory(_root, "builds/" + binding.TitleId + "/" + binding.BuildId);
    private ModRevision RequiredCurrent(GameBuildIdentity binding) => ReadUnlocked(binding) ?? throw new ServiceException(ServiceError.NotFound, "No mod revision exists for this build.");

    private ModRevision? ReadUnlocked(GameBuildIdentity binding)
    {
        var pointerPath = Paths.Under(BuildRoot(binding), "current.json");
        if (!File.Exists(pointerPath)) return null;
        var pointer = StoreIo.ReadJson<ModPointer>(pointerPath);
        if (pointer.SchemaVersion != 1) throw new ServiceException(ServiceError.CorruptData, "Unknown mod pointer schema.");
        return ReadRevision(binding, Ids.GuidId(pointer.RevisionId));
    }

    private ModRevision ReadRevision(GameBuildIdentity binding, string revisionId)
    {
        var revision = StoreIo.ReadJson<ModRevision>(Paths.Under(BuildRoot(binding), "revisions/" + Ids.GuidId(revisionId) + ".json"));
        if (revision.SchemaVersion != 1 || revision.RevisionId != revisionId || revision.Binding != binding || revision.Mods is null || revision.Mods.Count > 10000 || revision.Mods.Any(m => m is null))
            throw new ServiceException(ServiceError.CorruptData, "The mod revision has an invalid identity or schema.");
        if (revision.ParentRevisionId is not null) Ids.GuidId(revision.ParentRevisionId);
        if (revision.Mods.Select(m => m.ModId).Distinct(StringComparer.Ordinal).Count() != revision.Mods.Count ||
            !revision.Mods.Select(m => m.Order).Order().SequenceEqual(Enumerable.Range(0, revision.Mods.Count)))
            throw new ServiceException(ServiceError.CorruptData, "Mod identifiers or order are not unique and contiguous.");
        foreach (var selection in revision.Mods) ReadReceipt(selection, binding, verifyPayload: false);
        return revision;
    }

    private ModRevision Commit(GameBuildIdentity binding, IReadOnlyList<ModSelection> selections, string? parent)
    {
        var revision = new ModRevision(1, Guid.NewGuid().ToString("N"), parent, binding, selections, DateTimeOffset.UtcNow);
        var buildRoot = BuildRoot(binding);
        StoreIo.AtomicJson(Paths.Under(buildRoot, "revisions/" + revision.RevisionId + ".json"), revision);
        StoreIo.AtomicJson(Paths.Under(buildRoot, "current.json"), new ModPointer(1, revision.RevisionId));
        return revision;
    }

    private ModReceipt ReadReceipt(ModSelection selection, GameBuildIdentity? binding, bool verifyPayload)
    {
        Ids.Slug(selection.ModId);
        Ids.GuidId(selection.ReceiptId);
        var package = Paths.Under(_root, "packages/" + selection.ReceiptId);
        var receipt = StoreIo.ReadJson<ModReceipt>(Paths.Under(package, "receipt.json"));
        if (receipt.SchemaVersion != 1 || receipt.ReceiptId != selection.ReceiptId || receipt.ModId != selection.ModId || receipt.Binding is null ||
            receipt.Binding != receipt.Binding.Canonical() || (binding is not null && receipt.Binding != binding) || receipt.Format != BundleFormat)
            throw new ServiceException(ServiceError.CorruptData, "The ownership receipt does not match the selected mod and exact build.");
        Ids.Slug(receipt.Version);
        StoreIo.ValidateFiles(receipt.Files, _workspace.Limits);
        foreach (var file in receipt.Files) ValidateTargetPath(file.Path);
        if (verifyPayload)
        {
            StoreIo.VerifyTree(Paths.Under(package, "payload"), receipt.Files, _workspace.Limits);
            foreach (var file in receipt.Files) ValidatePayload(Paths.Under(package, "payload/" + file.Path), file.Path);
        }
        return receipt;
    }

    private void ValidateManifest(ModManifest manifest, GameBuildIdentity binding)
    {
        if (manifest.SchemaVersion != 1 || manifest.Format != BundleFormat || manifest.Files is null || manifest.Files.Count == 0)
            throw new ServiceException(ServiceError.InvalidInput, "Only nonempty nx-overlay-v1 manifests with schema version 1 are accepted.");
        Ids.Slug(manifest.ModId);
        Ids.Slug(manifest.Version);
        if (new GameBuildIdentity(manifest.TitleId, manifest.BuildId).Canonical() != binding)
            throw new ServiceException(ServiceError.Conflict, "The mod requires a different exact title or full 64-character build ID.");
        StoreIo.ValidateFiles(manifest.Files, _workspace.Limits);
        foreach (var file in manifest.Files) ValidateTargetPath(file.Path);
    }

    private static void ValidateTargetPath(string path)
    {
        Paths.Relative(path);
        var extension = Path.GetExtension(path);
        if (DisallowedExtensions.Contains(extension))
            throw new ServiceException(ServiceError.InvalidInput, "Host scripts, launchers, libraries, and executable payload files are not accepted as mods.");
        if (path.StartsWith("romfs/", StringComparison.Ordinal)) return;
        if (path.StartsWith("exefs/", StringComparison.Ordinal) && extension.Equals(".ips", StringComparison.OrdinalIgnoreCase)) return;
        throw new ServiceException(ServiceError.InvalidInput, "Mod targets must be RomFS data or structurally valid ExeFS IPS patch data.");
    }

    private static void ValidatePayload(string path, string target)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (target.StartsWith("exefs/", StringComparison.Ordinal)) { ValidateIps(stream); return; }
        Span<byte> header = stackalloc byte[8];
        var count = stream.Read(header);
        if ((count >= 2 && ((header[0] == 'M' && header[1] == 'Z') || (header[0] == '#' && header[1] == '!'))) ||
            (count >= 4 && (header[..4].SequenceEqual(new byte[] { 0x7f, 0x45, 0x4c, 0x46 }) ||
                BinaryPrimitives.ReadUInt32BigEndian(header) is 0xfeedface or 0xfeedfacf or 0xcefaedfe or 0xcffaedfe or 0xcafebabe)))
            throw new ServiceException(ServiceError.InvalidInput, "An executable or script signature was found in a mod payload.");
    }

    private static void ValidateIps(Stream stream)
    {
        try
        {
            Span<byte> header = stackalloc byte[5];
            stream.ReadExactly(header);
            var is32 = header.SequenceEqual("IPS32"u8);
            if (!is32 && !header.SequenceEqual("PATCH"u8)) throw new ServiceException(ServiceError.CorruptData, "An IPS patch has an invalid header.");
            var width = is32 ? 4 : 3;
            Span<byte> offset = stackalloc byte[4];
            Span<byte> size = stackalloc byte[2];
            while (true)
            {
                stream.ReadExactly(offset[..width]);
                if (offset[..width].SequenceEqual(is32 ? "EEOF"u8 : "EOF"u8))
                {
                    if (stream.Length - stream.Position is var remaining && remaining != 0 && remaining != width)
                        throw new ServiceException(ServiceError.CorruptData, "An IPS patch has unexpected trailing data.");
                    return;
                }
                stream.ReadExactly(size);
                var count = BinaryPrimitives.ReadUInt16BigEndian(size);
                if (count == 0)
                {
                    stream.ReadExactly(size);
                    if (BinaryPrimitives.ReadUInt16BigEndian(size) == 0 || stream.ReadByte() < 0)
                        throw new ServiceException(ServiceError.CorruptData, "An IPS patch has an invalid run-length record.");
                }
                else
                {
                    if (stream.Length - stream.Position < count) throw new EndOfStreamException();
                    stream.Seek(count, SeekOrigin.Current);
                }
            }
        }
        catch (EndOfStreamException ex) { throw new ServiceException(ServiceError.CorruptData, "An IPS patch is truncated.", ex); }
    }

    private IReadOnlyList<ModConflict> FindConflicts(IReadOnlyList<ModSelection> selections, bool includeDisabled)
    {
        var owners = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var selection in selections.Where(m => includeDisabled || m.Enabled))
        {
            var receipt = ReadReceipt(selection, binding: null, verifyPayload: false);
            foreach (var file in receipt.Files)
            {
                if (!owners.TryGetValue(file.Path, out var ids)) owners[file.Path] = ids = new(StringComparer.Ordinal);
                ids.Add(selection.ModId);
            }
        }
        var conflicts = owners.Where(kv => kv.Value.Count > 1).ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value), StringComparer.OrdinalIgnoreCase);
        foreach (var pair in owners)
        {
            var parent = pair.Key;
            while (parent.Contains('/'))
            {
                parent = parent[..parent.LastIndexOf('/')];
                if (!owners.TryGetValue(parent, out var parentOwners)) continue;
                if (!conflicts.TryGetValue(parent, out var ids)) conflicts[parent] = ids = new(StringComparer.Ordinal);
                ids.UnionWith(pair.Value);
                ids.UnionWith(parentOwners);
            }
        }
        return conflicts.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new ModConflict(kv.Key, kv.Value.Order(StringComparer.Ordinal).ToArray())).ToArray();
    }

    private void ValidateConflictFree(IReadOnlyList<ModSelection> selections)
    {
        var conflicts = FindConflicts(selections, includeDisabled: false);
        if (conflicts.Count > 0)
            throw new ServiceException(ServiceError.Conflict, "Enabled mods overlap: " + string.Join(", ", conflicts.Select(c => c.Path)) + ". Disable a conflicting mod; order never silently chooses a winner.");
    }
}
