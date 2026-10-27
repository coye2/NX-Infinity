<p align="center"><img src="assets/nx-infinity.png" width="260" alt="NX Infinity"></p>

# NX Infinity

**Stable Release v1 · 1.0.0**

my take on a Switch emulator. an entirely remade Switch themed UI, with Desktop when you're at your desk and HOME when you're on a controller.

NX Infinity is a fork of the Ryujinx core through Kenji-NX, with extensive repairs and performance improvements. huge credit to the Ryujinx and Kenji-NX teams for the foundation this is built on.

**[Windows download](https://github.com/coye2/NX-Infinity/releases/latest)** · **[Native Linux](docs/linux.md)** · **[What's changed](CHANGELOG.md)**

## get started

Download the Windows ZIP, extract it into a new folder and open **NX Infinity.exe**. keep the folders together. add your games, keys and firmware through setup, then you're ready to go.

Linux users: native self-contained x64 and ARM64 archives are produced by CI. See the [Linux guide](docs/linux.md) for artifact availability, system libraries, checksums and the Proton fallback.

## what's here

- Desktop + HOME, with the same library, saves and settings.
- HD landscape backgrounds from metadata. covers stay on game tiles.
- Faster startup, a cached library and quicker repeat game launches.
- MP4 graphics defaults at 1.5× resolution and 16× filtering.
- Low-end GPU and Minimum load modes, with settings for each game.
- Controller reconnect, smoother HOME navigation and a controller touchscreen pointer.
- Themes, avatars, save tools, updates, mods and local translation options.

MP4 still has some stutter. Native Linux gameplay, audio/controllers, physical ARM64 hardware and 3D All-Stars touch controls need more testing. [Test notes](docs/testing.md) have the details.

## build from source

The launcher and core host source are included. The emulator core is restored from pinned public Ryujinx and Kenji-NX inputs, with every exact file checked against the v1 release source manifest. [Source baseline instructions](build/source-baseline/README.md) document the reconstruction; [native Linux instructions](build/linux/README.md) build and verify x64/ARM64 archives.

## the people behind it

**COYE / coye2** — development.

**IL DENTE PROIBITO** and **ShadowEddyx12** — dev team testers. thank you for sticking with the builds and catching the stuff that needed fixing.

[Full credits](CREDITS.md) · [Licenses](LICENSING.md) · [Download checks](SECURITY.md)

if you want to support what i'm doing, [my Ko-fi is coye2](https://ko-fi.com/coye2). appreciate you.
