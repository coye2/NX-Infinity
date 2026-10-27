using System;
using System.Linq;
using System.Text.Json;

namespace NxInfinity.Core;

internal sealed record Setting(string Id, string Label, string Description, string Kind, object DefaultValue, double? Minimum, double? Maximum, string[] Values, string Apply, string Scope = "session", bool Supported = true)
{
    public object Validate(JsonElement value)
    {
        switch (Kind)
        {
            case "boolean":
                {
                    JsonValueKind valueKind = value.ValueKind;
                    if (valueKind - 5 <= JsonValueKind.Object)
                    {
                        return value.GetBoolean();
                    }
                    break;
                }
            case "number":
            case "integer":
                {
                    if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var value2) && double.IsFinite(value2))
                    {
                        if ((Minimum.HasValue && value2 < Minimum) || (Maximum.HasValue && value2 > Maximum) || (Kind == "integer" && Math.Truncate(value2) != value2))
                        {
                            throw new NxError("invalid_argument", "Value for '" + Id + "' is outside its schema.");
                        }
                        return value2;
                    }
                    break;
                }
            case "enum":
                if (value.ValueKind == JsonValueKind.String && Enumerable.Contains(Values, value.GetString()))
                {
                    return value.GetString();
                }
                break;
        }
        throw new NxError("invalid_argument", $"Value for '{Id}' does not match its {Kind} schema.");
    }
}
