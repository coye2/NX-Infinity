using System;
using System.Collections.Generic;
using System.Linq;
using NxInfinity.Runtime;

namespace NxInfinity;

public sealed class ControllerRoster
{
    private readonly HashSet<string> _waitingForNeutral = new HashSet<string>(StringComparer.Ordinal);

    private readonly HashSet<string> _releasedDevices = new HashSet<string>(StringComparer.Ordinal);

    public ControllerAssignment[] Players { get; }

    public ControllerRoster(string dataRoot)
    {
        Players = (from player in Enumerable.Range(0, 4)
                   select new ControllerAssignment(dataRoot, player)).ToArray();
    }

    public void Observe(IReadOnlyList<HostControllerSnapshot> devices, IReadOnlyList<HostControllerPress> presses)
    {
        ControllerAssignment[] players = Players;
        for (int i = 0; i < players.Length; i++)
        {
            players[i].Observe(devices);
        }
        _releasedDevices.RemoveWhere((string id) => !devices.Any((HostControllerSnapshot device) => device.DeviceId == id));
        _waitingForNeutral.RemoveWhere((string id) => !devices.Any((HostControllerSnapshot device) => device.DeviceId == id && !device.IsNeutral));
        foreach (HostControllerPress press in presses)
        {
            if (!_releasedDevices.Contains(press.DeviceId) && devices.Any((HostControllerSnapshot device) => device.DeviceId == press.DeviceId) && !Players.Any((ControllerAssignment player) => player.State.DeviceId == press.DeviceId))
            {
                ControllerAssignment controllerAssignment = Players.FirstOrDefault((ControllerAssignment player) => player.State.DeviceId == null);
                if (controllerAssignment == null)
                {
                    break;
                }
                controllerAssignment.Assign(press.DeviceId);
                _waitingForNeutral.Add(press.DeviceId);
            }
        }
    }

    public bool Assign(int player, string? deviceId)
    {
        if ((player < 0 || player > 3) ? true : false)
        {
            throw new ArgumentOutOfRangeException("player");
        }
        if (deviceId != null && Players.Where((ControllerAssignment _, int index) => index != player).Any((ControllerAssignment slot) => slot.State.DeviceId == deviceId))
        {
            throw new NxException("input_already_assigned", "This controller already belongs to another player. Reorder controllers to change the player order.");
        }
        string deviceId2 = Players[player].State.DeviceId;
        bool flag = Players[player].Assign(deviceId);
        if (deviceId2 != null && deviceId == null)
        {
            _releasedDevices.Add(deviceId2);
        }
        if (deviceId != null)
        {
            _releasedDevices.Remove(deviceId);
            if (flag)
            {
                _waitingForNeutral.Add(deviceId);
            }
        }
        return flag;
    }

    public void Reorder()
    {
        ControllerAssignment[] players = Players;
        for (int i = 0; i < players.Length; i++)
        {
            players[i].Assign(null);
        }
        _releasedDevices.Clear();
        _waitingForNeutral.Clear();
    }

    public IReadOnlyList<HostControllerSnapshot> NavigationSamples(IReadOnlyList<HostControllerSnapshot> devices)
    {
        return devices.Where((HostControllerSnapshot device) => !_waitingForNeutral.Contains(device.DeviceId)).ToArray();
    }

    public bool WaitingForNeutral(string id)
    {
        return _waitingForNeutral.Contains(id);
    }
}
