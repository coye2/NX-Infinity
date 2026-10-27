using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using NxInfinity.Runtime;

namespace NxInfinity;

public static class BuiltinTimingModule
{
    private sealed record Manifest(string TitleId, string BuildId, string DisplayVersion, Dictionary<string, string> Files);

    public const string MountId = "builtin-totk-dfps-100";

    public const string TitleId = "0100F2C0115B6000";

    public const string Version = "1.0.0";

    private const string Resource = "NxInfinity.TimingModules.totk-100.";

    public static bool Matches(string? title, string? build, string? version)
    {
        if (string.Equals(title, "0100F2C0115B6000", StringComparison.OrdinalIgnoreCase) && string.Equals(build, "082CE09B06E33A123CB1E2770F5F9147709033DB000000000000000000000000", StringComparison.OrdinalIgnoreCase))
        {
            return version == "1.0.0";
        }
        return false;
    }

    public static bool HasExecutableConflict(IEnumerable<NativeModMount> mounts)
    {
        return mounts.Any((NativeModMount m) => m.Enabled && m.Id != "builtin-totk-dfps-100" && (Directory.Exists(Path.Combine(m.SourcePath, "exefs")) || File.Exists(Path.Combine(m.SourcePath, "exefs.nsp"))));
    }

    public static string Prepare(string dataRoot)
    {
        using Stream utf8Json = Open("manifest.json");
        Manifest manifest = JsonSerializer.Deserialize<Manifest>(utf8Json) ?? throw new InvalidDataException("Timing module manifest is missing.");
        if (!Matches(manifest.TitleId, manifest.BuildId, manifest.DisplayVersion) || manifest.Files.Count != 2 || !manifest.Files.ContainsKey("subsdk9") || !manifest.Files.ContainsKey("main.npdm"))
        {
            throw new InvalidDataException("Timing module identity does not match its compiled catalog.");
        }
        InlineArray5<string> buffer = default;
        buffer[0] = dataRoot;
        buffer[1] = "enhancements";
        buffer[2] = "builtin";
        buffer[3] = "builtin-totk-dfps-100";
        buffer[4] = manifest.Files["subsdk9"];
        string text = Path.Combine(buffer);
        string exefs = Path.Combine(text, "exefs");
        for (DirectoryInfo directoryInfo = new DirectoryInfo(exefs); directoryInfo != null; directoryInfo = directoryInfo.Parent)
        {
            if (directoryInfo.LinkTarget != null)
            {
                throw new IOException("The timing module cache traverses a filesystem link.");
            }
        }
        Directory.CreateDirectory(exefs);
        if (Directory.EnumerateFileSystemEntries(text).Any((string p) => p != exefs) || Directory.EnumerateFileSystemEntries(exefs).Any((string p) => !manifest.Files.ContainsKey(Path.GetFileName(p))))
        {
            throw new IOException("Unexpected files in the built-in timing module cache. Move this cache folder aside and retry.");
        }
        foreach (var (text4, text5) in manifest.Files)
        {
            using StreamReader streamReader = new StreamReader(Open(text4 + ".b64"));
            byte[] array = Convert.FromBase64String(streamReader.ReadToEnd());
            if (Convert.ToHexString(SHA256.HashData(array)) != text5 || array.Length > 1048576 || !((ReadOnlySpan<byte>)array.AsSpan(0, 4)).SequenceEqual((text4 == "subsdk9") ? "NSO0"u8 : "META"u8))
            {
                throw new InvalidDataException("The bundled timing module failed verification.");
            }
            string text6 = Path.Combine(exefs, text4);
            if (new FileInfo(text6).LinkTarget != null)
            {
                throw new IOException("The timing module cache file is a filesystem link.");
            }
            if (File.Exists(text6) && new FileInfo(text6).Length == array.Length && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(text6))) == text5)
            {
                continue;
            }
            string text7 = text6 + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(text7, array);
                File.Move(text7, text6, overwrite: true);
            }
            finally
            {
                if (File.Exists(text7))
                {
                    File.Delete(text7);
                }
            }
        }
        return text;
    }

    private static Stream Open(string name)
    {
        return typeof(BuiltinTimingModule).Assembly.GetManifestResourceStream("NxInfinity.TimingModules.totk-100." + name) ?? throw new InvalidDataException("The built-in timing module was not included in this build.");
    }
}
