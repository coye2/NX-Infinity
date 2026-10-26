using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace NxInfinity.Runtime;

public enum HostButton { South, East, West, North, Back, Guide, Start, LeftStick, RightStick, LeftShoulder, RightShoulder, Up, Down, Left, Right }
public enum HostControllerConnection { Unknown, Wired, Wireless }
public sealed record HostControllerPress(string DeviceId, int Button, ulong TimestampNanoseconds);

/// <summary>A real host device sample. Host button positions stay separate from Nintendo labels.</summary>
public sealed record HostControllerSnapshot
{
    public required string DeviceId { get; init; }
    public required string Name { get; init; }
    /// <summary>USB identifiers reported by SDL; zero means unavailable. These do not identify a unique physical controller.</summary>
    public ushort VendorId { get; init; }
    public ushort ProductId { get; init; }
    /// <summary>SDL mapping identity, not a serial number or a persistent assignment.</summary>
    public string? MappingGuid { get; init; }
    /// <summary>Some wireless USB receivers report Unknown or Wired; do not infer a radio link from a device name.</summary>
    public HostControllerConnection Connection { get; init; }
    public HostButton[] Buttons { get; init; } = [];
    public double LeftX { get; init; }
    public double LeftY { get; init; }
    public double RightX { get; init; }
    public double RightY { get; init; }
    public double LeftTrigger { get; init; }
    public double RightTrigger { get; init; }
    public bool IsNeutral => Buttons.Length == 0 && Math.Abs(LeftX) < .2 && Math.Abs(LeftY) < .2 &&
        Math.Abs(RightX) < .2 && Math.Abs(RightY) < .2 && LeftTrigger < .2 && RightTrigger < .2;

    public InputSnapshot ToGuestSnapshot(int player = 0)
    {
        var names = new Dictionary<HostButton, string>
        {
            [HostButton.South] = "b", [HostButton.East] = "a", [HostButton.West] = "y", [HostButton.North] = "x",
            [HostButton.Start] = "plus", [HostButton.Back] = "minus", [HostButton.LeftStick] = "left_stick", [HostButton.RightStick] = "right_stick",
            [HostButton.LeftShoulder] = "l", [HostButton.RightShoulder] = "r", [HostButton.Up] = "dpad_up", [HostButton.Down] = "dpad_down",
            [HostButton.Left] = "dpad_left", [HostButton.Right] = "dpad_right"
        };
        var buttons = Buttons.Where(names.ContainsKey).Select(b => names[b]).ToList();
        if (LeftTrigger > .5) buttons.Add("zl");
        if (RightTrigger > .5) buttons.Add("zr");
        var snapshot = new InputSnapshot { Player = player, Buttons = buttons.ToArray(), LeftX = LeftX, LeftY = LeftY, RightX = RightX, RightY = RightY };
        snapshot.Validate();
        return snapshot;
    }
}

