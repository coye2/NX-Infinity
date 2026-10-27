using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace NxInfinity;

public sealed class ControllerMappingStore
{
    private readonly string _path;

    private readonly ControllerMapping _default = ControllerMapping.Default();

    private readonly Dictionary<string, ControllerMapping> _profiles;

    public ControllerMappingStore(string dataRoot)
    {
        _path = Path.Combine(dataRoot, "controller-mappings.json");
        if (File.Exists(_path) && new FileInfo(_path).Length > 2097152)
        {
            throw new InvalidDataException("Controller profiles exceed the size limit.");
        }
        _profiles = (File.Exists(_path) ? (JsonSerializer.Deserialize<Dictionary<string, ControllerMapping>>(File.ReadAllText(_path)) ?? throw new InvalidDataException("Controller profiles are unreadable.")) : new Dictionary<string, ControllerMapping>(StringComparer.Ordinal));
        if (_profiles.Count > 1000)
        {
            throw new InvalidDataException("Too many controller profiles.");
        }
        foreach (ControllerMapping value in _profiles.Values)
        {
            value.Validate();
        }
    }

    private static string Key(string core, string? game, string device)
    {
        return core + ":" + (game ?? "default") + ":" + device;
    }

    public ControllerMapping Read(string core, string? game, string device)
    {
        return _profiles.GetValueOrDefault(Key(core, game, device)) ?? _profiles.GetValueOrDefault(Key(core, null, device)) ?? _default;
    }

    public void Save(string core, string? game, string device, ControllerMapping profile)
    {
        profile.Validate();
        string text = Key(core, game, device);
        if (!_profiles.ContainsKey(text) && _profiles.Count >= 1000)
        {
            throw new InvalidDataException("The controller profile limit was reached.");
        }
        Dictionary<string, ControllerMapping> value = new Dictionary<string, ControllerMapping>(_profiles, StringComparer.Ordinal) { [text] = profile };
        Directory.CreateDirectory(Path.GetDirectoryName(_path));
        string text2 = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(text2, JsonSerializer.Serialize(value));
            File.Move(text2, _path, overwrite: true);
            _profiles[text] = profile;
        }
        finally
        {
            if (File.Exists(text2))
            {
                File.Delete(text2);
            }
        }
    }
}
