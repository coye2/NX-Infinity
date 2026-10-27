#!/usr/bin/env python3
"""Assemble user-facing files and notices for native Linux archives."""

from __future__ import annotations

import pathlib
import posixpath
import re
import shutil
import subprocess
import zipfile

import common


HERE = pathlib.Path(__file__).resolve().parent
REPOSITORY = HERE.parents[1]
PACKAGE_ONLY_NOTICES = {"Package-MIT.txt", "Shaderc.net-MIT.txt"}
PACKAGED_DOCUMENTS = (
    ("docs/linux.md", "README-LINUX.md"),
    ("LICENSING.md", "licenses/LICENSING.md"),
    ("THIRD-PARTY-NOTICES.md", "licenses/THIRD-PARTY-NOTICES.md"),
)
REPOSITORY_LOCATIONS = (
    ("LICENSE", "licenses/LICENSE"),
    ("LICENSING.md", "licenses/LICENSING.md"),
    ("THIRD-PARTY-NOTICES.md", "licenses/THIRD-PARTY-NOTICES.md"),
    ("docs/linux.md", "README-LINUX.md"),
    ("licenses", "licenses/repository"),
    ("third_party/totk-dfps", "licenses/totk-dfps-source"),
)
INLINE_LINK = re.compile(r"\[([^\]]+)\]\(([^)\s]+)\)")
NOTICE_SOURCES = (
    ("avalonia.desktop", "12.1.4", "Avalonia.Desktop.nuspec", "Avalonia.Desktop-12.1.4.nuspec"),
    ("avalonia.fonts.inter", "12.1.4", "Avalonia.Fonts.Inter.nuspec", "Avalonia.Fonts.Inter-12.1.4.nuspec"),
    ("concentus", "2.2.2", "LICENSE", "Concentus-2.2.2-LICENSE.txt"),
    ("skiasharp.nativeassets.linux", "3.119.4", "LICENSE.txt", "SkiaSharp-3.119.4-LICENSE.txt"),
    (
        "skiasharp.nativeassets.linux",
        "3.119.4",
        "THIRD-PARTY-NOTICES.txt",
        "SkiaSharp-3.119.4-THIRD-PARTY-NOTICES.txt",
    ),
    ("harfbuzzsharp.nativeassets.linux", "8.3.1.3", "LICENSE.txt", "HarfBuzzSharp-8.3.1.3-LICENSE.txt"),
    (
        "harfbuzzsharp.nativeassets.linux",
        "8.3.1.3",
        "THIRD-PARTY-NOTICES.txt",
        "HarfBuzzSharp-8.3.1.3-THIRD-PARTY-NOTICES.txt",
    ),
    ("skiasharp.nativeassets.linux", "2.88.9", "LICENSE.txt", "SkiaSharp-2.88.9-LICENSE.txt"),
    (
        "skiasharp.nativeassets.linux",
        "2.88.9",
        "THIRD-PARTY-NOTICES.txt",
        "SkiaSharp-2.88.9-THIRD-PARTY-NOTICES.txt",
    ),
    ("opentk.redist.glfw", "3.4.0.44", "COPYING.md", "GLFW-3.4.0.44-COPYING.md"),
    ("ryujinx.sdl3-cs", "2026.918.0", "Ryujinx.SDL3-CS.nuspec", "SDL3-CS-2026.918.0.nuspec"),
    ("ryujinx.sdl3-cs", "2026.918.0", "README_nuget.md", "SDL3-CS-2026.918.0-README.md"),
    (
        "silk.net.shaderc.native",
        "2.22.0",
        "Silk.NET.Shaderc.Native.nuspec",
        "Shaderc-2.22.0.nuspec",
    ),
    ("silk.net.shaderc.native", "2.22.0", "README.md", "Shaderc-2.22.0-README.md"),
    (
        "ryujinx.graphics.nvdec.dependencies.linux",
        "6.1.2-build4",
        "Ryujinx.Graphics.Nvdec.Dependencies.Linux.nuspec",
        "FFmpeg-6.1.2-build4.nuspec",
    ),
    (
        "ryujinx.graphics.nvdec.dependencies.linux",
        "6.1.2-build4",
        "README.md",
        "FFmpeg-6.1.2-build4-README.md",
    ),
)
PACKAGE_HASHES = {
    ("avalonia.desktop", "12.1.4"): "5da57df4f56d32f3894475da869d3555a968085f5d93b73b3f5ae89f70a5cd58",
    ("avalonia.fonts.inter", "12.1.4"): "b6d9b4c4a6097099629553b400851e12127677c14118e82c042ff7aed37a548b",
    ("concentus", "2.2.2"): "f487f890bef65757af1cecf420e357f00488cbfac0b409ba461ccbceef81481f",
    ("skiasharp.nativeassets.linux", "3.119.4"): "fae0554059b1107ef7888e46c20bdfb548401ef7a7a6f7391ad4fadc7432d50a",
    ("harfbuzzsharp.nativeassets.linux", "8.3.1.3"): "7de58e9daffc9dcbe6aeaec2c670dccefd5f69c576a68655e51f8ec82b687295",
    ("skiasharp.nativeassets.linux", "2.88.9"): "990fe805aa9147bd567d2eba9890af71cdee296ec23441e83cdd898a50dbc3f0",
    ("opentk.redist.glfw", "3.4.0.44"): "c505bfd32f258371ced5f08e5531c124449f60e5625fa6332ba17f2df5861ad5",
    ("ryujinx.sdl3-cs", "2026.918.0"): "fa14f44eb270364a5a2b8c1e23e93d81d6621218a8684de85587df7e1464c79b",
    ("silk.net.shaderc.native", "2.22.0"): "7b80ff4f11497b81e73b27017352994817f44dcf79ecaab8c6506c3645ec59b8",
    (
        "ryujinx.graphics.nvdec.dependencies.linux",
        "6.1.2-build4",
    ): "0351007f097b75328a5c64b467682b50c9337848d13712ed7462861b0333453c",
}


