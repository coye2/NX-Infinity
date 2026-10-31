using System;
using System.Linq;

namespace NxInfinity.Core;

internal static class SettingCatalog
{
    public static readonly Setting[] All = new Setting[11]
    {
        new Setting("audio.volume", "Volume", "OpenAL device master volume; effective is read from the core audio driver.", "number", 1.0, 0.0, 1.0, Array.Empty<string>(), "live"),
        new Setting("system.docked", "Docked mode", "Change the emulated performance mode and notify guest applets. Actual game response remains game-specific.", "boolean", true, null, null, Array.Empty<string>(), "live"),
        new Setting("graphics.resolution_scale", "Resolution scale", "Core texture resolution scale, written on the GPU thread. Effective readback is configuration acknowledgment, not evidence of visual correctness.", "number", 1.0, 0.5, 4.0, Array.Empty<string>(), "live"),
        new Setting("graphics.vsync", "Vertical synchronization", "Apply guest synchronization and the actual renderer window's presentation request together. Readback observes the renderer setter return, not physical scanout or the selected swapchain mode. Unbounded can alter game speed and is not a universal FPS fix.", "enum", "switch", null, null, new string[2] { "switch", "unbounded" }, "live"),
        new Setting("graphics.max_anisotropy", "Maximum anisotropy", "-1 uses the game's requested value; other values are passed to the pinned renderer's sampler logic.", "integer", -1, -1.0, 16.0, Array.Empty<string>(), "before_load"),
        new Setting("graphics.shader_cache", "Shader cache", "Use the isolated backend profile's disk shader cache on the next content load.", "boolean", true, null, null, Array.Empty<string>(), "before_load"),
        new Setting("graphics.backend_threading", "Renderer threading", "Use a separate native renderer consumer thread. Fixed during initialization; a changed request takes effect only in a new worker. The original single GPU thread remains available. First-use shader compilation can still cause hitches; game-specific results vary.", "enum", "threaded_renderer", null, null, new string[2] { "single_gpu_thread", "threaded_renderer" }, "before_initialize"),
        new Setting("graphics.fast_pipeline_compilation", "Fast pipeline compilation", "Reduce first-use graphics pipeline compilation cost for every game on this core. May reduce sustained GPU performance on some games or devices. Disable to favor driver optimization. Applies on the next game launch; no effects or draws are skipped.", "boolean", true, null, null, Array.Empty<string>(), "before_load"),
        new Setting("graphics.texture_recompression", "Texture recompression", "Recompress host-unsupported texture formats. May affect fidelity and performance; qualification is pending.", "boolean", false, null, null, Array.Empty<string>(), "before_load"),
        new Setting("graphics.fast_gpu_time", "Fast GPU timestamps", "Report shorter GPU elapsed times to the game. This can keep dynamic resolution high even when the GPU is struggling. Disable to let games with adaptive resolution respond to actual elapsed time. Does not change the emulated CPU clock or frame-rate target.", "boolean", true, null, null, Array.Empty<string>(), "before_load"),
        new Setting("cpu.ptc", "Persistent translation cache", "Use the pinned CPU backend's translation cache on content load.", "boolean", true, null, null, Array.Empty<string>(), "before_load")
    };

    public static Setting Find(string id)
    {
        return All.FirstOrDefault((Setting s) => s.Id == id) ?? throw new NxError("unsupported", "Setting '" + id + "' is not integrated by this adapter.");
    }
}
