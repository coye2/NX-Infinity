using System;
using System.Linq;
using Avalonia.Input;
using NxInfinity.Runtime;

namespace NxInfinity;

internal sealed class ControllerDirection
{
    private Key? _held;

    private long _next;

    private long _since;

    public void Reset()
    {
        _held = null;
        _next = (_since = 0L);
    }

    public Key? Step(HostControllerSnapshot sample, long now)
    {
        int num = (sample.Buttons.Contains(HostButton.Right, null) ? 1 : 0) - (sample.Buttons.Contains(HostButton.Left, null) ? 1 : 0);
        int num2 = (sample.Buttons.Contains(HostButton.Up, null) ? 1 : 0) - (sample.Buttons.Contains(HostButton.Down, null) ? 1 : 0);
        bool flag = sample.Buttons.Any((HostButton b) => (uint)(b - 11) <= 3u);
        double num3 = (flag ? ((double)num) : sample.LeftX);
        double num4 = (flag ? ((double)num2) : sample.LeftY);
        if (!double.IsFinite(num3) || !double.IsFinite(num4))
        {
            Reset();
            return null;
        }
        double num5 = _held switch
        {
            Key.Left => 0.0 - num3,
            Key.Right => num3,
            Key.Up => num4,
            Key.Down => 0.0 - num4,
            _ => 0.0,
        };
        bool flag2;
        switch (_held)
        {
            case Key.Left:
            case Key.Right:
                flag2 = true;
                break;
            default:
                flag2 = false;
                break;
        }
        double num6 = (flag2 ? Math.Abs(num4) : Math.Abs(num3));
        Key? held = _held;
        Key? key;
        if (held.HasValue && num5 >= 0.35 && num6 < num5 * 1.3)
        {
            key = _held;
        }
        else
        {
            key = ((!(Math.Max(Math.Abs(num3), Math.Abs(num4)) < 0.55)) ? new Key?((!(Math.Abs(num3) >= Math.Abs(num4))) ? ((num4 > 0.0) ? Key.Up : Key.Down) : ((num3 < 0.0) ? Key.Left : Key.Right)) : ((Key?)null));
        }
        if (!key.HasValue)
        {
            Reset();
            return null;
        }
        bool flag3 = key != _held;
        _held = key;
        if (!flag3 && now < _next)
        {
            return null;
        }
        if (flag3)
        {
            _since = now;
        }
        _next = now + (flag3 ? 320 : ((now - _since >= 1200) ? 80 : 110));
        return key;
    }
}