def copy_tree(source: pathlib.Path, destination: pathlib.Path) -> None:
    if source.is_symlink() or not source.is_dir():
        raise ValueError(f"Publish output is unavailable: {source}")
    common.regular_files(source)
    shutil.copytree(source, destination, copy_function=shutil.copy2, symlinks=True)
    common.regular_files(destination)


def add_distribution_files(
    root: pathlib.Path,
    dotnet_license: pathlib.Path | None = None,
    dotnet_notices: pathlib.Path | None = None,
    nuget_root: pathlib.Path | None = None,
    external_notices: pathlib.Path | None = None,
) -> None:
    write_text(
        root / "nx-infinity",
        "#!/bin/sh\nset -eu\nroot=$(CDPATH='' cd -- \"$(dirname -- \"$0\")\" && pwd -P)\n"
        'export LD_LIBRARY_PATH="$root/cores/nxi${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"\n'
        'exec "$root/NxInfinity" --install-root "$root" "$@"\n',
    )
    write_text(
        root / "share/applications/nx-infinity.desktop",
        "[Desktop Entry]\nType=Application\nVersion=1.0\nName=NX Infinity\n"
        "Comment=NX Infinity emulator frontend\nExec=nx-infinity\nTryExec=nx-infinity\n"
        "Icon=nx-infinity\nTerminal=false\nCategories=Game;Emulator;\n",
    )
    write_text(root / "install-desktop.sh", desktop_installer())
    copy_file(
        REPOSITORY / "assets/nx-infinity.png",
        root / "share/icons/hicolor/256x256/apps/nx-infinity.png",
    )
    copy_file(REPOSITORY / "LICENSE", root / "licenses/LICENSE")
    for source, destination in PACKAGED_DOCUMENTS:
        write_text(root / destination, packaged_markdown(source, destination))
    copy_file(HERE / "NATIVE-DEPENDENCIES.json", root / "licenses/NATIVE-DEPENDENCIES.json")
    copy_file(HERE / "NOTICE-SOURCES.json", root / "licenses/native/NOTICE-SOURCES.json")
    if dotnet_license is not None:
        copy_file(dotnet_license, root / "licenses/native/dotnet-LICENSE.txt")
    if dotnet_notices is not None:
        copy_file(dotnet_notices, root / "licenses/native/dotnet-THIRD-PARTY-NOTICES.txt")
    if nuget_root is not None:
        copy_native_notices(root, nuget_root)
    copy_notice_directory(root)
    if external_notices is not None:
        copy_external_notices(root, external_notices)
    copy_tracked("licenses", root / "licenses/repository")
    copy_tracked("third_party/totk-dfps", root / "licenses/totk-dfps-source")


