#!/usr/bin/env python3
"""Verify native NX Infinity Linux package trees and archives."""

from __future__ import annotations

import argparse
import hashlib
import pathlib
import re
import stat
import tarfile
import tempfile
import urllib.parse
import zlib
from typing import Any, BinaryIO

import common
import verify_dependencies


VERSION = "1.0.0"
BUILD_MANIFEST = "NX-LINUX-BUILD.json"
BACKEND_NAME = "NX Infinity Core"
BACKEND_SOURCE_URL = "https://github.com/coye2/NX-Infinity"
CORE_REVISION = "67591deee07ab2ce86fc9b72c42e8b64c95ba1b3"
SOURCE_BASELINE = "b26ba273dc83fb757b2548519f1ee44e3d4d6df2"
MAX_ARCHIVE_MEMBERS = 4096
MAX_ARCHIVE_EXPANDED_BYTES = 4 * 1024 * 1024 * 1024
ARCHIVE_EXECUTABLES = {
    "NxInfinity",
    "createdump",
    "nx-infinity",
    "install-desktop.sh",
    "cores/nxi/NxInfinity.Core",
    "cores/nxi/createdump",
}
MAX_ARCHIVE_STREAM_BYTES = MAX_ARCHIVE_EXPANDED_BYTES + (4 * MAX_ARCHIVE_MEMBERS + 20) * 512
STREAM_CHUNK_BYTES = 1024 * 1024
SYSTEM_DEPENDENCIES = (
    "glibc (x64 2.38 or newer, ARM64 2.34 or newer)",
    "the GCC runtime libraries libstdc++.so.6 and libgcc_s.so.1",
    "fontconfig",
    "libopenal.so.1",
    "libvulkan.so.1 and a Vulkan 1.2-capable driver",
    "an X11 desktop session (Wayland through XWayland is supported)",
)
SYSTEM_LIBRARIES = {
    "libc.so.6",
    "libdl.so.2",
    "libm.so.6",
    "libpthread.so.0",
    "librt.so.1",
    "libstdc++.so.6",
    "libgcc_s.so.1",
    "libfontconfig.so.1",
}
SYSTEM_LOADERS = {"linux-x64": "ld-linux-x86-64.so.2", "linux-arm64": "ld-linux-aarch64.so.1"}
TRACE_PROVIDER = "libcoreclrtraceptprovider.so"
TRACE_PROVIDER_LIBRARY = "liblttng-ust.so.0"
THIRD_PARTY_DOCUMENT_ROOT = "licenses/native/"
MARKDOWN_INLINE_LINK = re.compile(r"\[[^\]]*\]\(([^()\s<>\"']+)(?: \"[^\"]*\")?\)")
MARKDOWN_REFERENCE = re.compile(r"\[[^\]]+\]:")
MARKDOWN_REFERENCE_LINK = re.compile(r"^ {0,3}\[[^\]]+\]:[ \t]*([^\s<>\"']+)[ \t]*$", re.MULTILINE)
MARKDOWN_AUTOLINK = re.compile(r"<([A-Za-z][A-Za-z0-9+.-]{1,31}:[^\s<>]*)>")
HTML_TAG_WITH_ATTRIBUTE = re.compile(r"<[A-Za-z][A-Za-z0-9-]*[\s/][^>]*=")
REQUIRED_DISTRIBUTION_FILES = (
    "README-LINUX.md",
    "licenses/LICENSE",
    "licenses/LICENSING.md",
    "licenses/THIRD-PARTY-NOTICES.md",
    "licenses/repository/Ryujinx-MIT.txt",
    "licenses/repository/Ryujinx-Team-MIT.txt",
    "licenses/repository/Ryujinx-third-party.md",
    "licenses/totk-dfps-source/LICENSE",
    "licenses/totk-dfps-source/NX-NOTICE.md",
)
PROHIBITED_CONTENT_COMPONENTS = {
    "data",
    "firmware",
    "games",
    "keys",
    "save",
    "saves",
    "translation",
    "user-data",
    "userdata",
}
PROHIBITED_CONTENT_SUFFIXES = {".keys", ".nca", ".nsp", ".nsz", ".xci", ".xcz"}
FORBIDDEN_MUTABLE_NETWORK_CONTENT = (
    b"tessdata_fast/main",
    b"?ref=main",
    b"/nso-icons/main/",
    b"static.wixstatic.com/media/",
)
FORBIDDEN_UNSUPPORTED_UI = (
    b"Universal text translation",
    b"Download selected language packs",
    b"Hello Kitty & Friends",
    b"Nintendo Switch Online icon collections",
)


