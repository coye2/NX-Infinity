#!/usr/bin/env python3
"""Create deterministic corresponding-source bundles for redistributed binaries."""

from __future__ import annotations

import os
import pathlib
import re
import shutil
import subprocess
from dataclasses import dataclass

import common


@dataclass(frozen=True)
class FfmpegSource:
    revision: str
    archive_name: str
    archive_sha256: str
    configuration: str
    configuration_sha256: str


FFMPEG_SOURCES = {
    "linux-x64": FfmpegSource(
        "39cac587c4f78f7c26e0f0e5da664943d4e9fade",
        "ffmpeg-39cac587c4-source.tar",
        "7cdc5c54068202f40f4bdcc344f4912db3e4defc95238d7dd9c30926c17dd9d4",
        "--target-os=linux --disable-debug --disable-all --disable-autodetect "
        "--enable-shared --enable-avcodec --enable-avutil "
        "--enable-decoder='h264,vp8,vp9' --prefix=bin/build",
        "3fa99c8eebc95c729c30f4dbf9d406500b8d7dbdb206ae924246da28bd59ba99",
    ),
    "linux-arm64": FfmpegSource(
        "e16ff06adb9acd647c0c33aa3c27438e1009b822",
        "ffmpeg-e16ff06adb-source.tar",
        "42cad98c27584ad67d4517d996f4b561e1ed2a0ff9f44e70d2e5be855add9e66",
        "--target-os=linux --arch=arm64 --disable-debug --disable-all "
        "--disable-autodetect --enable-shared --enable-avcodec --enable-avutil "
        "--enable-decoder='h264,vp8,vp9' --prefix=bin/build",
        "6aeae7e5468efec76be381d8007d4f9e61778973dd7eb302682bb2b2a6cff0be",
    ),
}
CONFIGURATION_NAME = "FFmpeg-BUILD-CONFIGURATION.txt"
ARCHIVE_SETTINGS = (
    "core.autocrlf=false",
    "core.eol=lf",
    "core.attributesFile=/dev/null",
    "core.bigFileThreshold=512m",
    "core.useReplaceRefs=false",
    "tar.umask=0002",
)
MINIMUM_GIT_VERSION = (2, 45)
GIT_VERSION = re.compile(r"git version (\d+)\.(\d+)")


def git_environment() -> dict[str, str]:
    """Ignore inherited git variables, user and system configuration, and lazy fetches."""
    environment = {name: value for name, value in os.environ.items() if not name.startswith("GIT_")}
    environment.update(
        {
            "GIT_ATTR_NOSYSTEM": "1",
            "GIT_CONFIG_GLOBAL": "/dev/null",
            "GIT_CONFIG_NOSYSTEM": "1",
            "GIT_NO_LAZY_FETCH": "1",
        }
    )
    return environment


def git_command(repository: pathlib.Path, *arguments: str) -> list[str]:
    settings = [part for setting in ARCHIVE_SETTINGS for part in ("-c", setting)]
    return [
        "git", "--no-replace-objects", "--no-lazy-fetch", "-C", str(repository), *settings, *arguments
    ]


def require_supported_git() -> None:
    """The --no-lazy-fetch option, which keeps archiving offline, first appears in git 2.45."""
    output = subprocess.check_output(["git", "--version"], env=git_environment(), text=True)
    match = GIT_VERSION.match(output)
    if match is None or tuple(map(int, match.groups())) < MINIMUM_GIT_VERSION:
        raise ValueError(f"FFmpeg source archiving needs git 2.45 or later: {output.strip()}")


def require_identity(path: pathlib.Path, expected: str, description: str) -> None:
    if common.sha256_file(path) != expected:
        path.unlink()
        raise ValueError(f"FFmpeg {description} identity mismatch.")


def write_git_stream(command: list[str], destination: pathlib.Path) -> None:
    with destination.open("xb") as raw:
        try:
            with subprocess.Popen(command, stdout=subprocess.PIPE, env=git_environment()) as process:
                shutil.copyfileobj(process.stdout, raw, 1024 * 1024)
            if process.returncode != 0:
                raise subprocess.CalledProcessError(process.returncode, command)
        except BaseException:
            destination.unlink(missing_ok=True)
            raise


def require_no_local_attributes(repository: pathlib.Path) -> None:
    relative = subprocess.check_output(
        git_command(repository, "rev-parse", "--git-path", "info/attributes"),
        env=git_environment(),
        text=True,
    ).strip()
    path = pathlib.Path(relative)
    if not path.is_absolute():
        path = repository / path
    if path.is_symlink() or (path.exists() and (not path.is_file() or path.stat().st_size)):
        raise ValueError(f"FFmpeg source repository has local archive attributes: {path}")


def add_ffmpeg_source(
    root: pathlib.Path, repository: pathlib.Path, rid: str
) -> None:
    if rid not in FFMPEG_SOURCES:
        raise ValueError(f"Unsupported FFmpeg source RID: {rid}")
    source = FFMPEG_SOURCES[rid]
    repository = repository.absolute()
    if repository.is_symlink() or not repository.is_dir():
        raise ValueError(f"FFmpeg source repository is unavailable: {repository}")
    require_supported_git()
    resolved = subprocess.check_output(
        git_command(repository, "rev-parse", f"{source.revision}^{{commit}}"),
        env=git_environment(),
        text=True,
    ).strip()
    if resolved != source.revision:
        raise ValueError("FFmpeg source repository does not contain the pinned revision.")
    require_no_local_attributes(repository)
    destination = root / "licenses/sources" / source.archive_name
    destination.parent.mkdir(parents=True, exist_ok=True)
    command = git_command(
        repository,
        "archive",
        "--format=tar",
        f"--prefix=ffmpeg-{source.revision[:10]}/",
        source.revision,
    )
    write_git_stream(command, destination)
    destination.chmod(0o644)
    require_identity(destination, source.archive_sha256, "corresponding-source archive")
    configuration = root / "licenses/sources" / CONFIGURATION_NAME
    with configuration.open("x", encoding="utf-8", newline="\n") as stream:
        stream.write(f"revision={source.revision}\nconfigure={source.configuration}\n")
    configuration.chmod(0o644)
    require_identity(configuration, source.configuration_sha256, "build-configuration")
