#!/usr/bin/env python3
"""Publish and assemble reproducible native NX Infinity Linux archives."""

from __future__ import annotations

import argparse
import gzip
import os
import pathlib
import subprocess
from dataclasses import dataclass
from typing import Sequence

import common
import package_notices
import source_bundle
from distribution import add_distribution_files, copy_tree, normalize_modes


HERE = pathlib.Path(__file__).resolve().parent
REPOSITORY = HERE.parents[1]
VERSION = "1.0.0"
SOURCE_BASELINE = "b26ba273dc83fb757b2548519f1ee44e3d4d6df2"
CORE_REVISION = "67591deee07ab2ce86fc9b72c42e8b64c95ba1b3"
BUILD_MANIFEST = "NX-LINUX-BUILD.json"
BACKEND_MANIFEST = "backend.json"
SYSTEM_DEPENDENCIES = (
    "glibc (x64 2.38 or newer, ARM64 2.34 or newer)",
    "the GCC runtime libraries libstdc++.so.6 and libgcc_s.so.1",
    "fontconfig",
    "libopenal.so.1",
    "libvulkan.so.1 and a Vulkan 1.2-capable driver",
    "an X11 desktop session (Wayland through XWayland is supported)",
)


@dataclass(frozen=True)
class BuildInputs:
    rid: str
    dotnet: pathlib.Path
    core_dotnet: pathlib.Path
    core_source: pathlib.Path
    output: pathlib.Path
    work: pathlib.Path
    notice_sources: pathlib.Path
    ffmpeg_source: pathlib.Path
    epoch: int


def write_backend_manifest(core: pathlib.Path, rid: str) -> common.JsonObject:
    require_rid(rid)
    entry = core / "NxInfinity.Core"
    if not entry.is_file() or entry.is_symlink():
        raise ValueError("Native core publication is missing NxInfinity.Core.")
    if common.elf_machine_file(entry) != common.SUPPORTED_RIDS[rid]:
        raise ValueError(f"Native core apphost does not match {rid}.")
    files = common.inventory(core, (BACKEND_MANIFEST,))
    if not files:
        raise ValueError("Native core publication is empty.")
    value: common.JsonObject = {
        "backendId": "nxi",
        "backendName": "NX Infinity Core",
        "entryPoint": "NxInfinity.Core",
        "files": {path: metadata["sha256"] for path, metadata in files.items()},
        "managedAssembly": False,
        "manifestVersion": 1,
        "qualification": "unqualified",
        "revision": CORE_REVISION,
        "sourceUrl": "https://github.com/coye2/NX-Infinity",
    }
    path = core / BACKEND_MANIFEST
    path.write_bytes(common.canonical_json(value))
    path.chmod(0o644)
    return value


def write_package_manifest(root: pathlib.Path, rid: str, epoch: int) -> common.JsonObject:
    require_rid(rid)
    if epoch < 1:
        raise ValueError("SOURCE_DATE_EPOCH must be a positive integer.")
    files = common.inventory(root, (BUILD_MANIFEST,))
    value: common.JsonObject = {
        "application": "NX Infinity",
        "backendRevision": CORE_REVISION,
        "files": files,
        "qualification": "unqualified",
        "runtimeIdentifier": rid,
        "schemaVersion": 1,
        "sourceBaseline": SOURCE_BASELINE,
        "sourceDateEpoch": epoch,
        "systemDependencies": list(SYSTEM_DEPENDENCIES),
        "version": VERSION,
    }
    path = root / BUILD_MANIFEST
    path.write_bytes(common.canonical_json(value))
    path.chmod(0o644)
    return value


def create_archive(root: pathlib.Path, destination: pathlib.Path, epoch: int) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    if destination.exists() or destination.is_symlink():
        raise FileExistsError(f"Archive destination already exists: {destination}")
    with destination.open("xb") as raw:
        with gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=epoch, compresslevel=6) as compressed:
            common.write_canonical_tar(root, compressed, epoch)


