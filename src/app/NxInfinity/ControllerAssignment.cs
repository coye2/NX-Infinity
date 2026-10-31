using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using NxInfinity.Runtime;

namespace NxInfinity;

public sealed class ControllerAssignment
{
    private sealed record Preference(int SchemaVersion, bool Enabled, string? DeviceName);

    private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly string _preferencePath;

    private Preference _preference = new Preference(1, Enabled: false, null);

    private string? _deviceId;

    private bool _preferenceUnreadable;

    private bool _preferenceSaved;

    public IReadOnlyList<HostControllerSnapshot> Devices { get; private set; } = Array.Empty<HostControllerSnapshot>();

    public ControllerAssignmentState State
    {
        get
        {
            int num = ((_preference.DeviceName != null) ? Devices.Count((HostControllerSnapshot d) => d.Name == _preference.DeviceName) : 0);
            return new ControllerAssignmentState(_preferenceUnreadable ? ControllerAssignmentPhase.PreferenceUnreadable : (_preference.Enabled ? ((_deviceId != null) ? ControllerAssignmentPhase.Assigned : (num switch
            {
                1 => ControllerAssignmentPhase.ConfirmConnection,
                0 => ControllerAssignmentPhase.Disconnected,
                _ => ControllerAssignmentPhase.Ambiguous,
            })) : ControllerAssignmentPhase.Released), _preference.DeviceName, _deviceId, Devices.Count, num, _preferenceSaved);
        }
    }

    public ControllerAssignment(string workspaceRoot, int player = 0)
    {
        if ((player < 0 || player > 3) ? true : false)
        {
            throw new ArgumentOutOfRangeException("player");
        }
        if (!Path.IsPathFullyQualified(workspaceRoot))
        {
            throw new ArgumentException("The controller preference workspace must be absolute.");
        }
        _preferencePath = Path.Combine(Path.GetFullPath(workspaceRoot), "frontend-input", $"player{player + 1}.json");
        try
        {
            RejectLinks(_preferencePath);
            if (!File.Exists(_preferencePath))
            {
                return;
            }
            if (new FileInfo(_preferencePath).Length > 4096)
            {
                throw new InvalidDataException();
            }
            using JsonDocument jsonDocument = JsonDocument.Parse(File.ReadAllText(_preferencePath), new JsonDocumentOptions
            {
                MaxDepth = 4
            });
            if (jsonDocument.RootElement.ValueKind != JsonValueKind.Object || jsonDocument.RootElement.EnumerateObject().GroupBy((JsonProperty p) => p.Name, StringComparer.OrdinalIgnoreCase).Any((IGrouping<string, JsonProperty> group) => group.Count() != 1))
            {
                throw new InvalidDataException();
            }
            Preference preference = jsonDocument.Deserialize<Preference>(Json);
            if ((object)preference == null || preference.SchemaVersion != 1 || (preference.Enabled && !ValidName(preference.DeviceName)) || (!preference.Enabled && preference.DeviceName != null))
            {
                throw new InvalidDataException();
            }
            _preference = preference;
            _preferenceSaved = true;
        }
        catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is JsonException) ? true : false)
        {
            _preferenceUnreadable = true;
        }
    }

    public void Observe(IReadOnlyList<HostControllerSnapshot> devices)
    {
        Devices = devices.ToArray();
        if (_deviceId != null && !Devices.Any((HostControllerSnapshot d) => d.DeviceId == _deviceId && d.Name == _preference.DeviceName))
        {
            _deviceId = null;
        }
    }

    public bool Assign(string? deviceId)
    {
        HostControllerSnapshot hostControllerSnapshot = ((deviceId == null) ? null : (Devices.SingleOrDefault((HostControllerSnapshot d) => d.DeviceId == deviceId) ?? throw new NxException("input_disconnected", "That controller is no longer connected. Choose a currently attached controller.")));
        if ((object)hostControllerSnapshot != null && !ValidName(hostControllerSnapshot.Name))
        {
            throw new NxException("input_unavailable", "This controller did not provide a usable device name.");
        }
        Preference preference = new Preference(1, (object)hostControllerSnapshot != null, hostControllerSnapshot?.Name);
        bool result = _deviceId != deviceId || _preference.Enabled != preference.Enabled;
        if (!_preferenceSaved || _preferenceUnreadable || preference != _preference)
        {
            Write(preference);
        }
        _preference = preference;
        _preferenceSaved = true;
        _preferenceUnreadable = false;
        _deviceId = deviceId;
        return result;
    }

    private void Write(Preference preference)
    {
        RejectLinks(_preferencePath);
        string? directoryName = Path.GetDirectoryName(_preferencePath);
        Directory.CreateDirectory(directoryName);
        RejectLinks(directoryName);
        string text = Path.Combine(directoryName, ".player1-write-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (FileStream fileStream = new FileStream(text, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(fileStream, preference, Json);
                fileStream.Flush(flushToDisk: true);
            }
            File.Move(text, _preferencePath, overwrite: true);
        }
        catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
        {
            throw new NxException("input_preference_not_saved", "The controller preference could not be saved. The previous gameplay assignment remains selected.", ex);
        }
        finally
        {
            if (File.Exists(text))
            {
                File.Delete(text);
            }
        }
    }

    private static bool ValidName(string? name)
    {
        if (name != null)
        {
            int length = name.Length;
            if (length > 0 && length <= 256 && !string.IsNullOrWhiteSpace(name))
            {
                return !name.Any(char.IsControl);
            }
        }
        return false;
    }

    private static void RejectLinks(string path)
    {
        for (FileSystemInfo fileSystemInfo = (Directory.Exists(path) ? ((FileSystemInfo)new DirectoryInfo(path)) : ((FileSystemInfo)new FileInfo(path))); fileSystemInfo != null; fileSystemInfo = ((fileSystemInfo is DirectoryInfo directoryInfo) ? directoryInfo.Parent : ((FileInfo)fileSystemInfo).Directory))
        {
            if (fileSystemInfo.LinkTarget != null || (fileSystemInfo.Exists && (fileSystemInfo.Attributes & FileAttributes.ReparsePoint) != 0))
            {
                throw new IOException("Controller preferences cannot traverse filesystem links.");
            }
        }
    }
}
