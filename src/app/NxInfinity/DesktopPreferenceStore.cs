using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace NxInfinity;

public sealed class DesktopPreferenceStore(string dataRoot)
{
    private readonly string _path = Path.Combine(dataRoot, "frontend-desktop.json");

    public DesktopPreferences Read()
    {
        try
        {
            FileInfo fileInfo = new FileInfo(_path);
            if (!fileInfo.Exists || fileInfo.Length > 4096 || fileInfo.LinkTarget != null)
            {
                return new DesktopPreferences();
            }
            DesktopPreferences desktopPreferences = JsonSerializer.Deserialize<DesktopPreferences>(File.ReadAllText(_path));
            DesktopPreferences result;
            if ((object)desktopPreferences != null && desktopPreferences.SchemaVersion == 1)
            {
                double libraryFraction = desktopPreferences.LibraryFraction;
                if (libraryFraction >= 0.25 && libraryFraction <= 0.6 && ValidBrowsing(desktopPreferences))
                {
                    result = desktopPreferences;
                    goto IL_009e;
                }
            }
            result = new DesktopPreferences();
            goto IL_009e;
        IL_009e:
            return result;
        }
        catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is JsonException) ? true : false)
        {
            return new DesktopPreferences();
        }
    }

    public void Write(DesktopPreferences value)
    {
        bool flag = value.SchemaVersion != 1 || !double.IsFinite(value.LibraryFraction);
        if (!flag)
        {
            double libraryFraction = value.LibraryFraction;
            bool flag2 = ((libraryFraction < 0.25 || libraryFraction > 0.6) ? true : false);
            flag = flag2;
        }
        if (flag || !ValidBrowsing(value))
        {
            throw new ArgumentException("Invalid desktop preference.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(_path));
        if (new FileInfo(_path).LinkTarget != null)
        {
            throw new IOException("Desktop preferences must be a regular local file.");
        }
        string text = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(text, JsonSerializer.Serialize(value));
            File.Move(text, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(text))
            {
                File.Delete(text);
            }
        }
    }

    private static bool ValidBrowsing(DesktopPreferences value)
    {
        if (Enum.IsDefined(value.Sort))
        {
            if (value.SelectedEntryId != null)
            {
                if (value.SelectedEntryId.Length == 32)
                {
                    return value.SelectedEntryId.All(Uri.IsHexDigit);
                }
                return false;
            }
            return true;
        }
        return false;
    }
}