def publish(dotnet: pathlib.Path, project: pathlib.Path, rid: str, output: pathlib.Path,
            artifacts: pathlib.Path | None, properties: Sequence[str] = ()) -> None:
    command = [
        str(dotnet),
        "publish",
        str(project),
        "-c",
        "Release",
        "-r",
        rid,
        "--self-contained",
        "true",
        "-o",
        str(output),
        "-p:ContinuousIntegrationBuild=true",
        "-p:DebugSymbols=false",
        "-p:DebugType=None",
        "-p:PublishSingleFile=false",
        "-p:PublishTrimmed=false",
        "-p:UseAppHost=true",
        f"-p:PathMap={REPOSITORY}=/_/nx-infinity%2C{output.parent}=/_/build",
        *properties,
    ]
    if artifacts is not None:
        command[command.index("-o"):command.index("-o")] = ["--artifacts-path", str(artifacts)]
    process = subprocess.Popen(command, cwd=REPOSITORY / "src")
    enforce_required_nice(process.pid, f"dotnet publish {project.name}")
    if process.wait() != 0:
        raise subprocess.CalledProcessError(process.returncode, command)


def build_core(core_dotnet: pathlib.Path, core_source: pathlib.Path, rid: str) -> None:
    projects = (
        "src/Ryujinx.HLE/Ryujinx.HLE.csproj",
        "src/Ryujinx.Graphics.Vulkan/Ryujinx.Graphics.Vulkan.csproj",
        "src/Ryujinx.Audio.Backends.OpenAL/Ryujinx.Audio.Backends.OpenAL.csproj",
    )
    for runtime in (None, rid):
        for relative in projects:
            command = core_build_command(core_dotnet, core_source, relative, runtime)
            process = subprocess.Popen(command, cwd=core_source)
            suffix = f" ({runtime})" if runtime is not None else ""
            enforce_required_nice(process.pid, f"dotnet build {pathlib.Path(relative).stem}{suffix}")
            if process.wait() != 0:
                raise subprocess.CalledProcessError(process.returncode, command)


def core_build_command(
    dotnet: pathlib.Path,
    source: pathlib.Path,
    project: str,
    runtime: str | None,
) -> list[str]:
    command = [
        str(dotnet),
        "build",
        str(source / project),
        "-c",
        "Release",
        "--no-incremental",
    ]
    if runtime is not None:
        command.extend(["-r", runtime])
    command.extend(
        [
            "-p:ContinuousIntegrationBuild=true",
            "-p:Deterministic=true",
            "-p:DebugSymbols=false",
            "-p:DebugType=None",
            f"-p:PathMap={source}=/_/nx-core",
        ]
    )
    return command


def build(args: argparse.Namespace) -> pathlib.Path:
    enforce_required_nice(os.getpid(), "Linux packager")
    inputs = build_inputs(args)
    app_publish, core_publish = publish_products(inputs)
    return assemble_archive(inputs, app_publish, core_publish)


def build_inputs(args: argparse.Namespace) -> BuildInputs:
    inputs = BuildInputs(
        require_rid(args.rid),
        pathlib.Path(args.dotnet).resolve(),
        pathlib.Path(args.core_dotnet).resolve(),
        pathlib.Path(args.core_source).resolve(),
        pathlib.Path(args.output).resolve(),
        pathlib.Path(args.work).resolve(),
        pathlib.Path(args.notice_sources).resolve(),
        pathlib.Path(args.ffmpeg_source).resolve(),
        args.epoch,
    )
    require_file(inputs.dotnet, "dotnet executable")
    require_file(inputs.core_dotnet, "Core dotnet executable")
    if not inputs.core_source.is_dir() or inputs.core_source.is_symlink():
        raise ValueError(f"Restored core source is unavailable: {inputs.core_source}")
    if not inputs.notice_sources.is_dir() or inputs.notice_sources.is_symlink():
        raise ValueError(f"External notice sources are unavailable: {inputs.notice_sources}")
    if not inputs.ffmpeg_source.is_dir() or inputs.ffmpeg_source.is_symlink():
        raise ValueError(f"FFmpeg source repository is unavailable: {inputs.ffmpeg_source}")
    if inputs.work.exists() or inputs.work.is_symlink():
        raise FileExistsError(f"Build work directory must not already exist: {inputs.work}")
    inputs.work.mkdir(parents=True)
    inputs.output.mkdir(parents=True, exist_ok=True)
    return inputs


