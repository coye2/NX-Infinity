# Native Linux

Native self-contained archives are built for `linux-x64` and `linux-arm64`.
They run the Avalonia launcher and NX Infinity Core directly; Wine and Proton
are not used.

The current stable v1 release predates these archives. Until a maintainer
publishes them on [Releases](https://github.com/coye2/NX-Infinity/releases/latest),
use the `nx-infinity-native-linux` artifact from a successful Checks workflow
or [build it from source](../build/linux/README.md).

## Run the portable archive

1. Choose the archive matching your machine: `linux-x64` for ordinary AMD/Intel
   PCs, or `linux-arm64` for 64-bit ARM systems.
2. Verify the adjacent `.sha256` file: `sha256sum -c <archive>.sha256`.
3. Extract the archive and run `./nx-infinity` from its top-level directory.
4. Optional: run `./install-desktop.sh` after moving the extracted directory
   to its permanent location. This creates an app-menu entry without `sudo`.

The archive contains its .NET runtime. The host system must provide:

- a desktop X11 session (Wayland works through XWayland);
- glibc;
- the GCC runtime libraries `libstdc++.so.6` and `libgcc_s.so.1`;
- Fontconfig;
- `libopenal.so.1`;
- `libvulkan.so.1` and a Vulkan 1.2-capable driver.

The bundled native libraries require glibc 2.38 or newer on `linux-x64` and
glibc 2.34 or newer on `linux-arm64`. Ubuntu 24.04 satisfies the x64 floor.

For Ubuntu 24.04, the non-driver libraries are available through
`libstdc++6`, `libgcc-s1`, `libfontconfig1`, `libopenal1`, and `libvulkan1`.
Your GPU vendor supplies the Vulkan driver.

User data is stored outside the extracted application directory under the
platform local-data folder (`~/.local/share/NX Infinity` by default). Replacing
the application directory does not replace saves, profiles, keys, firmware, or
settings. The Checks workflow runs the packaged launcher before and after replacing its extracted
application directory and verifies an external data marker byte-for-byte. Keep
your own backups before changing emulator or game data.

Online avatar browsing is not offered in native Linux packages; local avatar
import and profiles remain available. Native Linux packages do not include the translation
runtime, and game-window OCR capture is Windows-only, so the translation
settings are not shown. The launcher does not require Python or Tesseract.

## Qualification status

The Checks workflow builds and verifies both architectures, complete file/hash manifests, ELF architecture
and native dependencies, and an x64 Xvfb launcher/worker handshake. Xvfb is a
CPU-only virtual display. These checks do not qualify Vulkan gameplay, audio,
controllers, saves, firmware, keys, or physical ARM64 hardware. Packages remain
marked `unqualified` until those manual checks are recorded.

## Proton fallback

The stable v1 release still offers
**NX-Infinity-v1.0.0-linux-proton-x64.zip**. Extract it and run
`Install-NX-Infinity.sh`. It requires x86-64 Linux, Python 3.11 or newer,
Vulkan drivers, and an internet connection for its first setup. Proton remains
a fallback while native packages complete hardware qualification.

See [test notes](testing.md) for current limits.
