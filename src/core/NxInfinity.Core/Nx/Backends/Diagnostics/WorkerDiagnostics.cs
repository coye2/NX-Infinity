using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Nx.Backends.Diagnostics;

internal sealed class WorkerDiagnostics(TextWriter writer, string backend) : IDisposable
{
    private readonly object _gate = new object();

    private long _sequence;

    private bool _closed;

    public void Trace(string component, string phase, string state, string? detail = null)
    {
        Write(component, phase, state, detail, null);
    }

    public void Exception(string component, string phase, string state, Exception error)
    {
        Write(component, phase, state, null, error);
    }

    public void CoreLog(string level, string message, string? threadName, object? data = null, TimeSpan? originTime = null)
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }
            try
            {
                bool flag = level == "Error";
                if (!flag)
                {
                    bool flag2 = ((data is Exception || data is StackTrace) ? true : false);
                    flag = flag2;
                }
                if (flag)
                {
                    Header("core", "log", "unknown", $"originThread={ThreadToken(threadName)} originTicks={(originTime.HasValue ? originTime.Value.Ticks.ToString(CultureInfo.InvariantCulture) : "unknown")} originFrequency={10000000L}");
                }
                writer.WriteLine("[" + level + "] " + message);
                string text;
                if (data is Exception ex)
                {
                    text = ex.ToString();
                }
                else
                {
                    text = ((!(data is StackTrace stackTrace)) ? null : stackTrace.ToString());
                }
                string text2 = text;
                if (text2 != null)
                {
                    TextWriter textWriter = writer;
                    string value;
                    if (text2.Length > 12000)
                    {
                        value = text2.Substring(0, 6000) + "\n[NX stackTruncated=true: middle omitted]\n" + text2.Substring(text2.Length - 6000);
                    }
                    else
                    {
                        value = text2;
                    }
                    textWriter.WriteLine(value);
                }
                writer.Flush();
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private void Write(string component, string phase, string state, string? detail, Exception? error)
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }
            try
            {
                Header(component, phase, state, detail);
                if (error != null)
                {
                    writer.WriteLine(error.ToString());
                }
                writer.Flush();
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private static string ThreadToken(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "unknown";
        }
        char[] array = name.Substring(0, Math.Min(name.Length, 128)).ToCharArray();
        for (int i = 0; i < array.Length; i++)
        {
            bool flag = !char.IsAsciiLetterOrDigit(array[i]);
            if (flag)
            {
                char c = array[i];
                bool flag2 = ((c == '-' || c == '.' || c == '_') ? true : false);
                flag = !flag2;
            }
            if (flag)
            {
                array[i] = '_';
            }
        }
        return new string(array);
    }

    private void Header(string component, string phase, string state, string? detail)
    {
        writer.WriteLine($"NX.lifecycle seq={++_sequence} utc={DateTimeOffset.UtcNow:O} ticks={Stopwatch.GetTimestamp()} frequency={Stopwatch.Frequency} tid={Environment.CurrentManagedThreadId} backend={backend} component={component} phase={phase} state={state}{((detail == null) ? string.Empty : (" " + detail))}");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _closed = true;
        }
    }
}
