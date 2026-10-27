#!/usr/bin/env python3
"""Shared validation primitives for native Linux packaging."""

from __future__ import annotations

import hashlib
import json
import pathlib
import re
import stat
import struct
import tarfile
import unicodedata
from typing import Any, BinaryIO, Iterable


JsonObject = dict[str, Any]
EXTERNAL_LINK = re.compile(r"(?:https?://|mailto:)", re.IGNORECASE)
SUPPORTED_RIDS = {"linux-x64": 62, "linux-arm64": 183}
WINDOWS_RESERVED = {
    "con",
    "prn",
    "aux",
    "nul",
    *(f"com{number}" for number in range(1, 10)),
    *(f"lpt{number}" for number in range(1, 10)),
}


def safe_relative(value: str) -> pathlib.PurePosixPath:
    windows = pathlib.PureWindowsPath(value)
    path = pathlib.PurePosixPath(value)
    if (
        not value
        or "\\" in value
        or "\0" in value
        or windows.drive
        or windows.root
        or path.is_absolute()
        or not path.parts
        or value != path.as_posix()
        or value != unicodedata.normalize("NFC", value)
        or any(
            unicodedata.category(character).startswith("C")
            or unicodedata.category(character) in {"Zl", "Zp"}
            for character in value
        )
    ):
        raise ValueError(f"Unsafe package path: {value!r}")
    for part in path.parts:
        stem = part.split(".", 1)[0].casefold()
        if (
            part in {"", ".", ".."}
            or part.endswith((".", " "))
            or any(ord(character) < 32 or character in '<>:"|?*' for character in part)
            or stem in WINDOWS_RESERVED
        ):
            raise ValueError(f"Unsafe package path: {value!r}")
    return path


def validate_portable_paths(values: Iterable[str]) -> None:
    seen: dict[str, str] = {}
    for value in values:
        normalized = str(safe_relative(value)).casefold()
        if normalized in seen:
            raise ValueError(f"Portable path collision: {seen[normalized]!r} and {value!r}")
        seen[normalized] = value


def strict_json(data: bytes, label: str) -> JsonObject:
    def unique_object(pairs: list[tuple[str, Any]]) -> JsonObject:
        result: JsonObject = {}
        for key, value in pairs:
            if key in result:
                raise ValueError(f"Duplicate JSON field {key!r} in {label}.")
            result[key] = value
        return result

    value = json.loads(data, object_pairs_hook=unique_object)
    if not isinstance(value, dict):
        raise ValueError(f"{label} must be a JSON object.")
    return value


def require_schema(value: JsonObject, expected: int, label: str) -> None:
    actual = value.get("schemaVersion")
    if not isinstance(actual, int) or isinstance(actual, bool) or actual != expected:
        raise ValueError(f"{label} has an unsupported schemaVersion.")


def canonical_json(value: JsonObject) -> bytes:
    return (json.dumps(value, indent=2, sort_keys=True) + "\n").encode("utf-8")


