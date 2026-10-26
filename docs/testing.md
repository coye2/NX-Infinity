# test notes

The v1 build passed 31 local build and test stages, including 57 Desktop/HOME UI checks. The packaged Windows app connected to its core and shut down cleanly. Proton installation, reinstall and save-prefix preservation were checked under Ubuntu WSL.

Additional v1 checks cover HD landscape downloads, offline artwork, rejected cover/thumbnail backgrounds, HOME focus, controller drift, diagonal movement and held-input repeat. Nintendo's live metadata returned landscape backgrounds for MP4 and Super Mario 3D All-Stars.

MP4 reaches gameplay with a controller, 1.5× resolution and 16× filtering. Warm scenes reached around 60 FPS on the test PC, but stutters remain. The last gameplay run included a 1.48-second frame interval. The test PC has an i7-14700F, RTX 4060 Ti 16 GB and 32 GB RAM.

Still to test: a full MP4 playthrough, Linux desktop gameplay, physical 3D All-Stars touch controls, physical audio, live Discord and game-window translation/OCR. The navigation checks use controlled controller samples; they don't replace feedback from different physical controllers.

The release attachments include the audit receipt, ZIP checksums and Defender scan results. Local results are recorded separately from GitHub Actions.