def publish_products(inputs: BuildInputs) -> tuple[pathlib.Path, pathlib.Path]:
    build_core(inputs.core_dotnet, inputs.core_source, inputs.rid)
    remove_core_debug_artifacts(inputs.core_source, inputs.rid)
    app_publish = inputs.work / "app-publish"
    core_publish = inputs.work / "core-publish"
    publish(
        inputs.dotnet,
        REPOSITORY / "src/app/NxInfinity/NxInfinity.csproj",
        inputs.rid,
        app_publish,
        inputs.work / "app-artifacts",
    )
    publish(
        inputs.dotnet,
        REPOSITORY / "src/core/NxInfinity.Core/NxInfinity.Core.csproj",
        inputs.rid,
        core_publish,
        None,
        (
            f"-p:CoreSourceRoot={inputs.core_source}",
            f"-p:NxiHostArtifactsRoot={inputs.work / 'core-artifacts'}",
        ),
    )
    return app_publish, core_publish


def remove_core_debug_artifacts(core_source: pathlib.Path, rid: str) -> None:
    projects = (
        "Ryujinx.HLE",
        "Ryujinx.Graphics.Vulkan",
        "Ryujinx.Audio.Backends.OpenAL",
    )
    for project in projects:
        root = core_source / "src" / project / "bin/Release/net9.0"
        for output in (root, root / rid):
            if output.is_symlink():
                raise ValueError(f"Core build output must not be a symbolic link: {output}")
            if output.is_dir():
                for path in output.glob("*.pdb"):
                    if path.is_symlink() or not path.is_file():
                        raise ValueError(f"Core debug artifact must be a regular file: {path}")
                    path.unlink()


def assemble_archive(
    inputs: BuildInputs, app_publish: pathlib.Path, core_publish: pathlib.Path
) -> pathlib.Path:
    root = inputs.work / f"NX-Infinity-{VERSION}-{inputs.rid}"
    copy_tree(app_publish, root)
    copy_tree(core_publish, root / "cores/nxi")
    nuget_value = os.environ.get("NUGET_PACKAGES")
    if not nuget_value:
        raise ValueError("NUGET_PACKAGES must name the explicit package cache used for this build.")
    add_distribution_files(
        root,
        rid=inputs.rid,
        nuget_root=pathlib.Path(nuget_value).resolve(),
        external_notices=inputs.notice_sources,
    )
    package_notices.generate(root, pathlib.Path(nuget_value).resolve())
    source_bundle.add_ffmpeg_source(root, inputs.ffmpeg_source, inputs.rid)
    normalize_modes(root)
    write_backend_manifest(root / "cores/nxi", inputs.rid)
    write_package_manifest(root, inputs.rid, inputs.epoch)

    archive = inputs.output / f"{root.name}.tar.gz"
    checksum_path = checksum_destination(archive)
    create_archive(root, archive, inputs.epoch)
    checksum = common.sha256_file(archive)
    checksum_path.write_text(f"{checksum}  {archive.name}\n", encoding="ascii", newline="\n")
    print(f"{archive} {checksum}")
    return archive


def checksum_destination(archive: pathlib.Path) -> pathlib.Path:
    path = archive.with_suffix(archive.suffix + ".sha256")
    if path.exists() or path.is_symlink():
        raise FileExistsError(f"Checksum destination already exists: {path}")
    return path


def require_file(path: pathlib.Path, label: str) -> None:
    if not path.is_file() or path.is_symlink():
        raise ValueError(f"{label} is unavailable: {path}")


def enforce_required_nice(pid: int, label: str) -> None:
    required_value = os.environ.get("NXI_REQUIRED_NICE")
    if required_value is None:
        return
    required = int(required_value)
    os.setpriority(os.PRIO_PROCESS, pid, required)
    observed = int(
        subprocess.check_output(["ps", "-o", "ni=", "-p", str(pid)], text=True).strip()
    )
    if observed != required:
        raise RuntimeError(f"{label} must run at nice {required}; observed {observed}.")
    print(f"priority: {label}: pid {pid}: nice {observed}", flush=True)


def require_rid(rid: str) -> str:
    if rid not in common.SUPPORTED_RIDS:
        raise ValueError(f"Unsupported Linux runtime identifier: {rid}")
    return rid


def arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rid", required=True, choices=sorted(common.SUPPORTED_RIDS))
    parser.add_argument("--core-source", required=True)
    parser.add_argument("--core-dotnet", required=True)
    parser.add_argument("--dotnet", required=True)
    parser.add_argument("--work", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--notice-sources", required=True)
    parser.add_argument("--ffmpeg-source", required=True)
    parser.add_argument("--epoch", type=int, required=True)
    return parser.parse_args()


if __name__ == "__main__":
    build(arguments())