def sha256_file(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def regular_files(root: pathlib.Path, excluded: Iterable[str] = ()) -> list[pathlib.Path]:
    if root.is_symlink() or not root.is_dir():
        raise ValueError(f"Package root must be an ordinary directory: {root}")
    excluded_set = set(excluded)
    files: list[pathlib.Path] = []
    for path in root.rglob("*"):
        relative = path.relative_to(root).as_posix()
        safe_relative(relative)
        if path.is_symlink():
            raise ValueError(f"Package tree contains a symbolic link: {relative}")
        if path.is_file() and relative not in excluded_set:
            files.append(path)
        elif not path.is_file() and not path.is_dir():
            raise ValueError(f"Package tree contains a non-regular entry: {relative}")
    validate_portable_paths(path.relative_to(root).as_posix() for path in files)
    return sorted(files, key=lambda path: path.relative_to(root).as_posix())


def inventory(root: pathlib.Path, excluded: Iterable[str] = ()) -> JsonObject:
    result: JsonObject = {}
    for path in regular_files(root, excluded):
        relative = path.relative_to(root).as_posix()
        mode = stat.S_IMODE(path.stat().st_mode)
        result[relative] = {
            "bytes": path.stat().st_size,
            "mode": f"{mode:04o}",
            "sha256": sha256_file(path),
        }
    return result


def elf_machine(data: bytes) -> int:
    if len(data) < 20 or data[:4] != b"\x7fELF" or data[4] not in (1, 2):
        raise ValueError("Expected a 32-bit or 64-bit ELF file.")
    byte_order = {1: "little", 2: "big"}.get(data[5])
    if byte_order is None:
        raise ValueError("ELF file has an unsupported byte order.")
    return int.from_bytes(data[18:20], byte_order)


def elf_machine_file(path: pathlib.Path) -> int:
    with path.open("rb") as stream:
        return elf_machine(stream.read(64))


def elf_needed(data: bytes) -> list[str]:
    """Return the DT_NEEDED entries of a 64-bit little-endian ELF file.

    The dynamic table is located through the program headers, as the loader
    does, so stripping section headers cannot hide a dependency.
    """
    try:
        elf_type = struct.unpack_from("<H", data, 0x10)[0]
        header_offset = struct.unpack_from("<Q", data, 0x20)[0]
        entry_size, count = struct.unpack_from("<HH", data, 0x36)
        if elf_type not in (2, 3) or count == 0 or entry_size != 56:
            raise ValueError("ELF is not a loadable executable or shared object.")
        headers = [
            struct.unpack_from("<IIQQQQQQ", data, header_offset + index * 56)
            for index in range(count)
        ]
        dynamic = [header for header in headers if header[0] == 2]
        if not dynamic:
            return []
        if len(dynamic) != 1:
            raise ValueError("ELF has more than one dynamic segment.")
        entries = []
        for index in range(dynamic[0][5] // 16):
            tag, value = struct.unpack_from("<qQ", data, dynamic[0][2] + index * 16)
            if tag == 0:
                break
            entries.append((tag, value))
        if not any(tag == 1 for tag, _ in entries):
            return []
        values = {tag: value for tag, value in entries if tag in (5, 10)}
        strings = elf_file_offset(headers, values[5])
        limit = strings + values[10]
        needed: list[str] = []
        for tag, value in entries:
            if tag == 1:
                end = data.index(b"\0", strings + value, limit)
                needed.append(data[strings + value:end].decode("ascii"))
        return needed
    except (IndexError, KeyError, struct.error, UnicodeDecodeError) as error:
        raise ValueError(f"ELF dynamic section is malformed: {error}") from error


def elf_file_offset(headers: list[tuple[int, ...]], address: int) -> int:
    for kind, _, offset, virtual, _, size, _, _ in headers:
        if kind == 1 and virtual <= address < virtual + size:
            return offset + address - virtual
    raise ValueError("ELF dynamic string table is outside every loadable segment.")


class _TarSink:
    def __init__(self, stream: BinaryIO | None = None) -> None:
        self.stream = stream
        self.digest = hashlib.sha256()
        self.size = 0

    def write(self, data: bytes) -> int:
        if self.stream is not None:
            self.stream.write(data)
        self.digest.update(data)
        self.size += len(data)
        return len(data)

    def tell(self) -> int:
        return self.size


def write_canonical_tar(root: pathlib.Path, stream: BinaryIO | None, epoch: int) -> tuple[str, int]:
    """Write the canonical uncompressed tar of root and return its SHA-256 and size."""
    files = regular_files(root)
    directory_set = {root}
    for path in files:
        parent = path.parent
        while parent != root.parent:
            directory_set.add(parent)
            parent = parent.parent
    directories = sorted(
        directory_set,
        key=lambda path: (len(path.relative_to(root.parent).parts), path.relative_to(root.parent).as_posix()),
    )
    sink = _TarSink(stream)
    with tarfile.open(fileobj=sink, mode="w", format=tarfile.PAX_FORMAT) as archive:
        for directory in directories:
            info = tar_info(directory.relative_to(root.parent).as_posix() + "/", epoch, 0o755, 0)
            info.type = tarfile.DIRTYPE
            archive.addfile(info)
        for path in files:
            mode = stat.S_IMODE(path.stat().st_mode)
            info = tar_info(path.relative_to(root.parent).as_posix(), epoch, mode, path.stat().st_size)
            with path.open("rb") as source:
                archive.addfile(info, source)
    return sink.digest.hexdigest(), sink.size


def tar_info(name: str, epoch: int, mode: int, size: int) -> tarfile.TarInfo:
    info = tarfile.TarInfo(name)
    info.mtime = epoch
    info.mode = mode
    info.size = size
    info.uid = 0
    info.gid = 0
    info.uname = ""
    info.gname = ""
    info.pax_headers = {}
    return info
