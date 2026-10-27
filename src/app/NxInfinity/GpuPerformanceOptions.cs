using System;
using System.Collections.Generic;
using System.Text.Json;

namespace NxInfinity;

public sealed record GpuPerformanceOptions(string Mode = "default", bool CompressTextures = false)
{
    public string Label
    {
        get
        {
            string mode = Mode;
            if (!(mode == "low_end"))
            {
                if (mode == "minimum")
                {
                    return "Minimum GPU load";
                }
                return "Standard graphics";
            }
            return "Low-end GPU";
        }
    }

    public static bool IsValid(GpuPerformanceOptions? value)
    {
        if ((object)value == null)
        {
            goto IL_0035;
        }
        switch (value.Mode)
        {
            case "default":
            case "low_end":
            case "minimum":
                break;
            default:
                goto IL_0035;
        }
        bool flag = true;
        goto IL_0037;
    IL_0035:
        flag = false;
        goto IL_0037;
    IL_0037:
        if (flag)
        {
            if (!(value.Mode != "default"))
            {
                return !value.CompressTextures;
            }
            return true;
        }
        return false;
    }

    public IReadOnlyDictionary<string, JsonElement> Settings()
    {
        if (!IsValid(this))
        {
            throw new ArgumentException("Unknown GPU performance mode.");
        }
        if (Mode == "default")
        {
            return new Dictionary<string, JsonElement>();
        }
        return new Dictionary<string, JsonElement>
        {
            ["graphics.resolution_scale"] = JsonSerializer.SerializeToElement((Mode == "minimum") ? 0.5 : 0.75),
            ["graphics.max_anisotropy"] = JsonSerializer.SerializeToElement((!(Mode == "minimum")) ? 2 : 0),
            ["system.docked"] = JsonSerializer.SerializeToElement(value: false),
            ["graphics.shader_cache"] = JsonSerializer.SerializeToElement(value: true),
            ["cpu.ptc"] = JsonSerializer.SerializeToElement(value: true),
            ["graphics.fast_pipeline_compilation"] = JsonSerializer.SerializeToElement(value: false),
            ["graphics.texture_recompression"] = JsonSerializer.SerializeToElement(CompressTextures),
            ["graphics.fast_gpu_time"] = JsonSerializer.SerializeToElement(value: false),
            ["graphics.vsync"] = JsonSerializer.SerializeToElement("switch")
        };
    }
}
