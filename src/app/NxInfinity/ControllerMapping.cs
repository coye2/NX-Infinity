using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NxInfinity.Runtime;

namespace NxInfinity;

public sealed record ControllerMapping(Dictionary<string, string> Buttons, double DeadZone = 0.15, double TriggerThreshold = 0.5, double LeftCenterX = 0.0, double LeftCenterY = 0.0, double RightCenterX = 0.0, double RightCenterY = 0.0)
{
    public static ControllerMapping Default()
    {
        return new ControllerMapping(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["a"] = "East",
            ["b"] = "South",
            ["x"] = "North",
            ["y"] = "West",
            ["plus"] = "Start",
            ["minus"] = "Back",
            ["l"] = "LeftShoulder",
            ["r"] = "RightShoulder",
            ["zl"] = "LeftTrigger",
            ["zr"] = "RightTrigger",
            ["left_stick"] = "LeftStick",
            ["right_stick"] = "RightStick",
            ["dpad_up"] = "Up",
            ["dpad_down"] = "Down",
            ["dpad_left"] = "Left",
            ["dpad_right"] = "Right"
        });
    }

    public void Validate()
    {
        HashSet<string> allowed = Default().Buttons.Keys.ToHashSet(StringComparer.Ordinal);
        bool flag = Buttons == null || Buttons.Count != allowed.Count || Buttons.Keys.Any((string key) => !allowed.Contains(key)) || Buttons.Values.Any((string value) => ((!(value == "LeftTrigger") && !(value == "RightTrigger")) || 1 == 0) && (!Enum.TryParse<HostButton>(value, out var result) || !Enum.IsDefined(result) || result == HostButton.Guide)) || !double.IsFinite(DeadZone);
        if (!flag)
        {
            double deadZone = DeadZone;
            bool flag2 = ((deadZone < 0.0 || deadZone > 0.5) ? true : false);
            flag = flag2;
        }
        bool flag3 = flag || !double.IsFinite(TriggerThreshold);
        if (!flag3)
        {
            double deadZone = TriggerThreshold;
            bool flag2 = ((deadZone < 0.1 || deadZone > 0.95) ? true : false);
            flag3 = flag2;
        }
        if (flag3 || new double[4] { LeftCenterX, LeftCenterY, RightCenterX, RightCenterY }.Any((double center) => !double.IsFinite(center) || Math.Abs(center) > 0.25))
        {
            throw new InvalidDataException("The controller profile is invalid. Restore its defaults before using it.");
        }
    }

    public InputSnapshot Apply(HostControllerSnapshot sample)
    {
        return new InputSnapshot
        {
            Buttons = (from pair in Buttons
                       where Pressed(pair.Value)
                       select pair.Key).ToArray(),
            LeftX = Axis(sample.LeftX, LeftCenterX),
            LeftY = Axis(sample.LeftY, LeftCenterY),
            RightX = Axis(sample.RightX, RightCenterX),
            RightY = Axis(sample.RightY, RightCenterY)
        };
        double Axis(double value, double center)
        {
            value = Math.Clamp(value - center, -1.0, 1.0);
            if (!(Math.Abs(value) <= DeadZone))
            {
                return Math.CopySign((Math.Abs(value) - DeadZone) / (1.0 - DeadZone), value);
            }
            return 0.0;
        }
        bool Pressed(string input)
        {
            if (!(input == "LeftTrigger"))
            {
                if (!(input == "RightTrigger"))
                {
                    return sample.Buttons.Contains(Enum.Parse<HostButton>(input), null);
                }
                return sample.RightTrigger > TriggerThreshold;
            }
            return sample.LeftTrigger > TriggerThreshold;
        }
    }
}
