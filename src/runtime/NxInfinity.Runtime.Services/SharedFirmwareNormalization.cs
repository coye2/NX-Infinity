using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace NxInfinity.Runtime.Services;

public sealed partial class SharedPrerequisiteStore
{
    // Container projection only. This does not decrypt, authenticate, install or
    // declare a firmware package complete. The exact worker must subsequently
    // validate the projected NCAs and bind its import and fresh inventory receipt.
    private const int FirmwareMaximumArchives = 4096;
    private const long FirmwareMaximumFileBytes = 4L * 1024 * 1024 * 1024;
    private const long FirmwareMaximumTotalBytes = 8L * 1024 * 1024 * 1024;

    private sealed class FirmwareProjection(string source, string? temporary, StorageLimits limits) : IDisposable
    {
        internal string Source { get; } = source;
        internal List<ContentFile> Files { get; } = [];
        public void Dispose()
        {
            if (temporary is null) return;
            try
            {
                // An interrupted/incomplete or externally changed projection is
                // retained privately. Never recursively remove unrecognized data.
                StoreIo.VerifyTree(temporary, Files, limits);
                foreach (var file in Files) File.Delete(Paths.Under(temporary, file.Path));
                Directory.Delete(temporary, recursive: false);
            }
            catch (Exception error) when (error is ServiceException or IOException or UnauthorizedAccessException) { }
        }
    }