def verify_tree(root: pathlib.Path, expected_rid: str) -> dict[str, Any]:
    files = verify_root(root, expected_rid)
    epoch = verify_build_manifest(root, expected_rid)
    verify_backend_manifest(root)
    verify_required_payload(root)
    verify_release_security(root, expected_rid)
    elf_files = verify_elf_architecture(root, files, expected_rid)
    verify_system_libraries(root, files, expected_rid)
    verify_document_links(root, files)
    return {
        "files": len(files),
        "elfFiles": elf_files,
        "runtimeIdentifier": expected_rid,
        "sourceDateEpoch": epoch,
    }


def verify_root(root: pathlib.Path, expected_rid: str) -> list[pathlib.Path]:
    require_rid(expected_rid)
    expected_name = f"NX-Infinity-{VERSION}-{expected_rid}"
    if root.name != expected_name or root.is_symlink() or not root.is_dir():
        raise ValueError(f"Package root must be {expected_name}.")
    files = common.regular_files(root)
    for path in files:
        relative_path = path.relative_to(root)
        relative = relative_path.as_posix()
        components = {part.casefold() for part in relative_path.parts}
        if components & PROHIBITED_CONTENT_COMPONENTS or path.suffix.casefold() in PROHIBITED_CONTENT_SUFFIXES:
            raise ValueError(f"Archive contains a prohibited user-data/content path: {relative}")
        if path.suffix.casefold() == ".pdb":
            raise ValueError(f"Archive contains a build-path-bearing debug artifact: {relative}")
    return files


def verify_build_manifest(root: pathlib.Path, expected_rid: str) -> int:
    build_path = root / BUILD_MANIFEST
    build = common.strict_json(build_path.read_bytes(), BUILD_MANIFEST)
    common.require_schema(build, 1, BUILD_MANIFEST)
    require_exact_keys(
        build,
        {
            "application",
            "backendRevision",
            "files",
            "qualification",
            "runtimeIdentifier",
            "schemaVersion",
            "sourceBaseline",
            "sourceDateEpoch",
            "systemDependencies",
            "version",
        },
        BUILD_MANIFEST,
    )
    if (
        build["application"] != "NX Infinity"
        or build["backendRevision"] != CORE_REVISION
        or build["qualification"] != "unqualified"
        or build["runtimeIdentifier"] != expected_rid
        or build["sourceBaseline"] != SOURCE_BASELINE
        or build["systemDependencies"] != list(SYSTEM_DEPENDENCIES)
        or build["version"] != VERSION
    ):
        raise ValueError("Linux build manifest identity does not match this package.")
    epoch = build["sourceDateEpoch"]
    if not isinstance(epoch, int) or isinstance(epoch, bool) or epoch < 1:
        raise ValueError("Linux build manifest has an invalid sourceDateEpoch.")
    verify_inventory(root, build["files"], (BUILD_MANIFEST,), "Linux build manifest")
    return epoch


def verify_backend_manifest(root: pathlib.Path) -> None:
    backend_path = root / "cores/nxi/backend.json"
    backend = common.strict_json(backend_path.read_bytes(), "backend.json")
    require_exact_keys(
        backend,
        {
            "backendId",
            "backendName",
            "entryPoint",
            "files",
            "managedAssembly",
            "manifestVersion",
            "qualification",
            "revision",
            "sourceUrl",
        },
        "backend.json",
    )
    if (
        backend["manifestVersion"] != 1
        or isinstance(backend["manifestVersion"], bool)
        or backend["backendId"] != "nxi"
        or backend["backendName"] != BACKEND_NAME
        or backend["entryPoint"] != "NxInfinity.Core"
        or backend["managedAssembly"] is not False
        or backend["qualification"] != "unqualified"
        or backend["revision"] != CORE_REVISION
        or backend["sourceUrl"] != BACKEND_SOURCE_URL
    ):
        raise ValueError("Backend manifest identity does not match native NX Infinity Core.")
    verify_backend_inventory(root / "cores/nxi", backend["files"])


