using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace NxInfinity;

public sealed class ThemePreferenceStore(string dataRoot)
{
    private readonly string _path = Path.Combine(dataRoot, "frontend-themes.json");

    public ThemePreferences Read(bool legacyNight = false)
    {
        try
        {
            if (File.Exists(_path) && new FileInfo(_path).Length <= 32768)
            {
                ThemePreferences themePreferences = JsonSerializer.Deserialize<ThemePreferences>(File.ReadAllText(_path));
                if ((object)themePreferences != null && Valid(themePreferences))
                {
                    return themePreferences;
                }
            }
        }
        catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is JsonException) ? true : false)
        {
        }
        return new ThemePreferences
        {
            Desktop = new ThemeChoice(legacyNight ? "night" : "dune")
        };
    }

    public void Write(ThemePreferences value)
    {
        if (!Valid(value))
        {
            throw new InvalidDataException("Invalid theme. Choose an available preset and a six-digit color.");
        }
        AvatarStore.WriteAtomic(_path, JsonSerializer.SerializeToUtf8Bytes(value));
    }

    private static bool Valid(ThemePreferences p)
    {
        if (p.Version == 1 && ThemePresets.ValidChoice(p.Desktop) && ThemePresets.ValidChoice(p.Home))
        {
            string[] favorites = p.Favorites;
            if (favorites != null && favorites.Length <= 64 && p.Favorites.All((string id) => ThemePresets.All.Any((ThemePreset t) => t.Id == id)))
            {
                SavedTheme[] saved = p.Saved;
                if (saved != null && saved.Length <= 16)
                {
                    return p.Saved.All((SavedTheme t) => (object)t != null && !string.IsNullOrWhiteSpace(t.Name) && t.Name.Length <= 32 && ThemePresets.ValidChoice(t.Choice));
                }
            }
        }
        return false;
    }
}
