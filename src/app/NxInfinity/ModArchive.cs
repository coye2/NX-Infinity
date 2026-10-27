using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NxInfinity.Runtime.Services;

namespace NxInfinity;

public static class ModArchive
{
    private static bool ReservedName(string part)
    {
        string text = part.Split('.')[0].ToUpperInvariant();
        bool flag;
        switch (text)
        {
            case "CON":
            case "PRN":
            case "AUX":
            case "NUL":
                flag = true;
                break;
            default:
                flag = false;
                break;
        }
        if (!flag)
        {
            if (text.Length == 4 && (text.StartsWith("COM") || text.StartsWith("LPT")))
            {
                char c = text[3];
                if (c >= '1')
                {
                    return c <= '9';
                }
                return false;
            }
            return false;
        }
        return true;
    }

    public static async Task<string> ExtractAsync(string archive, NxWorkspace workspace, CancellationToken ct)
    {
        if (!Path.GetExtension(archive).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("Choose a ZIP archive or an extracted RomFS/ExeFS mod folder.");
        }
        string stage = Path.Combine(workspace.ImportsPath, "mod-zip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            using ZipArchive zip = ZipFile.OpenRead(archive);
            if (zip.Entries.Count > workspace.Limits.MaximumFiles * 2)
            {
                throw new InvalidDataException("The archive contains too many entries.");
            }
            long total = 0L;
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                string text = entry.FullName.Replace('\\', '/');
                bool flag = text.EndsWith('/');
                string[] array = text.TrimEnd('/').Split('/');
                if (text.StartsWith('/') || array.Length > 64 || array.Any((string part) =>
                {
                    bool flag2 = (((part != null && part.Length == 0) || part == "." || part == "..") ? true : false);
                    return flag2 || part.Any((char c) => char.IsControl(c) || "<>:\"|?*".Contains(c)) || part.EndsWith(' ') || part.EndsWith('.') || ReservedName(part);
                }) || !names.Add(text.TrimEnd('/')))
                {
                    throw new InvalidDataException("The archive contains an unsafe or duplicate path.");
                }
                int num = (entry.ExternalAttributes >> 16) & 0xF000;
                if ((num != 0 && num != 32768 && num != 16384) || (entry.ExternalAttributes & 0x400) != 0)
                {
                    throw new InvalidDataException("Archive links and special files are not supported.");
                }
                string fullPath = Path.GetFullPath(Path.Combine(stage, Path.Combine(array)));
                if (!fullPath.StartsWith(stage + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The archive path leaves the import folder.");
                }
                if (flag)
                {
                    Directory.CreateDirectory(fullPath);
                    continue;
                }
                if (entry.Length > workspace.Limits.MaximumFileBytes || entry.Length > workspace.Limits.MaximumTotalBytes - total)
                {
                    throw new InvalidDataException("The archive exceeds the import size limit.");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                await using Stream input = entry.Open();
                await using FileStream output = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, useAsync: true);
                byte[] buffer = new byte[131072];
                long copied = 0L;
                int num2;
                while ((num2 = await input.ReadAsync(buffer, ct)) != 0)
                {
                    copied += num2;
                    total += num2;
                    if (copied > entry.Length || total > workspace.Limits.MaximumTotalBytes)
                    {
                        throw new InvalidDataException("The archive expanded beyond its declared size.");
                    }
                    await output.WriteAsync(buffer.AsMemory(0, num2), ct);
                }
                if (copied != entry.Length)
                {
                    throw new InvalidDataException("An archive entry is incomplete.");
                }
            }
            return stage;
        }
        catch
        {
            Directory.Delete(stage, recursive: true);
            throw;
        }
    }
}