def verify_required_payload(root: pathlib.Path) -> None:
    require_mode(root / "NxInfinity", 0o755)
    require_mode(root / "createdump", 0o755)
    require_mode(root / "nx-infinity", 0o755)
    require_mode(root / "install-desktop.sh", 0o755)
    require_mode(root / "cores/nxi/NxInfinity.Core", 0o755)
    require_mode(root / "cores/nxi/createdump", 0o755)
    require_mode(root / "share/applications/nx-infinity.desktop", 0o644)
    require_mode(root / "share/icons/hicolor/256x256/apps/nx-infinity.png", 0o644)
    for relative in REQUIRED_DISTRIBUTION_FILES:
        require_mode(root / relative, 0o644)


def verify_elf_architecture(
    root: pathlib.Path, files: list[pathlib.Path], expected_rid: str
) -> int:
    expected_machine = require_rid(expected_rid)
    elf_files = 0
    for path in files:
        with path.open("rb") as stream:
            header = stream.read(20)
        if header[:4] != b"\x7fELF":
            continue
        relative = path.relative_to(root).as_posix()
        elf_files += 1
        if len(header) < 20 or header[4:6] != b"\x02\x01":
            raise ValueError(f"ELF must be 64-bit little-endian: {relative}.")
        if common.elf_machine(header) != expected_machine:
            raise ValueError(f"ELF architecture mismatch for {relative}.")
    if elf_files < 8:
        raise ValueError("Linux package does not contain the required ELF payload.")
    return elf_files


def verify_system_libraries(
    root: pathlib.Path, files: list[pathlib.Path], expected_rid: str
) -> None:
    libraries: dict[pathlib.Path, list[str]] = {}
    for path in files:
        data = path.read_bytes()
        if data[:4] == b"\x7fELF":
            libraries[path] = common.elf_needed(data)
    bundled = {path.name for path in libraries}
    allowed = SYSTEM_LIBRARIES | {SYSTEM_LOADERS[expected_rid]}
    for path, needed in libraries.items():
        for name in needed:
            if name in bundled or name in allowed:
                continue
            if name == TRACE_PROVIDER_LIBRARY and path.name == TRACE_PROVIDER:
                continue
            relative = path.relative_to(root).as_posix()
            raise ValueError(f"{relative} needs an undeclared system library: {name}")


def verify_document_links(root: pathlib.Path, files: list[pathlib.Path]) -> None:
    resolved_root = root.resolve()
    for path in files:
        relative = path.relative_to(root).as_posix()
        if path.suffix.casefold() != ".md" or relative.startswith(THIRD_PARTY_DOCUMENT_ROOT):
            continue
        for target in document_link_targets(path.read_text(encoding="utf-8"), relative):
            if "\\" in target or "&" in target:
                raise ValueError(f"Packaged document link is broken by an escape or entity: {relative} -> {target}")
            if common.EXTERNAL_LINK.match(target) or target.startswith("#"):
                continue
            location = urllib.parse.unquote(target.split("#", 1)[0])
            resolved = (path.parent / location).resolve()
            names_directory = location.endswith("/") or location.rsplit("/", 1)[-1] in {".", ".."}
            if (
                not resolved.is_relative_to(resolved_root)
                or not resolved.exists()
                or (names_directory and not resolved.is_dir())
            ):
                raise ValueError(f"Packaged document link is broken: {relative} -> {target}")


def document_link_targets(text: str, relative: str) -> list[str]:
    inline = MARKDOWN_INLINE_LINK.findall(text)
    references = MARKDOWN_REFERENCE_LINK.findall(text)
    if (
        text.count("](") != len(inline)
        or len(MARKDOWN_REFERENCE.findall(text)) != len(references)
        or text.count("]:") != len(references)
        or HTML_TAG_WITH_ATTRIBUTE.search(text)
        or any(not common.EXTERNAL_LINK.match(target) for target in MARKDOWN_AUTOLINK.findall(text))
    ):
        raise ValueError(f"Packaged document link is broken or unsupported: {relative}")
    return inline + references


