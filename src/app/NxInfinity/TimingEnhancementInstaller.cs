using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NxInfinity.Runtime;
using NxInfinity.Runtime.Enhancements;

namespace NxInfinity;

public static class TimingEnhancementInstaller
{
    private static readonly HttpClient Http = new HttpClient(new HttpClientHandler
    {
        AllowAutoRedirect = false
    })
    {
        Timeout = TimeSpan.FromSeconds(30L)
    };

    private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

    public static bool HasExecutableConflict(IEnumerable<NativeModMount> mounts)
    {
        return mounts.Any((NativeModMount m) => m.Enabled && !TimingEnhancementCatalog.IsOwnedId(m.Id) && (Directory.Exists(Path.Combine(m.SourcePath, "exefs")) || File.Exists(Path.Combine(m.SourcePath, "exefs.nsp"))));
    }

    public static async Task<string> PrepareAsync(TimingEnhancementRecipe recipe, string dataRoot, CancellationToken ct)
    {
        if (!TimingEnhancementCatalog.Entries.Any((TimingEnhancementRecipe r) => (object)r == recipe))
        {
            throw new InvalidDataException("Timing recipe is not from the bundled catalog.");
        }
        await Gate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (recipe.Embedded)
            {
                return await Task.Run(() => BuiltinTimingModule.Prepare(dataRoot), ct);
            }
            string text = CacheRoot(recipe, dataRoot);
            if (CacheValid(recipe, text))
            {
                return text;
            }
            Uri uri = new Uri(recipe.ArchiveUrl);
            if (uri.Scheme != "https" || uri.Host != "raw.githubusercontent.com" || !uri.AbsolutePath.StartsWith("/Fl4sh9174/Switch-Emulator-Ultrawide-FPS-Mods/", StringComparison.Ordinal) || uri.Segments.Length < 5 || uri.Segments[3].Trim('/').Length != 40)
            {
                throw new InvalidDataException("Timing patch URL is not a pinned author revision.");
            }
            using HttpResponseMessage response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            long? contentLength = response.Content.Headers.ContentLength;
            if (contentLength.HasValue)
            {
                long valueOrDefault = contentLength.GetValueOrDefault();
                if (valueOrDefault != recipe.ArchiveBytes)
                {
                    throw new InvalidDataException("The author archive size changed. No patch was installed.");
                }
            }
            string result;
            await using (Stream stream = await response.Content.ReadAsStreamAsync(ct))
            {
                MemoryStream buffer = new MemoryStream();
                try
                {
                    byte[] chunk = new byte[16384];
                    int num;
                    while ((num = await stream.ReadAsync(chunk, ct)) != 0)
                    {
                        if (buffer.Length + num > recipe.ArchiveBytes || buffer.Length + num > 2097152)
                        {
                            throw new InvalidDataException("Timing archive exceeds its pinned size.");
                        }
                        buffer.Write(chunk, 0, num);
                    }
                    result = await Task.Run(() => InstallArchive(recipe, buffer.ToArray(), dataRoot, ct), ct);
                }
                finally
                {
                    if (buffer != null)
                    {
                        ((IDisposable)buffer).Dispose();
                    }
                }
            }
            return result;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static string InstallArchive(TimingEnhancementRecipe recipe, byte[] archive, string dataRoot, CancellationToken ct = default(CancellationToken))
    {
        bool flag = recipe.Embedded || archive.Length != recipe.ArchiveBytes || archive.Length > 2097152 || !string.Equals(Hash(archive), recipe.ArchiveSha256, StringComparison.Ordinal);
        if (!flag)
        {
            int count = recipe.Files.Count;
            bool flag2 = ((count < 1 || count > 8) ? true : false);
            flag = flag2;
        }
        if (flag)
        {
            throw new InvalidDataException("Timing archive failed verification. No patch was installed.");
        }
        Dictionary<string, byte[]> dictionary = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        using ZipArchive zipArchive = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);
        if (zipArchive.Entries.Count > 512)
        {
            throw new InvalidDataException("Timing archive contains too many entries.");
        }
        HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in zipArchive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            string fullName = entry.FullName;
            if (fullName.StartsWith('/') || fullName.Contains('\\') || fullName.Contains(':') || fullName.Contains('\0') || fullName.Split('/').Any((string part) => (part == ".." || part == ".") ? true : false) || !hashSet.Add(fullName) || ((entry.ExternalAttributes >> 16) & 0xF000) == 40960)
            {
                throw new InvalidDataException("Timing archive contains an unsafe or duplicate path.");
            }
        }
        foreach (TimingPatchFile file in recipe.Files)
        {
            int count = file.Bytes;
            flag = ((count < 1 || count > 262144) ? true : false);
            if (flag || Path.GetFileName(file.OutputName) != file.OutputName || !file.OutputName.EndsWith(".pchtxt", StringComparison.Ordinal) || file.OutputName.Contains('\\'))
            {
                throw new InvalidDataException("Timing recipe contains an invalid output.");
            }
            ZipArchiveEntry? zipArchiveEntry = zipArchive.GetEntry(file.ArchivePath) ?? throw new InvalidDataException("Required timing patch or companion file is missing.");
            if (zipArchiveEntry.Length != file.Bytes)
            {
                throw new InvalidDataException("Timing patch size changed.");
            }
            using Stream stream = zipArchiveEntry.Open();
            byte[] array = new byte[file.Bytes];
            stream.ReadExactly(array);
            if (stream.ReadByte() != -1 || Hash(array) != file.Sha256 || (!Encoding.UTF8.GetString(array).TrimStart(new char[4] { '\ufeff', ' ', '\r', '\n' }).StartsWith("@nsobid-" + file.NsoBuildId + "\r", StringComparison.Ordinal) && !Encoding.UTF8.GetString(array).TrimStart(new char[4] { '\ufeff', ' ', '\r', '\n' }).StartsWith("@nsobid-" + file.NsoBuildId + "\n", StringComparison.Ordinal)))
            {
                throw new InvalidDataException("Timing patch hash or NSO identity changed.");
            }
            dictionary.Add(file.OutputName, array);
        }
        string text = CacheRoot(recipe, dataRoot);
        string text2 = Path.Combine(text, "exefs");
        RejectLinks(text2);
        Directory.CreateDirectory(text2);
        RequireExpectedPaths(recipe, text);
        foreach (var (path, bytes) in dictionary)
        {
            ct.ThrowIfCancellationRequested();
            string text4 = Path.Combine(text2, path);
            string text5 = text4 + "." + Guid.NewGuid().ToString("N") + ".tmp";
            RejectLinks(text4);
            try
            {
                File.WriteAllBytes(text5, bytes);
                ct.ThrowIfCancellationRequested();
                File.Move(text5, text4, overwrite: true);
            }
            finally
            {
                if (File.Exists(text5))
                {
                    File.Delete(text5);
                }
            }
        }
        if (!CacheValid(recipe, text))
        {
            throw new IOException("Timing patch cache readback failed.");
        }
        return text;
    }

    private static string CacheRoot(TimingEnhancementRecipe recipe, string dataRoot)
    {
        int length = recipe.Id.Length;
        bool flag = ((length < 1 || length > 64) ? true : false);
        if (!flag && recipe.Id.All((char c) => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            string archiveSha = recipe.ArchiveSha256;
            if (archiveSha != null && archiveSha.Length == 64 && recipe.ArchiveSha256.All(Uri.IsHexDigit))
            {
                InlineArray5<string> buffer = default;
                buffer[0] = dataRoot;
                buffer[1] = "enhancements";
                buffer[2] = "builtin";
                buffer[3] = recipe.Id;
                buffer[4] = recipe.ArchiveSha256;
                return Path.Combine(buffer);
            }
        }
        throw new InvalidDataException("Invalid timing cache identity.");
    }

    private static bool CacheValid(TimingEnhancementRecipe recipe, string root)
    {
        RejectLinks(root);
        if (!Directory.Exists(root))
        {
            return false;
        }
        RequireExpectedPaths(recipe, root);
        return recipe.Files.All((TimingPatchFile f) =>
        {
            string text = Path.Combine(root, "exefs", f.OutputName);
            RejectLinks(text);
            return File.Exists(text) && new FileInfo(text).Length == f.Bytes && Hash(File.ReadAllBytes(text)) == f.Sha256;
        });
    }

    private static void RequireExpectedPaths(TimingEnhancementRecipe recipe, string root)
    {
        string exefs = Path.Combine(root, "exefs");
        RejectLinks(exefs);
        if (Directory.EnumerateFileSystemEntries(root).Any((string p) => p != exefs) || (Directory.Exists(exefs) && Directory.EnumerateFileSystemEntries(exefs).Any((string p) => !recipe.Files.Any((TimingPatchFile f) => f.OutputName == Path.GetFileName(p)))))
        {
            throw new IOException("Unexpected files in the timing cache. Move this cache folder aside and retry.");
        }
    }

    private static void RejectLinks(string path)
    {
        if (new FileInfo(path).LinkTarget != null)
        {
            throw new IOException("Timing cache cannot use filesystem links.");
        }
        for (DirectoryInfo directoryInfo = new DirectoryInfo(path); directoryInfo != null; directoryInfo = directoryInfo.Parent)
        {
            if (directoryInfo.LinkTarget != null)
            {
                throw new IOException("Timing cache cannot traverse filesystem links.");
            }
        }
    }

    private static string Hash(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
