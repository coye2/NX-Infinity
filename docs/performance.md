# low-end GPUs

open **Settings → Graphics → Low-end GPU performance** in Desktop or HOME. the same controls are also on the Performance page.

start with **Low-end GPU**. use **Minimum load** if the card still struggles. restart the game after changing modes.

| setting | Low-end GPU | Minimum load |
| --- | --- | --- |
| internal resolution | 0.75× | 0.5× |
| at a 720p base | 960 × 540 | 640 × 360 |
| anisotropic filtering | 2× | basic filtering |
| console mode | handheld | handheld |
| shader cache | on | on |
| CPU translation cache | on | on |
| graphics pipelines | driver optimization enabled | driver optimization enabled |
| game synchronization | original | original |
| GPU timestamps for adaptive resolution | real elapsed time | real elapsed time |

the game chooses its base resolution. some targets cannot be scaled, and dynamic resolution can change the result. lower resolution means less GPU work and a softer picture. these modes do not skip effects, remove draws or unlock game speed.

## cards short on VRAM

**Save VRAM with texture compression** is optional. it compresses texture formats the host GPU cannot use directly. this can help memory pressure, but it costs CPU time and can change image quality. try it if memory fills up; turn it back off if loading gets worse. it does not compress every texture.

the core sizes its texture cache against the card's reported memory and available host memory. it does not treat the emulated console's RAM as your PC's total RAM.

## caches and stutter

leave the shader and CPU caches on. the first visit to a scene can still compile new work. these modes enable driver optimization for graphics pipelines, which can take longer to compile the first time. **Fast pipeline compilation** in the individual settings trades some driver optimization for quicker first compilation.

for a game-specific change, switch Settings from **Core defaults** to **This game**. individual game settings win over the mode. a saved 2× resolution override will still render at 2×, so check those first if the preset seems to do nothing. MP4's normal graphics upgrade does not override a low-end mode.

choose **Standard** to restore core preferences and normal title defaults. a game can use Standard while the global setting stays Low-end, or select **Use core performance mode** to inherit the global choice again.

## check the result

use the Performance page to compare the same scene after caches have warmed up. look at frame intervals and long stalls as well as average FPS. a fast menu is not a gameplay benchmark. lower resolution helps GPU limits; it cannot fix a slow CPU, every driver issue or a game bug.

the renderer requires a compatible Vulkan driver. there is no software fallback that makes unsupported cards fast. the preset values, persistence, both interfaces and native core acknowledgments are tested separately from old-GPU frame-rate claims. older hardware still needs its own gameplay results.
