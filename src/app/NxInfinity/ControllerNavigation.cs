using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using NxInfinity.Runtime;

namespace NxInfinity;

public sealed class ControllerNavigation(MainWindow main)
{
    private HashSet<HostButton> _previous = new HashSet<HostButton>();

    private string? _uiDevice;

    private Window? _uiWindow;

    private bool _guest;

    private bool _needsUiNeutral = true;

    private bool _assignmentNeutralPending;

    private readonly HashSet<Window> _pendingWindows = new HashSet<Window>();

    private readonly ControllerDirection _directions = new ControllerDirection();

    private readonly HashSet<string> _neutralDevices = new HashSet<string>();

    private long _scrollAt;

    private string? _gameDeviceId;

    private bool _homeArmed;

    private bool _homePending;

    private string? _chordDevice;

    private long _chordSince;

    public string? GameDeviceId
    {
        get
        {
            return _gameDeviceId;
        }
        set
        {
            if (!(_gameDeviceId == value))
            {
                _assignmentNeutralPending |= _guest && (GuestReady || (object)LastSample != null);
                _gameDeviceId = value;
                GuestReady = false;
                LastSample = null;
                _previous.Clear();
                _needsUiNeutral = true;
                _directions.Reset();
            }
        }
    }

    public bool GuestReady { get; private set; }

    public HostControllerSnapshot? LastSample { get; private set; }

    public bool SendNeutral { get; private set; }

    public void RequireNeutral()
    {
        _needsUiNeutral = true;
        GuestReady = false;
        LastSample = null;
        _previous.Clear();
        _directions.Reset();
        _homeArmed = false;
    }

