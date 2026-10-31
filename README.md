<p align="center"><img src="assets/nx-infinity.png" width="260" alt="NX Infinity"></p>

# NX Infinity

**Stable Release v1 · 1.0.0**

my take on a Switch emulator. an entirely remade Switch themed UI, with Desktop when you're at your desk and HOME when you're on a controller.

NX Infinity is a fork of the Ryujinx core through Kenji-NX, with extensive repairs and performance improvements. huge credit to the Ryujinx and Kenji-NX teams for the foundation this is built on.

**[Windows download](https://github.com/coye2/NX-Infinity/releases/latest)** · **[Linux / Proton](docs/linux.md)** · **[What's changed](CHANGELOG.md)**

## get started

Windows: download **NX-Infinity-v1.0.0-win-x64.zip** from [Releases](https://github.com/coye2/NX-Infinity/releases/latest), extract it into a new folder and open **NX Infinity.exe**. keep the folders together. add your games, keys and firmware through setup. Games, keys and firmware are supplied by you.

Linux / Proton: download **NX-Infinity-v1.0.0-linux-proton-x64.zip**, extract it and run `bash Install-NX-Infinity.sh` from the extracted folder. You need x86-64 Linux, Python 3.11+, Vulkan drivers and internet access for first setup. setup adds NX Infinity to your app menu and keeps your Proton prefix and saves when you update. See the [Linux setup guide](docs/linux.md).

The release includes SHA-256 checksums, an audit receipt and Defender scan results. See [download checks](SECURITY.md) before reporting a download or scanner issue.

## what's here

- Desktop + HOME, with the same library, saves and settings.
- HD landscape backgrounds from metadata. covers stay on game tiles.
- Faster startup, a cached library and quicker repeat game launches.
- MP4 graphics defaults at 1.5× resolution and 16× filtering.
- Low-end GPU and Minimum load modes, with settings for each game.
- Controller reconnect, smoother HOME navigation and a controller touchscreen pointer.
- Themes, avatars, save tools, updates, mods and local translation options.

MP4 still stutters. Proton installation, reinstall and save-prefix preservation were checked under Ubuntu WSL; Linux desktop gameplay, controller input and audio still need testing. Physical 3D All-Stars touch controls also need more testing. [Test notes](docs/testing.md) have the details.

## build from source

The launcher and core host source are included. The emulator core is restored from pinned public Ryujinx and Kenji-NX inputs, with every exact file checked against the v1 release source manifest. [Source baseline instructions](build/source-baseline/README.md) document the inputs, reconstruction boundary and build commands.

## the people behind it

**COYE / coye2** — development.

**IL DENTE PROIBITO** and **ShadowEddyx12** — dev team testers. thank you for sticking with the builds and catching the stuff that needed fixing.

**sakasakiking / Dune** — desktop design origins.

[Full credits](CREDITS.md) · [Licenses](LICENSING.md) · [Download checks](SECURITY.md)

if you want to support what i'm doing, [my Ko-fi is coye2](https://ko-fi.com/coye2). appreciate you.
