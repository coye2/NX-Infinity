using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;

namespace NxInfinity;

internal static class GameTextCapture
{
    private delegate bool EnumWindowsCallback(nint handle, nint data);

    private struct Rect
    {
        public int Left;

        public int Top;

        public int Right;

        public int Bottom;
    }

    private struct BitmapInfo
    {
        public uint Size;

        public int Width;

        public int Height;

        public ushort Planes;

        public ushort BitCount;

        public uint Compression;

        public uint SizeImage;

        public int XPels;

        public int YPels;

        public uint Used;

        public uint Important;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint data);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint process);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint dc, nint obj);

    [DllImport("gdi32.dll")]
    private static extern bool StretchBlt(nint dest, int x, int y, int width, int height, nint source, int sx, int sy, int sourceWidth, int sourceHeight, uint operation);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(nint dc, int mode);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, nint bits, ref BitmapInfo info, uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint dc);

    public static byte[] Capture(int processId, bool fullFrame)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Game-window capture uses the Windows renderer, including the Windows build under Proton.");
        }
        nint window = 0;
        long area = 0L;
        EnumWindows((nint handle, nint _) =>
        {
            GetWindowThreadProcessId(handle, out var process);
            if (process == processId && IsWindowVisible(handle) && !IsIconic(handle) && GetClientRect(handle, out var rect2))
            {
                long num6 = (long)(rect2.Right - rect2.Left) * (long)(rect2.Bottom - rect2.Top);
                if (num6 > area)
                {
                    area = num6;
                    window = handle;
                }
            }
            return true;
        }, 0);
        if (window == 0 || !GetClientRect(window, out var rect))
        {
            throw new IOException("The game window is unavailable. Restore the game window and retry.");
        }
        int right = rect.Right;
        int bottom = rect.Bottom;
        int num = ((!fullFrame) ? ((int)((double)bottom * 0.55)) : 0);
        bottom -= num;
        bool flag = ((right < 64 || right > 8192) ? true : false);
        bool flag2 = flag;
        if (!flag2)
        {
            bool flag3 = ((bottom < 32 || bottom > 4320) ? true : false);
            flag2 = flag3;
        }
        if (flag2 || (long)right * (long)bottom > 24000000)
        {
            throw new IOException("The game capture size is unsupported.");
        }
        int sourceWidth = right;
        int sourceHeight = bottom;
        double num2 = Math.Min(1.0, Math.Min(1920.0 / (double)right, 1080.0 / (double)bottom));
        right = Math.Max(1, (int)((double)right * num2));
        bottom = Math.Max(1, (int)((double)bottom * num2));
        nint dC = GetDC(window);
        nint num3 = 0;
        nint num4 = 0;
        nint num5 = 0;
        try
        {
            if (dC == 0)
            {
                throw new IOException("The game window cannot be captured.");
            }
            num3 = CreateCompatibleDC(dC);
            num4 = CreateCompatibleBitmap(dC, right, bottom);
            if (num3 == 0 || num4 == 0)
            {
                throw new IOException("Game capture allocation failed.");
            }
            num5 = SelectObject(num3, num4);
            SetStretchBltMode(num3, 4);
            if (!StretchBlt(num3, 0, 0, right, bottom, dC, 0, num, sourceWidth, sourceHeight, 13369376u))
            {
                throw new IOException("Capture failed. Try the game's windowed or borderless display mode.");
            }
            SelectObject(num3, num5);
            num5 = 0;
            using SKBitmap sKBitmap = new SKBitmap(right, bottom, SKColorType.Bgra8888, SKAlphaType.Opaque);
            BitmapInfo info = new BitmapInfo
            {
                Size = (uint)Marshal.SizeOf<BitmapInfo>(),
                Width = right,
                Height = -bottom,
                Planes = 1,
                BitCount = 32
            };
            if (GetDIBits(dC, num4, 0u, (uint)bottom, sKBitmap.GetPixels(), ref info, 0u) != bottom)
            {
                throw new IOException("Game capture returned an incomplete frame.");
            }
            using SKImage sKImage = SKImage.FromBitmap(sKBitmap);
            using SKData sKData = sKImage.Encode(SKEncodedImageFormat.Png, 90);
            return sKData.ToArray();
        }
        finally
        {
            if (num5 != 0)
            {
                SelectObject(num3, num5);
            }
            if (num4 != 0)
            {
                DeleteObject(num4);
            }
            if (num3 != 0)
            {
                DeleteDC(num3);
            }
            if (dC != 0)
            {
                ReleaseDC(window, dC);
            }
        }
    }

    public static async Task<string> RecognizeAsync(string runtime, string languageData, byte[] png, string language, CancellationToken ct)
    {
        string code = MainWindow.OcrLanguages.GetValueOrDefault(language) ?? throw new InvalidOperationException("This game OCR language is not supported.");
        string executable = (OperatingSystem.IsWindows() ? Path.Combine(runtime, "ocr", "tesseract.exe") : "tesseract");
        string temp = Path.Combine(Path.GetTempPath(), "nx-ocr-" + Guid.NewGuid().ToString("N") + ".png");
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(12L));
        Process process = null;
        try
        {
            await File.WriteAllBytesAsync(temp, png, deadline.Token);
            ProcessStartInfo processStartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            string[] array = new string[6] { temp, "stdout", "-l", code, "--psm", "11" };
            foreach (string item in array)
            {
                processStartInfo.ArgumentList.Add(item);
            }
            if (OperatingSystem.IsWindows())
            {
                processStartInfo.ArgumentList.Add("--tessdata-dir");
                processStartInfo.ArgumentList.Add(languageData);
            }
            processStartInfo.Environment["OMP_THREAD_LIMIT"] = "2";
            process = Process.Start(processStartInfo) ?? throw new IOException("OCR could not start.");
            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception ex) when ((ex is InvalidOperationException || ex is Win32Exception) ? true : false)
            {
            }
            Task<string> output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            Task<string> errors = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            string text = await output;
            string text2 = await errors;
            if (process.ExitCode != 0)
            {
                throw new IOException("OCR could not read this language. Install its OCR language pack in Translation settings. " + text2.Substring(0, Math.Min(text2.Length, 180)));
            }
            return text.Trim().Substring(0, Math.Min(text.Trim().Length, 4000));
        }
        finally
        {
            if (process != null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException)
                {
                }
                process.Dispose();
            }
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
            }
        }
    }
}
