using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace NxInfinity;

public static class DesktopShortcuts
{
    public static string WindowsArgument(string value)
    {
        Validate(value);
        StringBuilder stringBuilder = new StringBuilder("\"");
        int num = 0;
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\':
                    num++;
                    continue;
                case '"':
                    stringBuilder.Append('\\', num * 2 + 1).Append('"');
                    break;
                default:
                    stringBuilder.Append('\\', num).Append(c);
                    break;
            }
            num = 0;
        }
        return stringBuilder.Append('\\', num * 2).Append('"').ToString();
    }

    public static string DesktopArgument(string value)
    {
        Validate(value);
        return ("\"" + value.Replace("%", "%%").Replace("\\", "\\\\").Replace("\"", "\\\"")
            .Replace("`", "\\`")
            .Replace("$", "\\$") + "\"").Replace("\\", "\\\\");
    }

    private static void Validate(string value)
    {
        if (value.Any(char.IsControl))
        {
            throw new ArgumentException("Launcher paths and arguments must not contain control characters.");
        }
    }

    public static string DesktopEntry(string name, string executable, IEnumerable<string> arguments, string? icon = null)
    {
        Validate(executable);
        return "[Desktop Entry]\nType=Application\nVersion=1.0\nName=" + Escape(Label(name)) + "\nExec=" + string.Join(" ", new string[1] { executable }.Concat(arguments).Select(DesktopArgument)) + "\nPath=" + Escape(Path.GetDirectoryName(executable) ?? "/") + "\nTerminal=false\nCategories=Game;\n" + ((icon != null) ? ("Icon=" + Escape(Label(icon)) + "\n") : "");
        static string Escape(string value)
        {
            return value.Replace("\\", "\\\\");
        }
        static string Label(string value)
        {
            return new string(value.Where((char c) => !char.IsControl(c)).ToArray());
        }
    }

    public static string Create(string directory, string title, string entryId, string coreId, string executable, IReadOnlyList<string> arguments, string? icon = null)
    {
        if (!Guid.TryParseExact(entryId, "N", out var _) || coreId != "nxi")
        {
            throw new ArgumentException("A shortcut requires a known library entry and core.");
        }
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
        {
            throw new FileNotFoundException("Launcher executable is unavailable.", executable);
        }
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException("No desktop directory is available. Choose a shortcut folder.");
        }
        Validate(executable);
        foreach (string argument in arguments)
        {
            Validate(argument);
        }
        string text = Path.Combine(directory, "NX-" + entryId + "-" + coreId + (OperatingSystem.IsWindows() ? ".lnk" : ".desktop"));
        string text2 = text + "." + Guid.NewGuid().ToString("N") + (OperatingSystem.IsWindows() ? ".lnk" : ".tmp");
        try
        {
            if (OperatingSystem.IsWindows())
            {
                WriteWindows(text2, title, executable, arguments, icon);
            }
            else
            {
                if (!OperatingSystem.IsLinux())
                {
                    throw new PlatformNotSupportedException("Desktop shortcuts are supported on Windows and Linux.");
                }
                string text3 = DesktopEntry(title, executable, arguments, icon);
                File.WriteAllText(text2, text3, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                if (File.ReadAllText(text2) != text3)
                {
                    throw new IOException("Shortcut readback failed. The previous shortcut was preserved.");
                }
                File.SetUnixFileMode(text2, UnixFileMode.UserExecute | UnixFileMode.UserWrite | UnixFileMode.UserRead);
            }
            File.Move(text2, text, overwrite: true);
            return text;
        }
        finally
        {
            if (File.Exists(text2))
            {
                File.Delete(text2);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void WriteWindows(string path, string title, string executable, IReadOnlyList<string> arguments, string? icon)
    {
        dynamic val = null;
        object obj = null;
        object obj2 = null;
        try
        {
            val = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true));
            obj = val.CreateShortcut(path);
            dynamic val2 = obj;
            val2.TargetPath = executable;
            val2.Arguments = string.Join(" ", arguments.Select(WindowsArgument));
            val2.WorkingDirectory = Path.GetDirectoryName(executable);
            val2.Description = "NX Infinity · " + new string(title.Where((char c) => !char.IsControl(c)).Take(200).ToArray());
            val2.IconLocation = ((icon != null && File.Exists(icon)) ? icon : executable) + ",0";
            val2.Save();
            obj2 = val.CreateShortcut(path);
            dynamic val3 = obj2;
            if (!string.Equals((string)val3.TargetPath, executable, StringComparison.OrdinalIgnoreCase) || (string)val3.Arguments != string.Join(" ", arguments.Select(WindowsArgument)) || !string.Equals((string)val3.WorkingDirectory, Path.GetDirectoryName(executable), StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Windows shortcut readback failed. The previous shortcut was preserved.");
            }
        }
        finally
        {
            if (obj2 != null)
            {
                Marshal.FinalReleaseComObject(obj2);
            }
            if (obj != null)
            {
                Marshal.FinalReleaseComObject(obj);
            }
            if ((object)val != null)
            {
                Marshal.FinalReleaseComObject((object)val);
            }
        }
    }

    public static void OpenFolder(string path)
    {
        path = Path.GetFullPath(path);
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException("This folder is not available: " + path);
        }
        Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true
        });
    }
}
