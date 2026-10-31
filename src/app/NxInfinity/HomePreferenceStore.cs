using System;
using System.IO;
using System.Text.Json;

namespace NxInfinity;

internal sealed class HomePreferenceStore(string dataRoot)
{
    private string FilePath => Path.Combine(dataRoot, "frontend-home", "home.json");

    public HomePreferences Read()
    {
        try
        {
            if (!File.Exists(FilePath) || new FileInfo(FilePath).Length > 8192)
            {
                return new HomePreferences();
            }
            HomePreferences homePreferences = JsonSerializer.Deserialize<HomePreferences>(File.ReadAllText(FilePath));
            bool flag = (object)homePreferences == null || homePreferences.SchemaVersion != 1;
            if (!flag)
            {
                bool flag2;
                switch (homePreferences.Filter)
                {
                    case "all":
                    case "favorites":
                    case "recent":
                        flag2 = true;
                        break;
                    default:
                        flag2 = false;
                        break;
                }
                flag = !flag2;
            }
            int num;
            if (!flag)
            {
                string selectedEntryId = homePreferences.SelectedEntryId;
                if (selectedEntryId == null || selectedEntryId.Length <= 128)
                {
                    num = ((!double.IsFinite(homePreferences.SoundVolume)) ? 1 : 0);
                    goto IL_00c6;
                }
            }
            num = 1;
            goto IL_00c6;
        IL_00c6:
            bool flag3 = (byte)num != 0;
            if (!flag3)
            {
                double soundVolume = homePreferences.SoundVolume;
                bool flag2 = ((soundVolume < 0.0 || soundVolume > 1.0) ? true : false);
                flag3 = flag2;
            }
            if (flag3)
            {
                return new HomePreferences();
            }
            return homePreferences with
            {
                Language = UiText.NormalizeLanguage(homePreferences.Language)
            };
        }
        catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is JsonException) ? true : false)
        {
            return new HomePreferences();
        }
    }

    public bool Write(HomePreferences value)
    {
        string text = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(text, JsonSerializer.Serialize(value));
            File.Move(text, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
        {
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(text))
                {
                    File.Delete(text);
                }
            }
            catch (Exception ex2) when ((ex2 is IOException || ex2 is UnauthorizedAccessException) ? true : false)
            {
            }
        }
    }
}
