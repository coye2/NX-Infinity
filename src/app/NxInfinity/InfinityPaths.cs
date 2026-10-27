using System;
using System.IO;

namespace NxInfinity;

public sealed record InfinityPaths(string InstallRoot, string DataRoot, string? DotnetPath, string? ValidationContent = null)
{
    public static InfinityPaths FromArguments(string[] args)
    {
        string text = Value("--install-root") ?? AppContext.BaseDirectory;
        if (Value("--install-root") == null)
        {
            DirectoryInfo directoryInfo = new DirectoryInfo(text);
            int num = 0;
            while (directoryInfo != null && num < 8)
            {
                if (File.Exists(Path.Combine(directoryInfo.FullName, "NxInfinity.slnx")))
                {
                    text = directoryInfo.FullName;
                    break;
                }
                directoryInfo = directoryInfo.Parent;
                num++;
            }
        }
        string path = Value("--data-root") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NX Infinity");
        string text2 = Value("--dotnet");
        if (text2 == null)
        {
            string environmentVariable = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (environmentVariable != null)
            {
                string text3 = Path.Combine(environmentVariable, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
                if (File.Exists(text3))
                {
                    text2 = text3;
                }
            }
        }
        if (text2 == null)
        {
            string text4 = Path.Combine(text, ".tools", "dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(text4))
            {
                text2 = text4;
            }
        }
        return new InfinityPaths(Path.GetFullPath(text), Path.GetFullPath(path), (text2 == null) ? null : Path.GetFullPath(text2), Value("--validate-game"));
        string? Value(string flag)
        {
            int num2 = Array.IndexOf(args, flag);
            if (num2 < 0 || num2 + 1 >= args.Length)
            {
                return null;
            }
            return args[num2 + 1];
        }
    }
}
