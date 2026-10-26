using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace NxInfinity.Runtime.Services;

public enum ServiceError
{
    InvalidInput, UnsafePath, Busy, CorruptData, NotFound, Conflict,
    Unavailable, ExperimentalOptInRequired, UnverifiedSaveMapping, RecoveryRequired
}

public sealed class ServiceException : Exception
{
    public ServiceError Code { get; }
    public ServiceException(ServiceError code, string message, Exception? inner = null) : base(message, inner) => Code = code;
}

public sealed record StorageLimits(int MaximumFiles = 10000, long MaximumFileBytes = 8L * 1024 * 1024 * 1024,
    long MaximumTotalBytes = 16L * 1024 * 1024 * 1024);

/// <summary>All service-owned data and incoming bundles remain under this caller-owned workspace.</summary>
public sealed class NxWorkspace
{
    public string RootPath { get; }
    public string ImportsPath { get; }
    public StorageLimits Limits { get; }
    internal string DataPath { get; }

    public NxWorkspace(string rootPath, StorageLimits? limits = null)
    {
        if (!Path.IsPathFullyQualified(rootPath))
            throw new ServiceException(ServiceError.UnsafePath, "The workspace path must be absolute.");
        RootPath = Path.GetFullPath(rootPath);
        Limits = limits ?? new();
        if (Limits.MaximumFiles <= 0 || Limits.MaximumFileBytes < 0 || Limits.MaximumTotalBytes < 0)
            throw new ServiceException(ServiceError.InvalidInput, "Storage limits are invalid.");
        Paths.RejectLinks(RootPath);
        Directory.CreateDirectory(RootPath);
        ImportsPath = Paths.EnsureDirectory(RootPath, "imports");
        DataPath = Paths.EnsureDirectory(RootPath, "nx-services");
        Paths.EnsureDirectory(DataPath, "locks");
    }

    public string ResolveImportDirectory(string relativePath)
    {
        var path = Paths.Under(ImportsPath, relativePath);
        if (!Directory.Exists(path)) throw new ServiceException(ServiceError.NotFound, "The incoming directory does not exist.");
        return path;
    }

    internal FileStream Acquire(string name) => StoreIo.AcquireLock(Paths.Under(DataPath, "locks/" + Ids.Slug(name) + ".lock"));
}

internal static partial class Ids
{
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,95}$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();
    [GeneratedRegex("^[0-9a-fA-F]+$", RegexOptions.CultureInvariant)]
    private static partial Regex HexPattern();

    internal static string Slug(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !SlugPattern().IsMatch(value) || value is "." or "..")
            throw new ServiceException(ServiceError.InvalidInput, "An identifier must contain 1–96 lowercase letters, digits, dots, underscores, or hyphens.");
        return value;
    }

    internal static string Hex(string value, int length, string name)
    {
        if (value is null || value.Length != length || !HexPattern().IsMatch(value))
            throw new ServiceException(ServiceError.InvalidInput, $"{name} must contain exactly {length} hexadecimal characters.");
        return value.ToUpperInvariant();
    }

    internal static string GuidId(string value)
    {
        if (!Guid.TryParseExact(value, "N", out var id))
            throw new ServiceException(ServiceError.InvalidInput, "A stored receipt or revision ID is invalid.");
        return id.ToString("N");
    }
}