/// <summary>
/// SDK host-input service independent of a running emulator/GPU. Call Open, Poll and Dispose on
/// the application's main/UI thread. SDL3 must come from a hash-verified local installation.
/// No devices or emulation sessions are assigned automatically. Poll returns actual attached
/// gamepads, so an empty list never becomes a simulated connection or input-consumption claim.
/// </summary>
public sealed class HostControllerInput : IDisposable
{
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly nint _library;
    private readonly Dictionary<uint, nint> _devices = [];
    private readonly Dictionary<uint, (ushort Vendor, ushort Product, string? Guid)> _identities = [];
    private readonly NativeVoid _pump;
    private readonly NativeUIntVoid _quit;
    private readonly NativeGamepads _getPads;
    private readonly NativePtrVoid _free;
    private readonly NativeOpen _open;
    private readonly NativePtrVoid _close;
    private readonly NativeName _name;
    private readonly NativeButton _button;
    private readonly NativeAxis _axis;
    private readonly NativeConnected _connected;
    private readonly NativeFlush _flush;
    private readonly NativeUShort _vendor, _product;
    private readonly NativeInt _connection;
    private readonly NativeName _mapping;
    private readonly NativePeep _peep;
    private readonly nint _eventBuffer;
    private bool _disposed;
    public string LibrarySha256 { get; }
    /// <summary>Button-down events from the last poll, in SDL queue order, not device enumeration order.</summary>
    public IReadOnlyList<HostControllerPress> ButtonPresses { get; private set; } = [];

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void NativeVoid();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void NativeUIntVoid(uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte NativeInit(uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte NativeHint([MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint NativeGamepads(out int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void NativePtrVoid(nint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint NativeOpen(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint NativeName(nint gamepad);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte NativeButton(nint gamepad, int button);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate short NativeAxis(nint gamepad, int axis);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte NativeConnected(nint gamepad);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void NativeFlush(uint minType, uint maxType);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ushort NativeUShort(nint gamepad);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NativeInt(nint gamepad);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NativePeep(nint events, int count, int action, uint minimum, uint maximum);

    private T Bind<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));

    private HostControllerInput(string path, string sha256)
    {
        LibrarySha256 = sha256;
        try { _library = NativeLibrary.Load(path); }
        catch (Exception e) when (e is DllNotFoundException or BadImageFormatException)
        { throw new NxException("missing_dependency", "The verified SDL3 input library could not load. Check its native dependencies and architecture.", e); }
        try
        {
            _pump = Bind<NativeVoid>("SDL_PumpEvents"); _quit = Bind<NativeUIntVoid>("SDL_QuitSubSystem");
            _getPads = Bind<NativeGamepads>("SDL_GetGamepads"); _free = Bind<NativePtrVoid>("SDL_free");
            _open = Bind<NativeOpen>("SDL_OpenGamepad"); _close = Bind<NativePtrVoid>("SDL_CloseGamepad");
            _name = Bind<NativeName>("SDL_GetGamepadName"); _button = Bind<NativeButton>("SDL_GetGamepadButton");
            _axis = Bind<NativeAxis>("SDL_GetGamepadAxis"); _connected = Bind<NativeConnected>("SDL_GamepadConnected");
            _flush = Bind<NativeFlush>("SDL_FlushEvents");
            _peep = Bind<NativePeep>("SDL_PeepEvents");
            _vendor = Bind<NativeUShort>("SDL_GetGamepadVendor"); _product = Bind<NativeUShort>("SDL_GetGamepadProduct");
            _connection = Bind<NativeInt>("SDL_GetGamepadConnectionState"); _mapping = Bind<NativeName>("SDL_GetGamepadMapping");
            Bind<NativeVoid>("SDL_SetMainReady")();
            Bind<NativeHint>("SDL_SetHint")("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
            if (Bind<NativeInit>("SDL_InitSubSystem")(0x00002000) == 0)
                throw new NxException("input_unavailable", "SDL3 could not initialize host gamepad input.");
            _eventBuffer = Marshal.AllocHGlobal(128 * 64); // SDL3 SDL_Event ABI, 64 events per batch.
        }
        catch { NativeLibrary.Free(_library); throw; }
    }

    public static HostControllerInput Open(string libraryPath, string expectedSha256)
    {
        var path = SafePaths.Absolute(libraryPath);
        SafePaths.RejectLinks(path);
        if (expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit))
            throw new NxException("invalid_argument", "An exact SDL3 library SHA-256 is required.");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = Convert.ToHexStringLower(SHA256.HashData(file));
        if (!hash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase)) throw new NxException("version_mismatch", "SDL3 input library hash mismatch.");
        return new HostControllerInput(path, hash);
    }

    public IReadOnlyList<HostControllerSnapshot> Poll(double deadZone = .20)
    {
        RequireThread();
        if (!double.IsFinite(deadZone) || deadZone is < 0 or > .5) throw new NxException("invalid_argument", "Stick dead zone must be 0..0.5.");
        _pump();
        var list = _getPads(out int count);
        if (count is < 0 or > 64) { if (list != 0) _free(list); throw new NxException("input_unavailable", "Invalid gamepad enumeration."); }
        var seen = new HashSet<uint>();
        try
        {
            for (int i = 0; list != 0 && i < count; i++)
            {
                var id = unchecked((uint)Marshal.ReadInt32(list, i * 4));
                if (id == 0) continue;
                seen.Add(id);
                if (!_devices.ContainsKey(id))
                {
                    var handle = _open(id);
                    if (handle == 0) continue;
                    _devices[id] = handle;
                    var mapping = _mapping(handle);
                    try
                    {
                        string? guid = Marshal.PtrToStringUTF8(mapping)?.Split(',')[0];
                        if (guid is not { Length: 32 } || !guid.All(Uri.IsHexDigit)) guid = null;
                        _identities[id] = (_vendor(handle), _product(handle), guid);
                    }
                    finally { if (mapping != 0) _free(mapping); }
                }
            }
        }
        finally { if (list != 0) _free(list); }
        foreach (var pair in _devices.ToArray())
            if (!seen.Contains(pair.Key) || _connected(pair.Value) == 0)
            { _close(pair.Value); _devices.Remove(pair.Key); _identities.Remove(pair.Key); }
        var results = new List<HostControllerSnapshot>();
        foreach (var (id, pad) in _devices)
        {
            double Axis(int axis, bool invert = false)
            {
                var value = _axis(pad, axis);
                double normalized = value < 0 ? value / 32768.0 : value / 32767.0;
                if (Math.Abs(normalized) < deadZone) return 0;
                return invert ? -normalized : normalized;
            }
            results.Add(new HostControllerSnapshot
            {
                DeviceId = "sdl:" + id, Name = Marshal.PtrToStringUTF8(_name(pad)) ?? "Gamepad",
                VendorId = _identities[id].Vendor, ProductId = _identities[id].Product, MappingGuid = _identities[id].Guid,
                Connection = _connection(pad) switch { 1 => HostControllerConnection.Wired, 2 => HostControllerConnection.Wireless, _ => HostControllerConnection.Unknown },
                Buttons = Enum.GetValues<HostButton>().Where(button => _button(pad, (int)button) != 0).ToArray(),
                LeftX = Axis(0), LeftY = Axis(1, true), RightX = Axis(2), RightY = Axis(3, true),
                LeftTrigger = Math.Clamp(_axis(pad, 4) / 32767.0, 0, 1), RightTrigger = Math.Clamp(_axis(pad, 5) / 32767.0, 0, 1)
            });
        }
        var presses = new List<HostControllerPress>();
        // SDL_EVENT_GAMEPAD_BUTTON_DOWN = 0x651; SDL_GETEVENT = 2.
        // Preserve actual queue order, including a press released between polls.
        for (int batch = 0; batch < 64; batch++)
        {
            int received = _peep(_eventBuffer, 64, 2, 0x651, 0x651);
            if (received < 0) throw new NxException("input_unavailable", "SDL3 could not read controller button events.");
            for (int i = 0; i < received; i++)
            {
                nint item = _eventBuffer + i * 128;
                uint id = unchecked((uint)Marshal.ReadInt32(item, 16));
                if (_devices.ContainsKey(id)) presses.Add(new("sdl:" + id, Marshal.ReadByte(item, 20), unchecked((ulong)Marshal.ReadInt64(item, 8))));
            }
            if (received < 64) break;
        }
        ButtonPresses = presses;
        // Drain gamepad/joystick events owned by this service
        // to keep a long-running library screen from accumulating an unbounded SDL queue.
        _flush(0x600, 0x6FF);
        return results;
    }

    private void RequireThread()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(HostControllerInput));
        if (Environment.CurrentManagedThreadId != _thread) throw new NxException("invalid_thread", "HostControllerInput belongs to its initializing UI thread.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        RequireThread();
        foreach (var pad in _devices.Values) _close(pad);
        _devices.Clear(); _identities.Clear(); ButtonPresses = []; Marshal.FreeHGlobal(_eventBuffer);
        _quit(0x00002000); NativeLibrary.Free(_library); _disposed = true;
    }
}