    public void Update(IReadOnlyList<HostControllerSnapshot> samples, bool guest, long? timestamp = null)
    {
        long num = timestamp ?? Environment.TickCount64;
        SendNeutral = _assignmentNeutralPending & guest;
        _assignmentNeutralPending = false;
        HostControllerSnapshot hostControllerSnapshot = samples.FirstOrDefault((HostControllerSnapshot s) => s.Buttons.Contains(HostButton.Guide, null));
        HostControllerSnapshot hostControllerSnapshot2 = (main.HomeFallbackChordEnabled ? samples.FirstOrDefault((HostControllerSnapshot s) => s.Buttons.Contains(HostButton.Back, null) && s.Buttons.Contains(HostButton.Start, null)) : null);
        if (hostControllerSnapshot2?.DeviceId != _chordDevice)
        {
            _chordDevice = hostControllerSnapshot2?.DeviceId;
            _chordSince = num;
        }
        if ((object)hostControllerSnapshot == null && (object)hostControllerSnapshot2 == null)
        {
            _homeArmed = true;
        }
        if (_homeArmed && ((object)hostControllerSnapshot != null || ((object)hostControllerSnapshot2 != null && num - _chordSince >= 450)))
        {
            _homeArmed = false;
            GuestReady = false;
            SendNeutral |= guest;
            LastSample = null;
            _needsUiNeutral = true;
            if (!_homePending)
            {
                _homePending = true;
                _ = Home();
            }
            return;
        }
        if (guest != _guest)
        {
            _guest = guest;
            GuestReady = false;
            _needsUiNeutral = true;
            _previous.Clear();
            _directions.Reset();
        }
        HostControllerSnapshot hostControllerSnapshot3 = samples.FirstOrDefault((HostControllerSnapshot s) => s.DeviceId == GameDeviceId);
        if (guest)
        {
            if ((object)hostControllerSnapshot3 == null)
            {
                SendNeutral |= GuestReady;
                GuestReady = false;
                LastSample = null;
                _previous.Clear();
                return;
            }
            LastSample = ((hostControllerSnapshot2?.DeviceId == hostControllerSnapshot3.DeviceId) ? hostControllerSnapshot3 with
            {
                Buttons = hostControllerSnapshot3.Buttons.Where((HostButton b) =>
                {
                    bool flag2 = ((b == HostButton.Back || b == HostButton.Start) ? true : false);
                    return !flag2;
                }).ToArray()
            } : hostControllerSnapshot3);
            if (!GuestReady)
            {
                if (hostControllerSnapshot3.IsNeutral)
                {
                    GuestReady = true;
                }
                _previous = hostControllerSnapshot3.Buttons.ToHashSet();
            }
            else
            {
                HashSet<HostButton> previous = hostControllerSnapshot3.Buttons.ToHashSet();
                _previous = previous;
            }
            return;
        }
        LastSample = null;
        _neutralDevices.RemoveWhere((string id) => !samples.Any((HostControllerSnapshot s) => s.DeviceId == id));
        foreach (HostControllerSnapshot item in samples.Where(UiNeutral))
        {
            _neutralDevices.Add(item.DeviceId);
        }
        HostControllerSnapshot hostControllerSnapshot4 = samples.FirstOrDefault((HostControllerSnapshot s) => s.DeviceId == _uiDevice);
        HostControllerSnapshot hostControllerSnapshot5 = (((object)hostControllerSnapshot4 != null && !UiNeutral(hostControllerSnapshot4)) ? hostControllerSnapshot4 : (samples.FirstOrDefault((HostControllerSnapshot s) => !UiNeutral(s)) ?? hostControllerSnapshot4 ?? samples.FirstOrDefault()));
        if ((object)hostControllerSnapshot5 == null)
        {
            _uiDevice = null;
            _previous.Clear();
            _needsUiNeutral = true;
            _directions.Reset();
            return;
        }
        if (_uiDevice != hostControllerSnapshot5.DeviceId)
        {
            _uiDevice = hostControllerSnapshot5.DeviceId;
            _previous.Clear();
            _needsUiNeutral = !_neutralDevices.Contains(hostControllerSnapshot5.DeviceId);
            _directions.Reset();
        }
        Window window = Navigation.CurrentWindow ?? main;
        if (_uiWindow != window)
        {
            _uiWindow = window;
            _needsUiNeutral = true;
            _directions.Reset();
        }
        if (_needsUiNeutral)
        {
            if (UiNeutral(hostControllerSnapshot5))
            {
                _needsUiNeutral = false;
            }
            _previous = hostControllerSnapshot5.Buttons.ToHashSet();
            return;
        }
        HashSet<HostButton> buttons = hostControllerSnapshot5.Buttons.ToHashSet();
        bool flag = object.Equals(window.Tag, "game-input");
        if ((object)hostControllerSnapshot2 != null || (object)hostControllerSnapshot != null)
        {
            _previous = buttons;
            return;
        }
        if (window == main && main.IsHomeMode && Pressed(HostButton.Start))
        {
            Run(window, main.HomeControllerOptionsAsync);
        }
        else if (window == main && main.IsHomeMode && Pressed(HostButton.North))
        {
            Run(window, main.HomeControllerStopAsync);
        }
        else if (window.Name == "GameKeyboard" && Pressed(HostButton.Start))
        {
            Button button = window.GetVisualDescendants().OfType<Button>().FirstOrDefault((Button b) => b.Name == "GameKeyboardSubmit");
            if (button != null && button.IsEnabled)
            {
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
        }
        else if (Pressed(flag ? HostButton.East : HostButton.South))
        {
            Run(window, () => Navigation.ActivateAsync(window));
        }
        else if (Pressed((!flag) ? HostButton.East : HostButton.South))
        {
            Run(window, () => (window != main) ? CloseDialog(window) : main.ControllerBackAsync());
        }
        else if (window == main && Pressed(HostButton.LeftShoulder))
        {
            main.CycleSection(-1);
        }
        else if (window == main && Pressed(HostButton.RightShoulder))
        {
            main.CycleSection(1);
        }
        if (buttons.Except(_previous).Any((HostButton b) =>
        {
            switch (b)
            {
                case HostButton.South:
                case HostButton.East:
                case HostButton.North:
                case HostButton.Start:
                case HostButton.LeftShoulder:
                case HostButton.RightShoulder:
                    return true;
                default:
                    return false;
            }
        }))
        {
            _directions.Reset();
            _needsUiNeutral = true;
        }
        else
        {
            Key? key = _directions.Step(hostControllerSnapshot5, num);
            if (key.HasValue)
            {
                Key valueOrDefault = key.GetValueOrDefault();
                Navigation.Move(window, valueOrDefault);
            }
        }
        if ((window != main || !main.IsHomeLibrary) && Math.Abs(hostControllerSnapshot5.RightY) > 0.4 && num >= _scrollAt)
        {
            Navigation.Scroll(window, (0.0 - hostControllerSnapshot5.RightY) * 16.0);
            _scrollAt = num + 16;
        }
        _previous = buttons;
        async Task Home()
        {
            try
            {
                await main.ControllerHomeAsync();
            }
            finally
            {
                _homePending = false;
            }
        }
        bool Pressed(HostButton item)
        {
            if (buttons.Contains(item))
            {
                return !_previous.Contains(item);
            }
            return false;
        }
    }

    private static bool UiNeutral(HostControllerSnapshot sample)
    {
        if (sample.Buttons.Length == 0 && Math.Abs(sample.LeftX) < 0.4 && Math.Abs(sample.LeftY) < 0.4)
        {
            return Math.Abs(sample.RightY) < 0.4;
        }
        return false;
    }

    private static Task CloseDialog(Window window)
    {
        window.Close();
        return Task.CompletedTask;
    }

    private void Run(Window owner, Func<Task> action)
    {
        if (_pendingWindows.Add(owner))
        {
            _ = Invoke();
        }
        async Task Invoke()
        {
            try
            {
                await action();
            }
            finally
            {
                _pendingWindows.Remove(owner);
            }
        }
    }
}
