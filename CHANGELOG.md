# changelog

## Unreleased

- Restored the public launcher and core-host source and added a hash-checked process for reconstructing the v1 core from pinned Ryujinx and Kenji-NX inputs.
- Added CI coverage for core reconstruction, core builds, host publishing and the protocol hello handshake.
- Added deterministic self-contained `linux-x64` and `linux-arm64` archives with native launcher/core apphosts, complete integrity manifests, license/source notices, checksum files and desktop integration.
- Added Linux archive, ELF/dependency and CPU-only Xvfb launcher/worker gates. Packages remain unqualified for gameplay, audio, controllers, saves and physical ARM64 hardware.
- Native Linux packages omit the translation and OCR features, which need the bundled translation runtime, and the online avatar catalog; local avatar import remains.

## Stable Release v1 — 1.0.0 — 26 October 2026

- Backgrounds now use separate HD landscape metadata from Nintendo. Covers stay on game tiles. Missing artwork uses the theme, and downloaded backgrounds work offline.
- Fixed HOME stick drift, diagonal direction changes and held-input repeat. D-pad input takes priority, and controller noise no longer steals navigation.
- Smoothed the HOME game row and kept the rest of the screen in place when moving beyond the visible cover range.
- Kept HOME navigation available during background library maintenance. Settings sliders and open lists now accept directional controller input.
- Updated the download page, setup notes and credits.

- Kept one compiled NX Infinity Core in the full application package.
- Made shader-cache status checks asynchronous. A nonblocking poll no longer waits for the render queue. Repeated polls cannot flood it.
- Kept unchanged sparse-texture tracking during remaps. Large interleaved 3D textures retain valid tracking inside affected slices too, avoiding repeated page protection and allocation work.
- Added MP4's 1.5× resolution / 16× filtering defaults, with per-game overrides.
- Added Low-end GPU and Minimum load modes in Desktop and HOME, with per-game choices and optional texture compression. Modes take priority over title presets. The core confirms every launch setting before the game starts.
- Parallelized large 3D texture conversions and corrected the texture cache using guest memory size instead of host memory size. Texture conversion tests compare exact output bytes against the previous core.
- Fixed the native host detecting a controller without assigning it. Player 1 connects automatically unless explicitly unassigned.
- Added controller and mouse touchscreen input. Both stick clicks toggle the pointer; right stick moves it and A taps.
- Removed the startup folder crawl. Cached games open first and folder watchers track changes.
- Skipped duplicate inspection for unchanged games loaded by the same core revision. File verification and native load checks remain active.
- Stopped stretching tiny Switch icons across the desktop hero. Landscape backgrounds keep their available detail up to 4K.
- Added About NX Infinity, developer credits and Dev Team Testers in both interfaces.
- Fixed Windows/Linux enhancement hash consistency and preserved older Windows profiles.
- Fixed immutable profile collisions after core updates and misleading successful validation end reasons.
- Updated application documentation and Proton setup notes.
- Added release-archive virus verification with SHA-256 hashes and the scanner's engine/signature versions.

NX Infinity is a Ryujinx-derived core fork through Kenji-NX, with extensive repairs and performance improvements and a rebuilt Switch-themed Desktop/HOME interface. Original contributors and Dune's design work remain credited.

Known limits: MP4 reaches gameplay with shader cache enabled but still stutters. The latest measured run included a 1.48-second frame interval. Proton installation checks pass; Linux gameplay and 3D All-Stars touch interaction are not hardware-qualified. See the test status for the audit scope.
