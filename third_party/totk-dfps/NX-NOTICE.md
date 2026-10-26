# NX experimental DFPS++ module

Source: MaxLastBreath/TOTK-DFPS, commit
`6597364efc5dc1efa01a43e702130d94140449f7`, under the included GPL version 2.
The original source incorporates exlaunch and retains its original notices.
`UPSTREAM.json` records the upstream file identities. This directory is the
complete modified corresponding source of the separately loaded guest module.

NX changes: initialize the first timing sample; use writable construction storage
with GCC 15; remove runtime INI parsing and SD logging from the interval hook;
select 60 FPS; reject invalid movie frame rates; remove the duplicate 1.1.2 movie
hook; skip all hooks for unknown versions; migrate NPDM field names for current
switch-tools; restrict the generated program ID range to TOTK; omit FTP deployment.
No game code, keys, firmware or commercial artwork is included.

The launcher enables the module for six cataloged versions (1.0.0, 1.1.0,
1.1.1, 1.1.2, 1.2.0, 1.2.1) when 60 FPS is requested. Version and author NSO
identifier must agree; every mount remains bound to the actual complete 32-byte
main build identity read by the core. Only 1.0.0 has a previously supplied full
identity observation; shorter author identifiers are not represented as verified
full identities. Newer versions are not covered by these offsets.
It uses the existing exact-build native ExeFS mount verification and rejects
conflicting ExeFS mounts. Choosing Original removes the owned mount next launch.
The module is a built-in enhancement, separate from the user mod inventory.

This is experimental. Compilation and controlled timing tests do not verify the
hook offsets against retail instruction bytes, physics, audio, saving, loading,
cutscenes, or sustained 60 FPS on any GPU. No stable-60 guarantee is made.

Rebuild with devkitPro devkita64 image `20260219` (GCC 15.2.0), libnx and switch-tools:

```sh
make -C third_party/totk-dfps -j2
```

`DEVKITPRO` must point to the toolchain root and its `tools/bin` must be on PATH.
Output is `deploy/DFPS++/exefs/{subsdk9,main.npdm}`. The launcher embeds base64
copies under `launcher/NxInfinity/TimingModules/totk-100`, with SHA-256 values in its
manifest. Both base64 files and manifest must be regenerated together after any
module change. The complete source accompanies test distributions; original
upstream remains at <https://github.com/MaxLastBreath/TOTK-DFPS>.
