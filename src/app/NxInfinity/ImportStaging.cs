using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NxInfinity.Runtime.Services;

namespace NxInfinity;

public static class ImportStaging
{
    private static void RejectLinks(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string pathRoot = Path.GetPathRoot(fullPath);
        StringComparison comparisonType = (OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        string text = fullPath;
        while (text != null && !string.Equals(text, pathRoot, comparisonType))
        {
            if ((File.Exists(text) || Directory.Exists(text)) && (File.GetAttributes(text) & FileAttributes.ReparsePoint) != 0)
            {
                throw new ServiceException(ServiceError.UnsafePath, "Import/export paths cannot traverse symbolic links or junctions.");
            }
            text = Path.GetDirectoryName(text);
        }
    }

    public static async Task<string> CopyDirectoryAsync(string source, NxWorkspace workspace, CancellationToken ct)
    {
        source = Path.GetFullPath(source);
        RejectLinks(source);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException("Choose an existing snapshot or mod-bundle folder.");
        }
        string id = "infinity-" + Guid.NewGuid().ToString("N");
        string destination = Path.Combine(workspace.ImportsPath, id);
        RejectLinks(destination);
        Directory.CreateDirectory(destination);
        try
        {
            Stack<(string Source, string Target, int Depth)> pending = new Stack<(string, string, int)>();
            pending.Push((source, destination, 0));
            int count = 0;
            long total = 0L;
            (string Source, string Target, int Depth) directory;
            while (pending.TryPop(out directory))
            {
                ct.ThrowIfCancellationRequested();
                if (directory.Depth > 64)
                {
                    throw new ServiceException(ServiceError.InvalidInput, "The selected folder is too deeply nested.");
                }
                foreach (FileSystemInfo item in new DirectoryInfo(directory.Source).EnumerateFileSystemInfos())
                {
                    ct.ThrowIfCancellationRequested();
                    int num = count + 1;
                    count = num;
                    if (num > workspace.Limits.MaximumFiles * 2)
                    {
                        throw new ServiceException(ServiceError.InvalidInput, "The selected bundle exceeds the file/directory limit.");
                    }
                    RejectLinks(item.FullName);
                    string text = Path.Combine(directory.Target, item.Name);
                    if (item is DirectoryInfo)
                    {
                        Directory.CreateDirectory(text);
                        pending.Push((item.FullName, text, directory.Depth + 1));
                        continue;
                    }
                    long length = ((FileInfo)item).Length;
                    if (length > workspace.Limits.MaximumFileBytes || total + length > workspace.Limits.MaximumTotalBytes)
                    {
                        throw new ServiceException(ServiceError.InvalidInput, "The selected bundle exceeds the import size limit.");
                    }
                    await using FileStream input = new FileStream(item.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, useAsync: true);
                    await using FileStream output = new FileStream(text, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, useAsync: true);
                    byte[] buffer = new byte[131072];
                    long copied = 0L;
                    int num2;
                    while ((num2 = await input.ReadAsync(buffer, ct)) != 0)
                    {
                        copied += num2;
                        total += num2;
                        if (copied > workspace.Limits.MaximumFileBytes || total > workspace.Limits.MaximumTotalBytes)
                        {
                            throw new ServiceException(ServiceError.InvalidInput, "The source grew beyond the import size limit.");
                        }
                        await output.WriteAsync(buffer.AsMemory(0, num2), ct);
                    }
                    if (copied != length)
                    {
                        throw new ServiceException(ServiceError.CorruptData, "A selected source changed while being copied.");
                    }
                }
            }
            return id;
        }
        catch
        {
            Directory.Delete(destination, recursive: true);
            throw;
        }
    }