def verify_release_security(root: pathlib.Path, rid: str) -> None:
    launcher = root / "NxInfinity.dll"
    if launcher.is_symlink() or not launcher.is_file():
        raise ValueError("Linux package is missing the launcher assembly.")
    launcher_bytes = launcher.read_bytes()
    for marker in FORBIDDEN_MUTABLE_NETWORK_CONTENT:
        if contains_marker(launcher_bytes, marker):
            raise ValueError(f"Linux launcher retains mutable network content: {marker!r}")
    for marker in FORBIDDEN_UNSUPPORTED_UI:
        if contains_marker(launcher_bytes, marker):
            raise ValueError(f"Linux launcher offers an unsupported online action: {marker!r}")
    verify_dependencies.verify(root, rid)


def contains_marker(data: bytes, marker: bytes) -> bool:
    return marker in data or marker.decode("ascii").encode("utf-16-le") in data


def verify_archive(archive_path: pathlib.Path, expected_rid: str) -> dict[str, Any]:
    expected_name = f"NX-Infinity-{VERSION}-{expected_rid}"
    if archive_path.is_symlink() or not archive_path.is_file():
        raise ValueError(f"Archive is unavailable: {archive_path}")
    stream_digest, stream_size, gzip_mtime = gzip_payload_identity(archive_path)
    with tarfile.open(archive_path, "r:gz") as archive:
        members = archive.getmembers()
        if len(members) > MAX_ARCHIVE_MEMBERS:
            raise ValueError("Archive exceeds the member-count safety bound.")
        if any(member.size < 0 for member in members):
            raise ValueError("Archive contains a negative entry size.")
        expanded_bytes = sum(member.size for member in members if member.isfile())
        if expanded_bytes > MAX_ARCHIVE_EXPANDED_BYTES:
            raise ValueError("Archive exceeds the expanded-size safety bound.")
        names = [member.name for member in members]
        common.validate_portable_paths(names)
        if len(names) != len(set(names)):
            raise ValueError("Archive contains duplicate paths.")
        if not names or names[0] != expected_name:
            raise ValueError(f"Archive root must be {expected_name}.")
        for member in members:
            if member.name.split("/", 1)[0] != expected_name:
                raise ValueError("Archive contains a path outside its package root.")
            if member.type not in (tarfile.REGTYPE, tarfile.DIRTYPE):
                raise ValueError(f"Archive contains an unsupported entry: {member.name}")
            if member.uid != 0 or member.gid != 0 or member.uname or member.gname:
                raise ValueError(f"Archive owner metadata is not reproducible: {member.name}")
            verify_archive_member_mode(member, expected_name)
        epochs = {member.mtime for member in members}
        if len(epochs) != 1:
            raise ValueError("Archive timestamps are not reproducible.")
        with tempfile.TemporaryDirectory(prefix="nx-linux-verify-", dir=archive_path.parent) as directory:
            archive.extractall(directory, filter="data")
            root = pathlib.Path(directory) / expected_name
            summary = verify_tree(root, expected_rid)
            if epochs != {summary["sourceDateEpoch"]} or gzip_mtime != summary["sourceDateEpoch"]:
                raise ValueError("Archive timestamp does not match its build manifest.")
            canonical = common.write_canonical_tar(root, None, summary["sourceDateEpoch"])
            if (stream_digest, stream_size) != canonical:
                raise ValueError("Archive stream is not the canonical serialization of its package files.")
            return summary


def gzip_payload_identity(archive_path: pathlib.Path) -> tuple[str, int, int]:
    """Return the SHA-256, size and mtime of the archive's only gzip member."""
    with archive_path.open("rb") as stream:
        header = stream.read(10)
        if len(header) != 10 or header[:4] != b"\x1f\x8b\x08\x00" or header[8:10] != b"\x00\xff":
            raise ValueError("Archive gzip header is not canonical.")
        stream.seek(0)
        digest, size = inflate_single_member(stream)
    return digest, size, int.from_bytes(header[4:8], "little")


