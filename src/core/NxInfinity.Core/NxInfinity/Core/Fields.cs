using System;
using System.Text.Json;

namespace NxInfinity.Core;

internal static class Fields
{
    public static string RequiredString(JsonElement p, string key)
    {
        if (!p.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new NxError("invalid_argument", key + " must be a nonempty string.");
        }
        return value.GetString();
    }

    public static string String(JsonElement p, string key, string fallback)
    {
        if (!p.TryGetProperty(key, out var value))
        {
            return fallback;
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new NxError("invalid_argument", key + " must be a string.");
        }
        return value.GetString();
    }

    public static bool Boolean(JsonElement p, string key, bool fallback)
    {
        if (p.TryGetProperty(key, out var value))
        {
            JsonValueKind valueKind = value.ValueKind;
            if (valueKind - 5 > JsonValueKind.Object)
            {
                throw new NxError("invalid_argument", key + " must be a boolean.");
            }
            return value.GetBoolean();
        }
        return fallback;
    }

    public static double Number(JsonElement p, string key, double fallback, double min, double max)
    {
        double value = fallback;
        if (p.TryGetProperty(key, out var value2) && (value2.ValueKind != JsonValueKind.Number || !value2.TryGetDouble(out value) || !double.IsFinite(value)))
        {
            throw new NxError("invalid_argument", key + " must be a finite number.");
        }
        if (value < min || value > max)
        {
            throw new NxError("invalid_argument", $"{key} is out of range [{min}, {max}].");
        }
        return value;
    }

    public static int Integer(JsonElement p, string key, int fallback, int min, int max)
    {
        double num = Number(p, key, fallback, min, max);
        if (num != Math.Truncate(num))
        {
            throw new NxError("invalid_argument", key + " must be an integer.");
        }
        return (int)num;
    }
}
