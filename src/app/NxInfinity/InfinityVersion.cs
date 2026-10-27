using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace NxInfinity;

public static class InfinityVersion
{
    private static readonly Lazy<string?> ExecutableDigest;

    public static string Application { get; }

    public static Task<string?> ExecutableSha256Async()
    {
        return Task.Run(() => ExecutableDigest.Value);
    }

    static InfinityVersion()
    {
        AssemblyInformationalVersionAttribute? customAttribute = typeof(InfinityVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        Application = ((customAttribute != null) ? customAttribute.InformationalVersion.Split('+')[0] : null) ?? "unknown";
        ExecutableDigest = new Lazy<string>(() =>
        {
            try
            {
                string processPath = Environment.ProcessPath;
                if (processPath != null)
                {
                    using FileStream source = File.OpenRead(processPath);
                    return Convert.ToHexStringLower(SHA256.HashData(source));
                }
                return (string?)null;
            }
            catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
            {
                return (string?)null;
            }
        });
    }
}
