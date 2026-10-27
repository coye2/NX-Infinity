# test notes

The v1 build passed 31 local build and test stages, including 57 Desktop/HOME UI checks. The packaged Windows app connected to its core and shut down cleanly. Proton installation, reinstall and save-prefix preservation were checked under Ubuntu WSL.

Native Linux packaging checks build self-contained x64 and ARM64 launcher/core
apphosts, verify complete SHA-256 inventories and architecture-correct ELF
files, and audit x64 dynamic dependencies. Two clean x64 builds from different
absolute source/work roots on the same host produce the same payload and
archive bytes, and an x64 archive compressed with classic zlib carries the same
tar stream; ARM64 is built once and statically verified. A CPU-only Xvfb run
starts the actual x64 Avalonia launcher,
verifies the packaged core handshake, records a clean shutdown, then repeats
after replacing the extracted application directory and checks that external
user data is unchanged. Both normal starts emit strict, hash-bound reports with
fresh timestamped evidence; the probe report rejects unexpected or positive
gameplay claims. It does not initialize a game, Vulkan renderer, audio output,
or controller.

Additional v1 checks cover HD landscape downloads, offline artwork, rejected cover/thumbnail backgrounds, HOME focus, controller drift, diagonal movement and held-input repeat. Nintendo's live metadata returned landscape backgrounds for MP4 and Super Mario 3D All-Stars.

MP4 reaches gameplay with a controller, 1.5× resolution and 16× filtering. Warm scenes reached around 60 FPS on the test PC, but stutters remain. The last gameplay run included a 1.48-second frame interval. The test PC has an i7-14700F, RTX 4060 Ti 16 GB and 32 GB RAM.

Still to test: a full MP4 playthrough, native Linux desktop gameplay, physical
ARM64 hardware, Linux controller/audio/save behavior, physical 3D All-Stars
touch controls, live Discord, and game-window translation/OCR on Windows.
Native Linux packages intentionally omit online avatar browsing and the
translation and OCR features. The navigation checks use controlled controller
samples; they do not replace feedback from different physical controllers.

The release attachments include the audit receipt, ZIP checksums and Defender scan results. Local results are recorded separately from GitHub Actions.
