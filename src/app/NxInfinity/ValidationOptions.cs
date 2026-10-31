using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;

namespace NxInfinity;

public sealed record ValidationOptions(string Backend, string DataRoot, string ReportPath, string? ContentPath, bool Probe, int Seconds, string? InstallRoot)
{
    public static ValidationOptions Parse(string[] args)
    {
        Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
        HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
        bool flag;
        for (int i = 0; i < args.Length; i++)
        {
            string text = args[i];
            if ((text == "--validate" || text == "--probe") ? true : false)
            {
                if (!hashSet.Add(text))
                {
                    throw new ArgumentException("Duplicate validation flag.");
                }
                continue;
            }
            switch (text)
            {
                case "--backend":
                case "--data-root":
                case "--report":
                case "--content":
                case "--seconds":
                case "--install-root":
                    flag = true;
                    break;
                default:
                    flag = false;
                    break;
            }
            if (flag)
            {
                if (++i == args.Length || args[i].StartsWith("--", StringComparison.Ordinal) || !values.TryAdd(text, args[i]))
                {
                    throw new ArgumentException("Missing or duplicate validation argument.");
                }
                continue;
            }
            throw new ArgumentException("Unknown validation argument.");
        }
        if (!hashSet.Contains("--validate"))
        {
            throw new ArgumentException("Explicit validation mode is required.");
        }
        string text2 = Required("--backend");
        if (text2 != "nxi")
        {
            throw new ArgumentException("Select NX Infinity Core (nxi).");
        }
        bool flag2 = hashSet.Contains("--probe");
        string text3 = (values.TryGetValue("--content", out string value) ? Absolute(value) : null);
        if (flag2 == (text3 != null))
        {
            throw new ArgumentException("Select exactly one probe or explicit content file.");
        }
        int result = 60;
        flag = values.TryGetValue("--seconds", out string value2);
        if (flag)
        {
            bool flag3 = !int.TryParse(value2, NumberStyles.None, CultureInfo.InvariantCulture, out result);
            if (!flag3)
            {
                bool flag4 = ((result < 1 || result > 3600) ? true : false);
                flag3 = flag4;
            }
            flag = flag3;
        }
        if (flag)
        {
            throw new ArgumentException("Validation duration must be 1 through 3600 seconds.");
        }
        string value3;
        return new ValidationOptions(text2, Absolute(Required("--data-root")), Absolute(Required("--report")), text3, flag2, result, values.TryGetValue("--install-root", out value3) ? Absolute(value3) : null);
        static string Absolute(string path)
        {
            if (Path.IsPathFullyQualified(path))
            {
                InlineArray2<char> buffer = default;
                buffer[0] = Path.DirectorySeparatorChar;
                buffer[1] = Path.AltDirectorySeparatorChar;
                if (!path.Split(buffer).Contains(".."))
                {
                    return Path.GetFullPath(path);
                }
            }
            throw new ArgumentException("Validation paths must be explicit absolute paths without traversal.");
        }
        string Required(string key)
        {
            if (!values.TryGetValue(key, out string value4) || string.IsNullOrWhiteSpace(value4))
            {
                throw new ArgumentException("A required validation argument is missing.");
            }
            return value4;
        }
    }
}