internal static class Paths
{
    private static readonly HashSet<string> Devices = new(StringComparer.OrdinalIgnoreCase)
    { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

    internal static string Relative(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 240 || Path.IsPathRooted(value) || value.Contains('\\'))
            throw new ServiceException(ServiceError.UnsafePath, "Only portable relative paths using '/' are accepted.");
        var parts = value.Split('/');
        foreach (var part in parts)
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.Any(c => c < 32 || "<>:\"|?*".Contains(c)) || Devices.Contains(part.Split('.')[0]))
                throw new ServiceException(ServiceError.UnsafePath, "A path contains an unsafe or nonportable component.");
        }
        return string.Join('/', parts);
    }

    internal static string Under(string root, string relative)
    {
        relative = Relative(relative);
        var canonicalRoot = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(canonicalRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.TrimEndingDirectorySeparator(canonicalRoot) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ServiceException(ServiceError.UnsafePath, "A path escapes its owned root.");
        RejectLinks(path);
        return path;
    }

    internal static string EnsureDirectory(string root, string relative)
    {
        var path = Under(root, relative);
        Directory.CreateDirectory(path);
        RejectLinks(path);
        return path;
    }

    internal static void RejectLinks(string path)
    {
        var full = Path.GetFullPath(path);
        var volumeRoot = Path.GetPathRoot(full)!;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            // Proton's mapped drive root can carry a reparse-point flag.
            // It is an OS boundary, not an in-workspace redirect. Exempt ONLY
            // that root, not an alias in the user profile, data folder, imported
            // package, save tree, or any other descendant. Keep the protection
            // effective for mutable data and for imported firmware/keys.
            if (string.Equals(current, volumeRoot, comparison))
                break;

            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new ServiceException(ServiceError.UnsafePath,
                        "A symbolic link or junction inside the selected NX data path is not supported.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static IReadOnlyList<string> EnumerateFiles(string root, StorageLimits limits, ICollection<string>? directoryPaths = null)
    {
        RejectLinks(root);
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(root);
        var directories = 0;
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            RejectLinks(directory);
            foreach (var item in Directory.EnumerateFileSystemEntries(directory))
            {
                RejectLinks(item);
                var relative = Relative(Path.GetRelativePath(root, item).Replace(Path.DirectorySeparatorChar, '/'));
                if (!seen.Add(relative)) throw new ServiceException(ServiceError.UnsafePath, "Case-colliding paths are not portable.");
                if ((File.GetAttributes(item) & FileAttributes.Directory) != 0)
                {
                    if (++directories > limits.MaximumFiles + 64)
                        throw new ServiceException(ServiceError.InvalidInput, "A directory tree exceeds its directory limit.");
                    pending.Push(item);
                    directoryPaths?.Add(relative);
                }
                else
                {
                    if (files.Count >= limits.MaximumFiles)
                        throw new ServiceException(ServiceError.InvalidInput, "A directory tree exceeds its file limit.");
                    files.Add(relative);
                }
            }
        }
        files.Sort(StringComparer.Ordinal);
        return files;
    }
}

internal static class StoreIo
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    internal static FileStream AcquireLock(string path)
    {
        Paths.RejectLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new ServiceException(ServiceError.Busy, "The workspace or backend data root is in use by a running session or another operation.", ex); }
    }

    internal static T ReadJson<T>(string path, long maximumBytes = 8 * 1024 * 1024)
    {
        Paths.RejectLinks(path);
        if (!File.Exists(path)) throw new ServiceException(ServiceError.NotFound, "A required metadata file does not exist.");
        if (new FileInfo(path).Length > maximumBytes) throw new ServiceException(ServiceError.CorruptData, "A metadata file exceeds its size limit.");
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 48 });
            ValidateProperties(doc.RootElement);
            return doc.Deserialize<T>(Json) ?? throw new JsonException("Null document.");
        }
        catch (JsonException ex) { throw new ServiceException(ServiceError.CorruptData, "Stored metadata is invalid; it was not reset or overwritten.", ex); }
    }

    private static void ValidateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate JSON property.");
                ValidateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) ValidateProperties(child);
    }

    internal static void AtomicJson<T>(string path, T value, JsonSerializerOptions? options = null)
    {
        Paths.RejectLinks(path);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, ".write-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, options ?? Json);
                stream.Flush(flushToDisk: true);
            }
            Paths.RejectLinks(path);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static ContentFile CopyHashed(string source, string destination, string relative, StorageLimits limits, long remainingBytes)
    {
        Paths.RejectLinks(source);
        Paths.RejectLinks(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long size = 0;
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            size = checked(size + read);
            if (size > limits.MaximumFileBytes || size > remainingBytes)
                throw new ServiceException(ServiceError.InvalidInput, "Payload exceeds its configured byte limit.");
            hash.AppendData(buffer.AsSpan(0, read));
            output.Write(buffer, 0, read);
        }
        output.Flush(flushToDisk: true);
        Paths.RejectLinks(source);
        return new ContentFile(relative, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), size);
    }

    internal static void ValidateFiles(IReadOnlyList<ContentFile>? files, StorageLimits limits)
    {
        if (files is null || files.Count > limits.MaximumFiles)
            throw new ServiceException(ServiceError.CorruptData, "The payload file list is missing or too large.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in files)
        {
            if (file is null) throw new ServiceException(ServiceError.CorruptData, "A payload file entry is null.");
            Paths.Relative(file.Path);
            Ids.Hex(file.Sha256, 64, "SHA-256");
            if (file.Size < 0 || file.Size > limits.MaximumFileBytes || !seen.Add(file.Path))
                throw new ServiceException(ServiceError.CorruptData, "A payload file has an invalid size or duplicate path.");
            total = checked(total + file.Size);
            if (total > limits.MaximumTotalBytes) throw new ServiceException(ServiceError.CorruptData, "The payload exceeds its total size limit.");
        }
        foreach (var file in files)
        {
            var prefix = file.Path;
            while (prefix.Contains('/'))
            {
                prefix = prefix[..prefix.LastIndexOf('/')];
                if (seen.Contains(prefix)) throw new ServiceException(ServiceError.CorruptData, "Payload paths contain a file/directory collision.");
            }
        }
    }

    internal static void VerifyFile(string path, ContentFile expected)
    {
        Paths.RejectLinks(path);
        if (!File.Exists(path) || new FileInfo(path).Length != expected.Size)
            throw new ServiceException(ServiceError.CorruptData, "A payload file is missing or has the wrong size.");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var actual = Convert.ToHexString(SHA256.HashData(input));
        if (!actual.Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new ServiceException(ServiceError.CorruptData, "A payload file does not match its SHA-256 receipt.");
    }

    internal static void VerifyTree(string payloadRoot, IReadOnlyList<ContentFile> files, StorageLimits limits)
    {
        ValidateFiles(files, limits);
        if (!Directory.Exists(payloadRoot)) throw new ServiceException(ServiceError.CorruptData, "The payload directory is missing.");
        var actual = Paths.EnumerateFiles(payloadRoot, limits);
        if (!actual.ToHashSet(StringComparer.Ordinal).SetEquals(files.Select(f => f.Path)))
            throw new ServiceException(ServiceError.CorruptData, "The payload contains missing or unlisted files.");
        foreach (var file in files) VerifyFile(Paths.Under(payloadRoot, file.Path), file);
    }

    internal static CopiedTree CopyTree(string source, string destination, StorageLimits limits)
    {
        Directory.CreateDirectory(destination);
        var results = new List<ContentFile>();
        var directories = new List<string>();
        var sourceFiles = Paths.EnumerateFiles(source, limits, directories);
        foreach (var directory in directories) Paths.EnsureDirectory(destination, directory);
        long total = 0;
        foreach (var path in sourceFiles)
        {
            var file = CopyHashed(Paths.Under(source, path), Paths.Under(destination, path), path, limits, limits.MaximumTotalBytes - total);
            total = checked(total + file.Size);
            results.Add(file);
        }
        return new CopiedTree(results, directories.Order(StringComparer.Ordinal).ToArray());
    }
}

public sealed record ContentFile(string Path, string Sha256, long Size);
internal sealed record CopiedTree(IReadOnlyList<ContentFile> Files, IReadOnlyList<string> Directories);