def desktop_installer() -> str:
    return (
        "#!/bin/sh\nset -eu\nroot=$(CDPATH='' cd -- \"$(dirname -- \"$0\")\" && pwd -P)\n"
        'clean=$(printf %s "$root" | LC_ALL=C tr -d \'\\000-\\037\\177\')\n'
        '[ "$clean" = "$root" ] || { printf "%s\\n" "Install path contains control characters." >&2; exit 1; }\n'
        'applications=${XDG_DATA_HOME:-"$HOME/.local/share"}/applications\n'
        'mkdir -p "$applications"\n'
        '# Literal sed patterns intentionally do not expand in this shell.\n'
        '# shellcheck disable=SC2016\n'
        'escape_exec() { printf %s "$1" | sed \'s/\\\\/\\\\\\\\/g; s/"/\\\\"/g; s/`/\\\\`/g; s/\\$/\\\\$/g; s/%/%%/g\'; }\n'
        'escape_value() { printf %s "$1" | sed \'s/\\\\/\\\\\\\\/g\'; }\n'
        'launcher=$(escape_exec "$root/nx-infinity")\n'
        'icon=$(escape_value "$root/share/icons/hicolor/256x256/apps/nx-infinity.png")\n'
        'temporary=$(mktemp "$applications/.nx-infinity.desktop.XXXXXX")\n'
        'trap \'rm -f "$temporary"\' EXIT HUP INT TERM\n'
        '{\n'
        '  printf "%s\\n" "[Desktop Entry]" "Type=Application" "Version=1.0" "Name=NX Infinity"\n'
        '  printf "Exec=\\"%s\\"\\nIcon=%s\\n" "$launcher" "$icon"\n'
        '  printf "%s\\n" "Terminal=false" "Categories=Game;Emulator;"\n'
        '} >"$temporary"\n'
        'chmod 0644 "$temporary"\n'
        'mv -f "$temporary" "$applications/nx-infinity.desktop"\n'
        'trap - EXIT HUP INT TERM\n'
        'command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$applications" || true\n'
        'printf "Installed %s\\n" "$applications/nx-infinity.desktop"\n'
    )


def packaged_markdown(source: str, destination: str) -> str:
    """Return a repository document with its relative links retargeted to the package layout."""
    path = REPOSITORY / source
    if path.is_symlink() or not path.is_file():
        raise ValueError(f"Required distribution source is unavailable: {path}")
    text = path.read_text(encoding="utf-8")
    source_directory = posixpath.dirname(source)
    destination_directory = posixpath.dirname(destination) or "."

    def retarget(match: re.Match[str]) -> str:
        label, target = match.groups()
        if common.EXTERNAL_LINK.match(target) or target.startswith("#"):
            return match.group(0)
        location, separator, fragment = target.partition("#")
        repository_path = posixpath.normpath(posixpath.join(source_directory, location))
        packaged = packaged_location(repository_path)
        if packaged is None:
            return f"{label} (`{repository_path}` in the NX Infinity source repository)"
        link = posixpath.relpath(packaged, destination_directory)
        if location.endswith("/"):
            link += "/"
        return f"[{label}]({link}{separator}{fragment})"

    return INLINE_LINK.sub(retarget, text)