    public static async Task<string> CopyModBundleAsync(string source, NxWorkspace workspace, GameBuildIdentity binding, CancellationToken ct)
    {
        source = Path.GetFullPath(source);
        RejectLinks(source);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException("Choose an extracted mod folder.");
        }
        if (File.Exists(Path.Combine(source, "manifest.json")))
        {
            return await CopyDirectoryAsync(source, workspace, ct);
        }
        string candidate = source;
        for (int i = 0; i < 5; i++)
        {
            if (IsModRoot(candidate))
            {
                break;
            }
            RejectLinks(candidate);
            string[] directories = Directory.GetDirectories(candidate);
            if (directories.Length != 1 || Directory.GetFiles(candidate).Length != 0)
            {
                break;
            }
            candidate = directories[0];
        }
        for (DirectoryInfo directoryInfo = new DirectoryInfo(candidate); directoryInfo != null; directoryInfo = directoryInfo.Parent)
        {
            if (directoryInfo.Name.Length == 16 && directoryInfo.Name.All(Uri.IsHexDigit) && !directoryInfo.Name.Equals(binding.TitleId, StringComparison.OrdinalIgnoreCase))
            {
                throw new ServiceException(ServiceError.InvalidInput, "This mod folder names a different title ID. Select its matching game.");
            }
            if (directoryInfo.FullName == source)
            {
                break;
            }
        }
        if (!IsModRoot(candidate))
        {
            throw new ServiceException(ServiceError.InvalidInput, "Choose one extracted mod folder containing romfs/ or exefs/, or an NX manifest.json bundle.");
        }
        string relative = await CopyDirectoryAsync(candidate, workspace, ct);
        string text = Path.Combine(workspace.ImportsPath, relative);
        string text2 = Path.Combine(text, "payload");
        Directory.CreateDirectory(text2);
        string[] array = new string[2] { "romfs", "exefs" };
        foreach (string path in array)
        {
            string text3 = Path.Combine(text, path);
            if (Directory.Exists(text3))
            {
                Directory.Move(text3, Path.Combine(text2, path));
            }
        }
        foreach (string item in Directory.EnumerateFileSystemEntries(text))
        {
            if (!string.Equals(item, text2, StringComparison.OrdinalIgnoreCase))
            {
                if (Directory.Exists(item))
                {
                    Directory.Delete(item, recursive: true);
                }
                else
                {
                    File.Delete(item);
                }
            }
        }
        List<ContentFile> list = new List<ContentFile>();
        foreach (string item2 in Directory.EnumerateFiles(text2, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            RejectLinks(item2);
            string path2 = Path.GetRelativePath(text2, item2).Replace(Path.DirectorySeparatorChar, '/');
            using FileStream fileStream = File.OpenRead(item2);
            string sha = Convert.ToHexString(SHA256.HashData(fileStream)).ToLowerInvariant();
            list.Add(new ContentFile(path2, sha, fileStream.Length));
        }
        if (list.Count == 0)
        {
            throw new ServiceException(ServiceError.InvalidInput, "This mod has no RomFS or ExeFS files.");
        }
        list.Sort((ContentFile a, ContentFile b) => string.CompareOrdinal(a.Path, b.Path));
        string text4 = Regex.Replace(Path.GetFileName(candidate).ToLowerInvariant(), "[^a-z0-9._-]+", "-").Trim(new char[3] { '-', '_', '.' });
        if (text4.Length > 40)
        {
            text4 = text4.Substring(0, 40).TrimEnd(new char[3] { '-', '_', '.' });
        }
        if (text4.Length == 0)
        {
            text4 = "mod";
        }
        string s = string.Join("\n", list.Select((ContentFile f) => f.Path + ":" + f.Sha256));
        string text5 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant().Substring(0, 10);
        GameBuildIdentity gameBuildIdentity = binding.Canonical();
        ModManifest value = new ModManifest(1, "imported-" + text4 + "-" + text5, "1", gameBuildIdentity.TitleId, gameBuildIdentity.BuildId, "nx-overlay-v1", list);
        await File.WriteAllTextAsync(Path.Combine(text, "manifest.json"), JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        }), ct);
        return relative;
        static bool IsModRoot(string directory)
        {
            if (!Directory.Exists(Path.Combine(directory, "romfs")))
            {
                return Directory.Exists(Path.Combine(directory, "exefs"));
            }
            return true;
        }
    }

    public static async Task<string> CopyProfileAsync(string source, NxWorkspace workspace, CancellationToken ct)
    {
        source = Path.GetFullPath(source);
        RejectLinks(source);
        if (new FileInfo(source).Length > 2097152)
        {
            throw new ServiceException(ServiceError.InvalidInput, "The profile manifest exceeds 2 MiB.");
        }
        string relative = "profile-" + Guid.NewGuid().ToString("N") + ".json";
        string path = Path.Combine(workspace.ImportsPath, relative);
        RejectLinks(path);
        string result;
        await using (FileStream input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true))
        {
            string text;
            await using (FileStream output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                byte[] buffer = new byte[65536];
                long total = 0L;
                int num;
                while ((num = await input.ReadAsync(buffer, ct)) != 0)
                {
                    total += num;
                    if (total > 2097152)
                    {
                        throw new ServiceException(ServiceError.InvalidInput, "The profile grew beyond its size limit.");
                    }
                    await output.WriteAsync(buffer.AsMemory(0, num), ct);
                }
                text = relative;
            }
            result = text;
        }
        return result;
    }

    public static async Task ExportSaveAsync(SaveBackup backup, string destination, CancellationToken ct)
    {
        destination = Path.GetFullPath(destination);
        RejectLinks(destination);
        bool created = false;
        try
        {
            await using FileStream file = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 131072, useAsync: true);
            created = true;
            using ZipArchive zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
            await using (Stream manifest = zip.CreateEntry("manifest.json").Open())
            {
                await JsonSerializer.SerializeAsync(manifest, backup.Manifest, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true
                }, ct);
            }
            foreach (string directory in backup.Manifest.Directories)
            {
                zip.CreateEntry("payload/" + directory.TrimEnd('/') + "/");
            }
            foreach (ContentFile item in backup.Manifest.Files)
            {
                ct.ThrowIfCancellationRequested();
                string fullPath = Path.GetFullPath(Path.Combine(backup.DirectoryPath, "payload", item.Path));
                RejectLinks(fullPath);
                await using FileStream input = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, useAsync: true);
                await using Stream manifest = zip.CreateEntry("payload/" + item.Path).Open();
                using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[131072];
                long total = 0L;
                int num;
                while ((num = await input.ReadAsync(buffer, ct)) != 0)
                {
                    total += num;
                    if (total > item.Size)
                    {
                        throw new ServiceException(ServiceError.CorruptData, "A backup changed while exporting.");
                    }
                    hash.AppendData(buffer, 0, num);
                    await manifest.WriteAsync(buffer.AsMemory(0, num), ct);
                }
                if (total != item.Size || !Convert.ToHexStringLower(hash.GetHashAndReset()).Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ServiceException(ServiceError.CorruptData, "A backup failed its export integrity check.");
                }
            }
        }
        catch
        {
            if (created && File.Exists(destination))
            {
                File.Delete(destination);
            }
            throw;
        }
    }
}
