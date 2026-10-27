using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace NxInfinity;

internal sealed class GpuPerformancePreferenceStore(string dataRoot)
{
    private string FilePath => Path.Combine(dataRoot, "preferences", "gpu-performance.json");

    public GpuPerformancePreferences Read()
    {
        try
        {
            if (!File.Exists(FilePath) || new FileInfo(FilePath).Length > 1048576)
            {
                return new GpuPerformancePreferences();
            }
            GpuPerformancePreferences gpuPerformancePreferences = JsonSerializer.Deserialize<GpuPerformancePreferences>(File.ReadAllText(FilePath));
            return Valid(gpuPerformancePreferences) ? gpuPerformancePreferences : new GpuPerformancePreferences();
        }
        catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is JsonException) ? true : false)
        {
            return new GpuPerformancePreferences();
        }
    }

    public void Write(GpuPerformancePreferences value)
    {
        if (!Valid(value))
        {
            throw new ArgumentException("Invalid GPU performance preference.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
        string text = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream fileStream = new FileStream(text, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(fileStream, value);
                fileStream.Flush(flushToDisk: true);
            }
            File.Move(text, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(text))
            {
                File.Delete(text);
            }
        }
    }

    private static bool Valid(GpuPerformancePreferences? value)
    {
        if ((object)value != null && value.SchemaVersion == 1 && value.Games != null && GpuPerformanceOptions.IsValid(value.Global) && value.Games.Count <= 10000)
        {
            return value.Games.All((KeyValuePair<string, GpuPerformanceOptions> pair) =>
            {
                string key = pair.Key;
                if (key != null)
                {
                    int length = key.Length;
                    if (length > 0 && length <= 128 && pair.Key.All((char c) =>
                    {
                        bool flag = char.IsAsciiLetterOrDigit(c);
                        if (!flag)
                        {
                            bool flag2 = ((c == '-' || c == '_') ? true : false);
                            flag = flag2;
                        }
                        return flag;
                    }))
                    {
                        return GpuPerformanceOptions.IsValid(pair.Value);
                    }
                }
                return false;
            });
        }
        return false;
    }
}