def inflate_single_member(stream: BinaryIO) -> tuple[str, int]:
    decompressor = zlib.decompressobj(31)
    digest = hashlib.sha256()
    size = 0
    pending = b""
    exhausted = False
    while not decompressor.eof:
        if not pending and not exhausted:
            pending = stream.read(STREAM_CHUNK_BYTES)
            exhausted = not pending
        try:
            output = decompressor.decompress(pending, STREAM_CHUNK_BYTES)
        except zlib.error as error:
            raise ValueError(f"Archive gzip stream is corrupt: {error}") from error
        pending = decompressor.unconsumed_tail
        if not output and not pending and exhausted and not decompressor.eof:
            raise ValueError("Archive gzip stream is truncated.")
        size += len(output)
        if size > MAX_ARCHIVE_STREAM_BYTES:
            raise ValueError("Archive exceeds the expanded-size safety bound.")
        digest.update(output)
    if decompressor.unused_data or pending or stream.read(1):
        raise ValueError("Archive contains bytes after its gzip member.")
    return digest.hexdigest(), size


def verify_archive_member_mode(member: tarfile.TarInfo, root_name: str) -> None:
    relative = pathlib.PurePosixPath(member.name.rstrip("/")).relative_to(root_name).as_posix()
    expected = 0o755 if member.isdir() or relative in ARCHIVE_EXECUTABLES else 0o644
    actual = stat.S_IMODE(member.mode)
    if actual != expected:
        raise ValueError(
            f"Archive entry has unsafe mode {actual:04o}; expected {expected:04o}: {member.name}"
        )


def verify_inventory(root: pathlib.Path, value: Any, excluded: tuple[str, ...], label: str) -> None:
    if not isinstance(value, dict):
        raise ValueError(f"{label} file inventory is invalid.")
    expected = common.inventory(root, excluded)
    if set(value) != set(expected):
        raise ValueError(f"{label} does not exactly cover the package files.")
    for relative, metadata in value.items():
        if not isinstance(metadata, dict):
            raise ValueError(f"{label} metadata is invalid for {relative}.")
        require_exact_keys(metadata, {"bytes", "mode", "sha256"}, f"{label}/{relative}")
        if metadata != expected[relative]:
            raise ValueError(f"{label} identity mismatch for {relative}.")


def verify_backend_inventory(core: pathlib.Path, value: Any) -> None:
    if not isinstance(value, dict) or not value:
        raise ValueError("Backend file inventory is invalid.")
    expected = common.inventory(core, ("backend.json",))
    if set(value) != set(expected):
        raise ValueError("Backend manifest does not exactly cover the core publication.")
    for relative, digest in value.items():
        if digest != expected[relative]["sha256"]:
            raise ValueError(f"Backend artifact hash mismatch: {relative}")


def require_exact_keys(value: dict[str, Any], expected: set[str], label: str) -> None:
    if set(value) != expected:
        raise ValueError(f"{label} has missing or unexpected fields.")


def require_mode(path: pathlib.Path, expected: int) -> None:
    if path.is_symlink() or not path.is_file():
        raise ValueError(f"Required package file is missing: {path.name}")
    actual = stat.S_IMODE(path.stat().st_mode)
    if actual != expected:
        raise ValueError(f"Unexpected mode {actual:04o} for {path.name}; expected {expected:04o}.")


def require_rid(rid: str) -> int:
    if rid not in common.SUPPORTED_RIDS:
        raise ValueError(f"Unsupported Linux runtime identifier: {rid}")
    return common.SUPPORTED_RIDS[rid]


def arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", type=pathlib.Path)
    parser.add_argument("--tree", type=pathlib.Path)
    parser.add_argument("--rid", required=True, choices=sorted(common.SUPPORTED_RIDS))
    value = parser.parse_args()
    if (value.archive is None) == (value.tree is None):
        parser.error("select exactly one of --archive or --tree")
    return value


if __name__ == "__main__":
    options = arguments()
    result = (
        verify_archive(options.archive.absolute(), options.rid)
        if options.archive is not None
        else verify_tree(options.tree.absolute(), options.rid)
    )
    print(result)
