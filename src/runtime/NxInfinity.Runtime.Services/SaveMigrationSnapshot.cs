using System.Security.Cryptography;
using System.Text.Json;

namespace NxInfinity.Runtime.Services;

public sealed partial class SaveMigrationStore
{
    private async Task<CopiedTree> CopyCheckedAsync(string source, string destination, CancellationToken ct)
    {
        Paths.RejectLinks(source); Paths.RejectLinks(destination);
        var directories = new List<string>(); var paths = Paths.EnumerateFiles(source, _workspace.Limits, directories);
        Directory.CreateDirectory(destination);
        foreach (var directory in directories) Paths.EnsureDirectory(destination, directory);
        var files = new List<ContentFile>(); long total = 0;
        foreach (var relative in paths)
        {
            ct.ThrowIfCancellationRequested();
            string original = Paths.Under(source, relative), copied = Paths.Under(destination, relative);
            await using var input = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            await using var output = new FileStream(copied, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[65536]; long length = 0; int count;
            while ((count = await input.ReadAsync(buffer, ct)) > 0)
            {
                length = checked(length + count); total = checked(total + count);
                if (length > _workspace.Limits.MaximumFileBytes || total > _workspace.Limits.MaximumTotalBytes)
                    throw new ServiceException(ServiceError.InvalidInput, "The source save exceeds its bounded copy limit.");
                digest.AppendData(buffer.AsSpan(0, count)); await output.WriteAsync(buffer.AsMemory(0, count), ct);
            }
            await output.FlushAsync(ct); output.Flush(flushToDisk: true);
            files.Add(new(relative, Convert.ToHexStringLower(digest.GetHashAndReset()), length));
        }
        var tree = new CopiedTree(files, directories.Order(StringComparer.Ordinal).ToArray());
        VerifyUnchanged(source, tree);
        return tree;
    }

    private async Task<CopiedTree> InventoryAsync(string source, CancellationToken ct)
    {
        var directories = new List<string>(); var paths = Paths.EnumerateFiles(source, _workspace.Limits, directories);
        var files = new List<ContentFile>();
        foreach (var relative in paths)
        {
            ct.ThrowIfCancellationRequested();
            await using var stream = new FileStream(Paths.Under(source, relative), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            files.Add(new(relative, Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)), stream.Length));
        }
        return new(files, directories.Order(StringComparer.Ordinal).ToArray());
    }
    private void VerifyUnchanged(string source, CopiedTree expected)
    {
        StoreIo.VerifyTree(source, expected.Files, _workspace.Limits);
        var directories = new List<string>(); Paths.EnumerateFiles(source, _workspace.Limits, directories);
        if (!directories.Order(StringComparer.Ordinal).SequenceEqual(expected.Directories.Order(StringComparer.Ordinal)))
            throw new ServiceException(ServiceError.CorruptData, "The source save directory inventory changed during verification.");
    }
    private static string TreeFingerprint(CopiedTree tree) => HashText(JsonSerializer.Serialize(new
    { files = tree.Files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray(), directories = tree.Directories.Order(StringComparer.Ordinal).ToArray() }));
    private static void TryDeleteProbes(string root)
    {
        // An unexpectedly live native worker retains its private clone for
        // recovery. Never remove a tree until its worker lease is released.
        foreach (var path in Directory.EnumerateFiles(root, ".nx-runtime.lock", SearchOption.AllDirectories))
        {
            try { using var lease = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { return; }
        }
        Directory.Delete(root, recursive: true);
    }
}
