using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NxInfinity.Runtime;

namespace NxInfinity;

public sealed record BackendChoice(string Id, string Name, string Revision, string Qualification, string ManifestPath, bool Managed)
{
    public string DisplayName => "NX Infinity Core";

    public override string ToString()
    {
        return DisplayName + ((Qualification == "qualified") ? "" : " · development");
    }

    public static IReadOnlyList<BackendChoice> Discover(InfinityPaths paths)
    {
        string text = ArtifactRuntimeIdentifier();
        List<BackendChoice> list = new List<BackendChoice>();
        string[] array = new string[1] { "nxi" };
        foreach (string text2 in array)
        {
            string text3 = new string[3]
            {
                Path.Combine(paths.InstallRoot, "cores", text2, "backend.json"),
                Path.Combine(paths.InstallRoot, "artifacts", "nxi-" + text + "-product", "backend.json"),
                Path.Combine(paths.InstallRoot, "artifacts", "nxi-" + text, "backend.json")
            }.FirstOrDefault(File.Exists);
            if (text3 == null)
            {
                continue;
            }
            try
            {
                if (new FileInfo(text3).Length <= 4194304)
                {
                    BackendInstallation backendInstallation = JsonSerializer.Deserialize<BackendInstallation>(File.ReadAllText(text3), new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                    if ((object)backendInstallation != null && !(backendInstallation.BackendId != text2) && backendInstallation.Revision.Length == 40)
                    {
                        list.Add(new BackendChoice(text2, backendInstallation.BackendName, backendInstallation.Revision, backendInstallation.Qualification, text3, backendInstallation.ManagedAssembly));
                    }
                }
            }
            catch (Exception ex) when ((ex is IOException || ex is JsonException || ex is UnauthorizedAccessException) ? true : false)
            {
            }
        }
        return list;
    }

    internal static string ArtifactRuntimeIdentifier()
    {
        string platform = OperatingSystem.IsWindows() ? "win" :
            OperatingSystem.IsLinux() ? "linux" :
            throw new PlatformNotSupportedException("NX Infinity desktop builds support Windows and Linux.");
        string architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException("NX Infinity desktop builds support x64 and ARM64.")
        };
        return platform + "-" + architecture;
    }

    public async Task<BackendLaunch> VerifyAsync(InfinityPaths paths, CancellationToken ct = default(CancellationToken))
    {
        BackendLaunch backendLaunch = await BackendInstallation.LoadAsync(ManifestPath, paths.DotnetPath, allowUnqualified: true, ct);
        if (backendLaunch.BackendId != Id || !backendLaunch.Revision.Equals(Revision, StringComparison.OrdinalIgnoreCase))
        {
            throw new NxException("version_mismatch", "The installed core changed after discovery. Restart NX Infinity and review the new revision before using it.");
        }
        return backendLaunch with
        {
            CommandTimeout = TimeSpan.FromSeconds(30L),
            StartupTimeout = TimeSpan.FromSeconds(30L),
            ContentTimeout = TimeSpan.FromMinutes(15L),
            InitializationTimeout = TimeSpan.FromMinutes(2L)
        };
    }
}
