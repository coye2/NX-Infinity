using System;
using System.Collections.Generic;
using System.Linq;
using NxInfinity.Runtime;

namespace NxInfinity;

public sealed class ControllerGuestRouter
{
    private readonly string?[] _devices = new string[4];

    private readonly bool[] _ready = new bool[4];

    private readonly bool[] _sent = new bool[4];

    private bool _guest;

    private readonly InputSnapshot?[] _last = new InputSnapshot[4];

    private readonly long[] _lastSent = new long[4];

    private readonly Func<long> _clock;

    public ControllerGuestRouter(Func<long>? clock = null)
    {
        _clock = clock ?? ((Func<long>)(() => Environment.TickCount64));
    }

    private static bool Same(InputSnapshot a, InputSnapshot b)
    {
        if (a.LeftX == b.LeftX && a.LeftY == b.LeftY && a.RightX == b.RightX && a.RightY == b.RightY)
        {
            return ((ReadOnlySpan<string>)a.Buttons.AsSpan()).SequenceEqual((ReadOnlySpan<string>)b.Buttons);
        }
        return false;
    }

    public IReadOnlyList<InputSnapshot> Update(ControllerRoster roster, IReadOnlyList<HostControllerSnapshot> samples, bool guest, bool suspend, Func<HostControllerSnapshot, InputSnapshot> map, bool homeChord)
    {
        List<InputSnapshot> list = new List<InputSnapshot>();
        if (!guest | suspend)
        {
            for (int i = 0; i < 4; i++)
            {
                if (guest && _sent[i])
                {
                    list.Add(new InputSnapshot
                    {
                        Player = i
                    });
                }
                _ready[i] = false;
                _sent[i] = false;
                _last[i] = null;
            }
            _guest = false;
            return list;
        }
        if (!_guest)
        {
            Array.Clear(_ready);
        }
        _guest = true;
        for (int j = 0; j < 4; j++)
        {
            string id = roster.Players[j].State.DeviceId;
            if (_devices[j] != id)
            {
                if (_sent[j])
                {
                    list.Add(new InputSnapshot
                    {
                        Player = j
                    });
                }
                _devices[j] = id;
                _ready[j] = false;
                _sent[j] = false;
                _last[j] = null;
            }
            HostControllerSnapshot hostControllerSnapshot = samples.FirstOrDefault((HostControllerSnapshot device) => device.DeviceId == id);
            if ((object)hostControllerSnapshot == null || roster.WaitingForNeutral(hostControllerSnapshot.DeviceId))
            {
                continue;
            }
            if (!_ready[j])
            {
                if (!hostControllerSnapshot.IsNeutral)
                {
                    continue;
                }
                _ready[j] = true;
            }
            if (homeChord && hostControllerSnapshot.Buttons.Contains(HostButton.Back, null) && hostControllerSnapshot.Buttons.Contains(HostButton.Start, null))
            {
                hostControllerSnapshot = hostControllerSnapshot with
                {
                    Buttons = hostControllerSnapshot.Buttons.Where((HostButton button) =>
                    {
                        bool flag = ((button == HostButton.Back || button == HostButton.Start) ? true : false);
                        return !flag;
                    }).ToArray()
                };
            }
            InputSnapshot inputSnapshot = map(hostControllerSnapshot) with
            {
                Player = j
            };
            long num = _clock();
            if ((object)_last[j] == null || !Same(_last[j], inputSnapshot) || num - _lastSent[j] >= 250)
            {
                list.Add(inputSnapshot);
                _last[j] = inputSnapshot;
                _lastSent[j] = num;
            }
            _sent[j] = true;
        }
        return list;
    }
}
