using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Logging;

namespace NxInfinity;

public static class Program
{
    public static string[] Arguments { get; private set; } = Array.Empty<string>();

    public static int ExitStatus { get; set; }

    [STAThread]
    public static int Main(string[] args)
    {
        Arguments = args;
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return ExitStatus;
        }
        catch (Exception ex)
        {
            try
            {
                string text = Path.Combine(InfinityPaths.FromArguments(args).DataRoot, "diagnostics");
                Directory.CreateDirectory(text);
                File.WriteAllText(Path.Combine(text, "startup-error.txt"), $"NX Infinity {InfinityVersion.Application}\nStartup failed. This private local log is not included in diagnostic exports.\n{ex}\n");
                if (OperatingSystem.IsWindows())
                {
                    MessageBox(0, "NX Infinity could not start.\n\n" + ex.Message + "\n\nA local error log was saved to:\n" + Path.Combine(text, "startup-error.txt"), "NX Infinity · Startup error", 16u);
                }
            }
            catch
            {
            }
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<InfinityApp>().UsePlatformDetect().WithInterFont()
            .LogToTrace(LogEventLevel.Warning);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(nint owner, string text, string caption, uint type);
}
