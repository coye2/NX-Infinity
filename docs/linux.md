# Linux / Proton

1. Download **NX-Infinity-v1.0.0-linux-proton-x64.zip** from [Releases](https://github.com/coye2/NX-Infinity/releases/latest).
2. Extract the ZIP.
3. Run **Install-NX-Infinity.sh**. If your file manager doesn't run scripts, open a terminal in the extracted folder and run `bash Install-NX-Infinity.sh`.

You need x86-64 Linux, Python 3.11 or newer, Vulkan drivers and an internet connection for first setup. No sudo is needed.

Setup puts the app in `~/.local/share/nx-infinity`, adds an app-menu shortcut and keeps your Proton prefix and saves when updating. The first launch downloads Proton support. Run the installer again when installing an update.

For setup diagnostics, run `python3 linux/nx_proton.py --check` from the extracted folder. Launch errors point to `last-launch.log`.

Installation and update checks pass under Ubuntu WSL. Desktop gameplay, controller input and audio still need Linux testing. [Test notes](testing.md).