def packaged_location(repository_path: str) -> str | None:
    for repository, packaged in REPOSITORY_LOCATIONS:
        if repository_path == repository:
            return packaged
        if repository_path.startswith(repository + "/"):
            return packaged + repository_path[len(repository):]
    return None


def copy_native_notices(root: pathlib.Path, nuget_root: pathlib.Path) -> None:
    packages: dict[tuple[str, str], pathlib.Path] = {}
    for (package_id, version), expected in PACKAGE_HASHES.items():
        package = nuget_root / package_id / version / f"{package_id}.{version}.nupkg"
        if package.is_symlink() or not package.is_file() or common.sha256_file(package) != expected:
            raise ValueError(f"NuGet package identity mismatch: {package_id}/{version}")
        packages[(package_id, version)] = package
    for package_id, version, source, destination in NOTICE_SOURCES:
        data = read_package_member(packages[(package_id, version)], source)
        write_bytes(root / "licenses/native" / destination, data)


def read_package_member(package: pathlib.Path, name: str) -> bytes:
    with zipfile.ZipFile(package) as archive:
        matches = [item for item in archive.infolist() if item.filename == name]
        if len(matches) != 1 or matches[0].is_dir():
            raise ValueError(f"NuGet package notice is missing or ambiguous: {name}")
        if matches[0].file_size > 2 * 1024 * 1024:
            raise ValueError(f"NuGet package notice exceeds the size bound: {name}")
        return archive.read(matches[0])


def copy_notice_directory(root: pathlib.Path) -> None:
    source = HERE / "notices"
    for path in common.regular_files(source):
        if path.name in PACKAGE_ONLY_NOTICES:
            continue
        relative = path.relative_to(source)
        copy_file(path, root / "licenses/native" / relative)


def copy_external_notices(root: pathlib.Path, source: pathlib.Path) -> None:
    if source.is_symlink() or not source.is_dir():
        raise ValueError(f"External notice directory is unavailable: {source}")
    files = common.regular_files(source)
    if not files:
        raise ValueError("External notice directory is empty.")
    for path in files:
        relative = path.relative_to(source)
        if len(relative.parts) != 1:
            raise ValueError(f"External notice must be a file name: {relative}")
        copy_file(path, root / "licenses/native" / relative)


def copy_tracked(prefix: str, destination: pathlib.Path) -> None:
    output = subprocess.check_output(
        ["git", "-C", str(REPOSITORY), "ls-files", "-z", "--", prefix]
    )
    for raw in output.split(b"\0"):
        if not raw:
            continue
        relative = raw.decode("utf-8")
        suffix = pathlib.PurePosixPath(relative).relative_to(prefix)
        copy_file(REPOSITORY / relative, destination.joinpath(*suffix.parts))


def copy_file(source: pathlib.Path, destination: pathlib.Path) -> None:
    if source.is_symlink() or not source.is_file():
        raise ValueError(f"Required distribution source is unavailable: {source}")
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(source, destination)


def write_bytes(path: pathlib.Path, value: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(value)


def write_text(path: pathlib.Path, value: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(value, encoding="utf-8", newline="\n")


def normalize_modes(root: pathlib.Path) -> None:
    for directory in (root, *root.rglob("*")):
        if directory.is_dir():
            directory.chmod(0o755)
    for path in common.regular_files(root):
        path.chmod(0o644)
    executables = (
        "NxInfinity",
        "createdump",
        "nx-infinity",
        "install-desktop.sh",
        "cores/nxi/NxInfinity.Core",
        "cores/nxi/createdump",
    )
    for relative in executables:
        path = root / relative
        if not path.is_file():
            raise ValueError(f"Required executable is unavailable: {relative}")
        path.chmod(0o755)
