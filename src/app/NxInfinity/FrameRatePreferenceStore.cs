using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace NxInfinity;

internal sealed class FrameRatePreferenceStore(string dataRoot)
{
    private string FilePath => Path.Combine(dataRoot, "preferences", "frame-rate.json");

    public FrameRatePreferences Read()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new FrameRatePreferences();
            }
            if (new FileInfo(FilePath).Length > 1048576)
            {
                return new FrameRatePreferences();
            }
            FrameRatePreferences frameRatePreferences = JsonSerializer.Deserialize<FrameRatePreferences>(File.ReadAllText(FilePath));
            return Valid(frameRatePreferences) ? frameRatePreferences : new FrameRatePreferences();
        }
        catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is JsonException) ? true : false)
        {
            return new FrameRatePreferences();
        }
    }

    public void Write(FrameRatePreferences value)
    {
        if (!Valid(value))
        {
            throw new ArgumentException("Invalid frame-rate preference.");
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

    private static bool Valid(FrameRatePreferences? value)
    {
        bool flag;
        if ((object)value != null && value.SchemaVersion == 1)
        {
            int globalTarget = value.GlobalTarget;
            if ((globalTarget == 0 || globalTarget == 60) && value.Games != null)
            {
                flag = true;
                goto IL_0029;
            }
        }
        flag = false;
        goto IL_0029;
    IL_0029:
        if (flag && value.Games.Count <= 10000)
        {
            return value.Games.All((KeyValuePair<string, int> item) =>
            {
                int length = item.Key.Length;
                bool flag2 = length > 0 && length <= 128 && item.Key.All((char c) =>
                {
                    bool flag4 = char.IsAsciiLetterOrDigit(c);
                    if (!flag4)
                    {
                        bool flag5 = ((c == '-' || c == '_') ? true : false);
                        flag4 = flag5;
                    }
                    return flag4;
                });
                if (flag2)
                {
                    length = item.Value;
                    bool flag3 = ((length == 0 || length == 60) ? true : false);
                    flag2 = flag3;
                }
                return flag2;
            });
        }
        return false;
    }
}