    private async Task<FirmwareProjection> PrepareFirmwareSourceAsync(SourceRecord source, NxRuntime runtime, CancellationToken ct)
    {
        string payload = SourcePayload(source);
        if (source.Receipt.Kind != "firmware" || source.Receipt.SourceForm == "directory" ||
            !runtime.Supports("prerequisites.import.firmware.directory"))
            return new(payload, null, _workspace.Limits);
        if (!runtime.Supports("prerequisites.firmware.inspect") || !runtime.Supports("prerequisites.import.firmware"))
            throw new NxException("unsupported", "This core cannot validate and bind a normalized firmware package.");
        if (source.Receipt.SourceForm is not ("zip" or "xci"))
            throw new ServiceException(ServiceError.CorruptData, "The retained firmware source form is invalid.");

        VerifySource(source);
        string directory = Paths.Under(_root, ".firmware-projection-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(directory) || File.Exists(directory))
            throw new ServiceException(ServiceError.Conflict, "The private firmware projection path is occupied.");
        Directory.CreateDirectory(directory); PrivateDirectory(directory);
        var projection = new FirmwareProjection(directory, directory, _workspace.Limits);
        try
        {
            await using var input = new FileStream(payload, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.RandomAccess);
            if (source.Receipt.SourceForm == "zip") await ProjectFirmwareZipAsync(input, projection, ct);
            else await ProjectFirmwareXciAsync(input, projection, ct);
            ct.ThrowIfCancellationRequested();
            VerifySource(source);
            StoreIo.VerifyTree(directory, projection.Files, _workspace.Limits);
            return projection;
        }
        catch (Exception error) when (error is InvalidDataException or EndOfStreamException or OverflowException or DecoderFallbackException)
        {
            projection.Dispose();
            throw new ServiceException(ServiceError.InvalidInput, "The firmware container is truncated, malformed, or outside the supported bounds.");
        }
        catch { projection.Dispose(); throw; }
    }

    private static void FirmwareContainerRequire(bool condition, string message)
    { if (!condition) throw new ServiceException(ServiceError.InvalidInput, message); }

    private static string FirmwareArchiveName(string name)
    {
        string value = name.ToLowerInvariant();
        bool extension = value.EndsWith(".cnmt.nca", StringComparison.Ordinal) ? value.Length == 41
            : value.EndsWith(".nca", StringComparison.Ordinal) && value.Length == 36;
        FirmwareContainerRequire(extension && value.Take(32).All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'),
            "Firmware containers may contain only archives named by their 32 digit content ID.");
        return value[..32] + ".nca";
    }

    private long ValidateFirmwareProjectionSizes(IEnumerable<long> sizes)
    {
        long total = 0; int count = 0;
        foreach (long size in sizes)
        {
            count++;
            FirmwareContainerRequire(size >= 0xc00 && size <= Math.Min(FirmwareMaximumFileBytes, _workspace.Limits.MaximumFileBytes),
                "A firmware archive is empty, truncated or larger than the supported limit.");
            total = checked(total + size);
            FirmwareContainerRequire(total <= Math.Min(FirmwareMaximumTotalBytes, _workspace.Limits.MaximumTotalBytes),
                "The expanded firmware package exceeds the configured private storage limit.");
        }
        FirmwareContainerRequire(count is >= 3 and <= FirmwareMaximumArchives && count <= _workspace.Limits.MaximumFiles,
            "A supported firmware package requires between 3 and 4096 bounded archives.");
        return total;
    }

    private async Task ProjectFirmwareZipAsync(FileStream input, FirmwareProjection projection, CancellationToken ct)
    {
        using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        FirmwareContainerRequire(zip.Entries.Count <= FirmwareMaximumArchives * 4, "The firmware ZIP has too many entries.");
        var entries = new List<(ZipArchiveEntry Entry, string Name)>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            string name = entry.FullName;
            bool directory = name.EndsWith('/');
            string[] parts = name.TrimEnd('/').Split('/');
            int unixType = (entry.ExternalAttributes >> 16) & 0xf000;
            FirmwareContainerRequire(name.Length is > 0 and <= 2048 && parts.Length <= 8 &&
                !name.Contains('\\') && !name.Contains(':') && !name.Contains('\0') && !name.StartsWith('/') &&
                parts.All(p => p.Length > 0 && p is not ("." or "..")) &&
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) == 0 &&
                (unixType == 0 || unixType == (directory ? 0x4000 : 0x8000)),
                "Firmware ZIP paths must be relative, bounded, and free of links or special files.");
            if (directory)
            {
                FirmwareContainerRequire(entry.Length == 0, "A ZIP directory entry contains unexpected data.");
                continue;
            }
            string canonical = FirmwareArchiveName(parts[^1]);
            FirmwareContainerRequire(ids.Add(canonical), "The firmware ZIP repeats a content ID.");
            entries.Add((entry, canonical));
        }
        ValidateFirmwareProjectionSizes(entries.Select(e => e.Entry.Length));
        foreach (var (entry, name) in entries)
        {
            await using var body = entry.Open();
            await CopyFirmwareProjectionAsync(body, entry.Length, name, projection, ct);
            FirmwareContainerRequire(await body.ReadAsync(new byte[1], ct) == 0, "The firmware ZIP entry exceeds its declared length.");
        }
    }

    private sealed record HfsEntry(string Name, long Offset, long Size, int HashSize, byte[] Hash);
    private sealed record HfsDirectory(int HeaderSize, IReadOnlyList<HfsEntry> Entries);

    private static long FirmwareLong(ReadOnlySpan<byte> bytes)
    {
        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        FirmwareContainerRequire(value <= long.MaxValue, "The firmware container has an invalid 64 bit range.");
        return (long)value;
    }

    private static byte[] FirmwareReadAt(FileStream input, long offset, int length)
    {
        FirmwareContainerRequire(offset >= 0 && length >= 0 && offset <= input.Length && length <= input.Length - offset,
            "A firmware container metadata range is truncated.");
        byte[] bytes = new byte[length]; input.Position = offset; input.ReadExactly(bytes); return bytes;
    }

    private static HfsDirectory ReadFirmwareHfs(FileStream input, long offset, long length, int maximumEntries)
    {
        FirmwareContainerRequire(offset >= 0 && length >= 16 && offset <= input.Length && length <= input.Length - offset,
            "The XCI partition range is invalid.");
        byte[] header = FirmwareReadAt(input, offset, 16);
        FirmwareContainerRequire(header.AsSpan(0, 4).SequenceEqual("HFS0"u8), "This XCI source does not expose the supported HFS0 update layout.");
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        uint strings = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        FirmwareContainerRequire(count > 0 && count <= maximumEntries && strings is > 0 and <= 2 * 1024 * 1024 &&
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12)) == 0,
            "The XCI partition metadata is outside the supported bounds.");
        int metadataSize = checked(16 + (int)count * 64 + (int)strings);
        FirmwareContainerRequire(metadataSize <= length, "The XCI partition metadata is truncated.");
        byte[] metadata = FirmwareReadAt(input, offset, metadataSize);
        int stringBase = 16 + (int)count * 64;
        var entries = new List<HfsEntry>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count; i++)
        {
            var record = metadata.AsSpan(16 + i * 64, 64);
            long relative = FirmwareLong(record), size = FirmwareLong(record[8..]);
            uint stringOffset = BinaryPrimitives.ReadUInt32LittleEndian(record[16..]);
            uint hashSize = BinaryPrimitives.ReadUInt32LittleEndian(record[20..]);
            FirmwareContainerRequire(relative <= length - metadataSize && size <= length - metadataSize - relative &&
                stringOffset < strings && hashSize <= size && hashSize <= 4 * 1024 * 1024 && FirmwareLong(record[24..]) == 0,
                "An XCI partition entry is truncated, overlaps metadata, or uses an unsupported hash range.");
            int start = checked(stringBase + (int)stringOffset), end = Array.IndexOf(metadata, (byte)0, start);
            FirmwareContainerRequire(end >= start && end - start <= 256, "The XCI partition name is invalid.");
            string name = new UTF8Encoding(false, true).GetString(metadata, start, end - start);
            FirmwareContainerRequire(name.Length > 0 && !name.Contains('/') && !name.Contains('\\') && !name.Contains(':') &&
                name is not ("." or "..") && names.Add(name), "The XCI partition repeats a name or contains an unsafe path.");
            entries.Add(new(name, checked(offset + metadataSize + relative), size, (int)hashSize, record[32..64].ToArray()));
        }
        long occupiedEnd = offset + metadataSize;
        foreach (var entry in entries.OrderBy(e => e.Offset))
        {
            FirmwareContainerRequire(entry.Offset >= occupiedEnd, "The XCI partition contains overlapping entries.");
            occupiedEnd = checked(entry.Offset + entry.Size);
        }
        return new(metadataSize, entries);
    }

    private static async Task CheckFirmwareRangeHashAsync(FileStream input, long offset, int length, byte[] expected, CancellationToken ct)
    {
        FirmwareContainerRequire(length > 0, "The selected XCI firmware partition has no bounded hash region.");
        byte[] bytes = FirmwareReadAt(input, offset, length);
        ct.ThrowIfCancellationRequested();
        // HFS/HEAD hashes check container consistency, never Nintendo signatures.
        byte[] actual = SHA256.HashData(bytes);
        FirmwareContainerRequire(CryptographicOperations.FixedTimeEquals(actual, expected), "The XCI firmware container hash does not match its selected bytes.");
        await Task.CompletedTask;
    }

    private async Task ProjectFirmwareXciAsync(FileStream input, FirmwareProjection projection, CancellationToken ct)
    {
        byte[] header = FirmwareReadAt(input, 0, 0x200);
        FirmwareContainerRequire(header.AsSpan(0x100, 4).SequenceEqual("HEAD"u8), "The selected source has no supported XCI HEAD header.");
        long rootOffset = FirmwareLong(header.AsSpan(0x130)), hashSize = FirmwareLong(header.AsSpan(0x138));
        FirmwareContainerRequire(rootOffset >= 0x200 && rootOffset < input.Length && hashSize is > 0 and <= 4 * 1024 * 1024,
            "The selected XCI root partition is outside the supported bounds.");
        var root = ReadFirmwareHfs(input, rootOffset, input.Length - rootOffset, 4);
        FirmwareContainerRequire(hashSize >= root.HeaderSize && root.Entries.All(e => e.Name is "update" or "normal" or "secure" or "logo"),
            "This XCI partition variant is not supported for firmware extraction.");
        await CheckFirmwareRangeHashAsync(input, rootOffset, (int)hashSize, header.AsSpan(0x140, 32).ToArray(), ct);
        var update = root.Entries.SingleOrDefault(e => e.Name == "update");
        FirmwareContainerRequire(update is not null, "The selected XCI does not contain an update partition.");
        await CheckFirmwareRangeHashAsync(input, update!.Offset, update.HashSize, update.Hash, ct);
        var firmware = ReadFirmwareHfs(input, update.Offset, update.Size, FirmwareMaximumArchives);
        FirmwareContainerRequire(update.HashSize >= 16, "The XCI update partition hash does not cover its directory header.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in firmware.Entries)
            FirmwareContainerRequire(names.Add(FirmwareArchiveName(entry.Name)), "The XCI update partition repeats a content ID.");
        ValidateFirmwareProjectionSizes(firmware.Entries.Select(e => e.Size));
        foreach (var entry in firmware.Entries)
        {
            await CheckFirmwareRangeHashAsync(input, entry.Offset, entry.HashSize, entry.Hash, ct);
            input.Position = entry.Offset;
            await CopyFirmwareProjectionAsync(input, entry.Size, FirmwareArchiveName(entry.Name), projection, ct);
        }
        // Secure, normal and logo content are not opened or imported by this route.
    }

    private async Task CopyFirmwareProjectionAsync(Stream input, long length, string name, FirmwareProjection projection, CancellationToken ct)
    {
        string destination = Paths.Under(projection.Source, name);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            65536, FileOptions.Asynchronous);
        PrivateFile(destination);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536]; long remaining = length;
        while (remaining > 0)
        {
            int read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
            FirmwareContainerRequire(read > 0, "A firmware container ended before its declared archive size.");
            hash.AppendData(buffer.AsSpan(0, read));
            await output.WriteAsync(buffer.AsMemory(0, read), ct); remaining -= read;
        }
        await output.FlushAsync(ct); output.Flush(flushToDisk: true);
        projection.Files.Add(new(name, Convert.ToHexStringLower(hash.GetHashAndReset()), length));
    }
}
