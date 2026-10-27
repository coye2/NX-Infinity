#!/usr/bin/env python3
"""Independent dependency, notice, and source policy for Linux archives."""

from __future__ import annotations

import hashlib
import pathlib
import struct
import unicodedata
from typing import Any

import common


NATIVE_DEPENDENCIES_SHA256 = "6a1b469b397265b859701f686af66e70c0a33f1e3fcb3bc82d109602e882128c"
VULNERABLE_LIBRARY_IDENTITIES = {
    "system.private.uri/4.3.0",
    "system.text.json/8.0.0",
}
REQUIRED_LIBRARY_IDENTITIES = {
    "launcher": {
        "avalonia.desktop/12.1.4",
        "avalonia.fonts.inter/12.1.4",
        "harfbuzzsharp.nativeassets.linux/8.3.1.3",
        "skiasharp.nativeassets.linux/3.119.4",
    },
    "core": {
        "concentus/2.2.2",
        "ryujinx.graphics.nvdec.dependencies.linux/6.1.2-build4",
        "ryujinx.sdl3-cs/2026.918.0",
        "silk.net.shaderc.native/2.22.0",
        "skiasharp.nativeassets.linux/2.88.9",
    },
}
EXPECTED_ASSET_PATHS = {
    "linux-x64": {
        "libHarfBuzzSharp.so",
        "libSkiaSharp.so",
        "cores/nxi/libSDL3.so",
        "cores/nxi/libSkiaSharp.so",
        "cores/nxi/libavcodec.so.60",
        "cores/nxi/libavutil.so.58",
        "cores/nxi/libglfw.so.3",
        "cores/nxi/libshaderc_shared.so",
    },
    "linux-arm64": {
        "libHarfBuzzSharp.so",
        "libSkiaSharp.so",
        "cores/nxi/libSDL3.so",
        "cores/nxi/libSkiaSharp.so",
        "cores/nxi/libavcodec.so.60",
        "cores/nxi/libavutil.so.58",
        "cores/nxi/libshaderc_shared.so",
    },
}
DEPENDENCY_KEYS = {
    "assets",
    "distribution",
    "id",
    "license",
    "notices",
    "packageSha256",
    "source",
    "sourceBinaryReproducibilityVerified",
    "sourceRevision",
    "version",
}
OPTIONAL_DEPENDENCY_KEYS = {"buildConfigurations", "sourceRevisions"}
GRAPH_NAMES = {"launcher", "core"}
GRAPH_KEYS = {"runtimeTarget", "sha256"}
NOTICE_KEYS = {"path", "sha256"}
SOURCE_KEYS = {"files", "path", "sha256"}
DEPS_KEYS = {"compilationOptions", "libraries", "runtimes", "runtimeTarget", "targets"}
PACKAGE_NOTICE_KEYS = {"files", "sha256"}
PACKAGE_INVENTORY_KEYS = {"files", "sha256"}
PACKAGE_METADATA_KEYS = {
    "authors",
    "copyright",
    "declaredLicense",
    "graphContentHash",
    "identity",
    "notices",
    "nuspecPath",
    "nuspecSha256",
    "nupkgSha256",
    "nupkgSha512",
    "projectUrl",
    "repository",
}
CODEVIEW_KEYS = {"assembly", "assemblySha256", "path"}
EMBEDDED_PDB_KEYS = {"assembly", "assemblySha256"}
PORTABLE_EXECUTABLE_SUFFIXES = {".dll", ".exe"}
DEBUG_CODEVIEW = 2
DEBUG_REPRODUCIBLE = 16
DEBUG_EMBEDDED_PORTABLE_PDB = 17
DEBUG_PDB_CHECKSUM = 19
DEBUG_PERF_MAP = 21
DEBUG_TYPES = {
    DEBUG_CODEVIEW,
    DEBUG_REPRODUCIBLE,
    DEBUG_EMBEDDED_PORTABLE_PDB,
    DEBUG_PDB_CHECKSUM,
    DEBUG_PERF_MAP,
}
DEBUG_PATH_MAGIC = {DEBUG_CODEVIEW: b"RSDS", DEBUG_PERF_MAP: b"R2RM"}
MAPPED_SOURCE_ROOTS = {"artifacts", "binding", "packages", "src"}
DEBUG_PATH_RECORD_SIZE = {DEBUG_PERF_MAP: 284}
SOURCE_RECORD_KEYS = {"path", "sha256"}
PACKAGE_INVENTORY_EXCLUDED = {
    "NX-LINUX-BUILD.json",
    "cores/nxi/backend.json",
    "licenses/NATIVE-DEPENDENCIES.json",
}


def verify(root: pathlib.Path, rid: str) -> None:
    manifest = load_manifest(root)
    graph_policy = validate_graph_policy(manifest.get("dependencyGraphs"))
    verify_package_inventory(root, rid, manifest.get("packageInventory"))
    verify_native_payloads(root, rid, manifest.get("nativePayloads"))
    dependency_ids: set[str] = set()
    notice_names: set[str] = set()
    asset_paths: set[str] = set()
    for dependency in require_dependencies(manifest):
        validate_dependency(dependency, dependency_ids)
        notice_names.update(validate_notices(root, dependency["notices"]))
        asset_paths.update(validate_assets(root, rid, dependency["assets"]))
    if asset_paths != EXPECTED_ASSET_PATHS[rid]:
        raise ValueError("Native dependency assets do not match the required archive paths.")
    actual_notices = {
        path.relative_to(root / "licenses/native").as_posix()
        for path in common.regular_files(root / "licenses/native")
    }
    if actual_notices != notice_names:
        raise ValueError("Native dependency notices are missing or unclaimed.")
    verify_corresponding_source(root, manifest.get("correspondingSource"))
    launcher_packages = verify_graph(
        root / "NxInfinity.deps.json", rid, "launcher", graph_policy[rid]["launcher"]
    )
    core_packages = verify_graph(
        root / "cores/nxi/NxInfinity.Core.deps.json",
        rid,
        "core",
        graph_policy[rid]["core"],
    )
    packages = dict(launcher_packages)
    for identity, metadata in core_packages.items():
        if identity in packages and packages[identity] != metadata:
            raise ValueError(f"Package metadata differs between dependency graphs: {identity}")
        packages[identity] = metadata
    verify_managed_package_notices(
        root,
        rid,
        manifest.get("managedPackageNotices"),
        packages,
    )
    verify_producer_codeview_paths(root, manifest.get("producerCodeViewPaths"))
    verify_embedded_portable_pdbs(root, manifest.get("embeddedPortablePdbAssemblies"))
    verify_redistribution_sources(root, rid, manifest.get("redistributionSources"))


def load_manifest(root: pathlib.Path) -> common.JsonObject:
    path = root / "licenses/NATIVE-DEPENDENCIES.json"
    if path.is_symlink() or not path.is_file():
        raise ValueError("Native dependency manifest is missing.")
    if common.sha256_file(path) != NATIVE_DEPENDENCIES_SHA256:
        raise ValueError("Native dependency manifest does not match the reviewed policy.")
    value = common.strict_json(path.read_bytes(), "NATIVE-DEPENDENCIES.json")
    common.require_schema(value, 6, "NATIVE-DEPENDENCIES.json")
    expected = {
        "correspondingSource",
        "dependencies",
        "dependencyGraphs",
        "embeddedPortablePdbAssemblies",
        "managedPackageNotices",
        "nativePayloads",
        "packageInventory",
        "producerCodeViewPaths",
        "redistributionSources",
        "schemaVersion",
    }
    if set(value) != expected:
        raise ValueError("Native dependency manifest has unexpected fields.")
    return value


def require_dependencies(value: common.JsonObject) -> list[common.JsonObject]:
    dependencies = value.get("dependencies")
    if not isinstance(dependencies, list) or not dependencies:
        raise ValueError("Native dependency manifest has no dependency inventory.")
    if not all(isinstance(item, dict) for item in dependencies):
        raise ValueError("Native dependency manifest contains an invalid dependency.")
    return dependencies


def validate_dependency(dependency: common.JsonObject, seen: set[str]) -> None:
    keys = set(dependency)
    if not DEPENDENCY_KEYS.issubset(keys) or keys - DEPENDENCY_KEYS - OPTIONAL_DEPENDENCY_KEYS:
        raise ValueError("Native dependency entry has missing or unexpected fields.")
    identity = dependency.get("id")
    strings = ("distribution", "id", "license", "source", "version")
    if not all(isinstance(dependency.get(key), str) and dependency[key] for key in strings):
        raise ValueError("Native dependency entry has invalid metadata.")
    if identity in seen:
        raise ValueError(f"Duplicate native dependency identity: {identity}")
    seen.add(identity)
    package_hash = dependency.get("packageSha256")
    if package_hash is not None:
        require_sha256(package_hash, f"Native dependency package {identity}")
    revision = dependency.get("sourceRevision")
    if revision is not None and (not isinstance(revision, str) or not revision):
        raise ValueError(f"Native dependency source revision is invalid: {identity}")
    revisions = dependency.get("sourceRevisions")
    if revisions is not None and (
        not isinstance(revisions, dict)
        or set(revisions) != set(common.SUPPORTED_RIDS)
        or not all(
            value is None
            or (
                isinstance(value, str)
                and len(value) == 40
                and all(character in "0123456789abcdef" for character in value)
            )
            for value in revisions.values()
        )
    ):
        raise ValueError(f"Native dependency per-RID source revisions are invalid: {identity}")
    configurations = dependency.get("buildConfigurations")
    if configurations is not None and (
        not isinstance(configurations, dict)
        or set(configurations) != set(common.SUPPORTED_RIDS)
        or not all(isinstance(value, str) and value for value in configurations.values())
    ):
        raise ValueError(f"Native dependency per-RID build configurations are invalid: {identity}")
    if not isinstance(dependency.get("sourceBinaryReproducibilityVerified"), bool):
        raise ValueError(f"Native dependency reproducibility status is invalid: {identity}")


def validate_notices(root: pathlib.Path, value: Any) -> set[str]:
    if not isinstance(value, list) or not all(isinstance(item, dict) for item in value):
        raise ValueError("Native dependency notice inventory is invalid.")
    names: set[str] = set()
    for record in value:
        if set(record) != NOTICE_KEYS:
            raise ValueError("Native dependency notice record is invalid.")
        name = record.get("path")
        if not isinstance(name, str):
            raise ValueError("Native dependency notice path is invalid.")
        relative = common.safe_relative(name)
        if len(relative.parts) != 1 or name in names:
            raise ValueError(f"Native dependency notice must be a unique file name: {name}")
        require_sha256(record.get("sha256"), f"Native dependency notice {name}")
        path = root / "licenses/native" / name
        if path.is_symlink() or not path.is_file():
            raise ValueError(f"Native dependency notice is missing: {name}")
        if common.sha256_file(path) != record["sha256"]:
            raise ValueError(f"Native dependency notice identity mismatch: {name}")
        names.add(name)
    return names


def validate_assets(root: pathlib.Path, rid: str, value: Any) -> set[str]:
    if not isinstance(value, dict) or not set(value).issubset(common.SUPPORTED_RIDS):
        raise ValueError("Native dependency asset map is invalid.")
    records = value.get(rid, [])
    if not isinstance(records, list):
        raise ValueError(f"Native dependency assets are invalid for {rid}.")
    paths: set[str] = set()
    for record in records:
        if not isinstance(record, dict) or set(record) != {"path", "sha256"}:
            raise ValueError(f"Native dependency asset record is invalid for {rid}.")
        raw_path = record.get("path")
        if not isinstance(raw_path, str):
            raise ValueError(f"Native dependency asset path is invalid for {rid}.")
        relative = str(common.safe_relative(raw_path))
        require_sha256(record.get("sha256"), f"Native dependency asset {relative}")
        if relative in paths:
            raise ValueError(f"Duplicate native dependency asset: {relative}")
        path = root.joinpath(*pathlib.PurePosixPath(relative).parts)
        if path.is_symlink() or not path.is_file() or common.sha256_file(path) != record["sha256"]:
            raise ValueError(f"Native dependency asset identity mismatch: {relative}")
        paths.add(relative)
    return paths


def validate_graph_policy(value: Any) -> dict[str, dict[str, common.JsonObject]]:
    if not isinstance(value, dict) or set(value) != set(common.SUPPORTED_RIDS):
        raise ValueError("Native dependency graph policy does not cover every RID.")
    result: dict[str, dict[str, common.JsonObject]] = {}
    for rid, graphs in value.items():
        if not isinstance(graphs, dict) or set(graphs) != GRAPH_NAMES:
            raise ValueError(f"Native dependency graph policy is incomplete for {rid}.")
        result[rid] = {}
        for graph, record in graphs.items():
            if not isinstance(record, dict) or set(record) != GRAPH_KEYS:
                raise ValueError(f"Native dependency graph record is invalid for {rid}/{graph}.")
            target = f".NETCoreApp,Version=v10.0/{rid}"
            if record.get("runtimeTarget") != target:
                raise ValueError(f"Native dependency runtime target is invalid for {rid}/{graph}.")
            require_sha256(record.get("sha256"), f"Native dependency graph {rid}/{graph}")
            result[rid][graph] = record
    return result


def verify_graph(
    path: pathlib.Path,
    rid: str,
    graph: str,
    policy: common.JsonObject,
) -> dict[str, common.JsonObject]:
    if path.is_symlink() or not path.is_file():
        raise ValueError(f"{path.name} is missing.")
    if common.sha256_file(path) != policy["sha256"]:
        raise ValueError(f"{graph} dependency graph does not match the reviewed policy.")
    value = common.strict_json(path.read_bytes(), path.name)
    if set(value) != DEPS_KEYS:
        raise ValueError(f"{path.name} has missing or unexpected fields.")
    runtime_target = value.get("runtimeTarget")
    expected_target = policy["runtimeTarget"]
    if runtime_target != {"name": expected_target, "signature": ""}:
        raise ValueError(f"{graph} dependency runtime target does not match {rid}.")
    targets = value.get("targets")
    libraries = value.get("libraries")
    if not isinstance(targets, dict) or not isinstance(libraries, dict):
        raise ValueError(f"{path.name} has no complete target/library inventory.")
    target_names = {".NETCoreApp,Version=v10.0", expected_target}
    if set(targets) != target_names or not all(isinstance(item, dict) for item in targets.values()):
        raise ValueError(f"{path.name} has unexpected dependency targets.")
    target_identities = set().union(*(set(item) for item in targets.values()))
    if target_identities != set(libraries):
        raise ValueError(f"{path.name} target and library identities disagree.")
    identities = {identity.casefold() for identity in target_identities}
    vulnerable = sorted(VULNERABLE_LIBRARY_IDENTITIES & identities)
    if vulnerable:
        raise ValueError(f"{graph} retains vulnerable package identities: {', '.join(vulnerable)}")
    required = REQUIRED_LIBRARY_IDENTITIES[graph]
    if graph == "core" and rid == "linux-x64":
        required = required | {"opentk.redist.glfw/3.4.0.44"}
    if not required.issubset(identities):
        raise ValueError(f"{graph} dependency manifest is missing required package identities.")
    runtime = f"runtimepack.microsoft.netcore.app.runtime.{rid}/10.0.13"
    if runtime not in identities:
        raise ValueError(f"{graph} does not use the pinned patched runtime pack: {runtime}")
    return {
        identity: metadata
        for identity, metadata in libraries.items()
        if isinstance(metadata, dict) and metadata.get("type") == "package"
    }


def verify_package_inventory(
    root: pathlib.Path, rid: str, value: Any
) -> None:
    if not isinstance(value, dict) or set(value) != set(common.SUPPORTED_RIDS):
        raise ValueError("Package path policy does not cover every RID.")
    policy = value.get(rid)
    if not isinstance(policy, dict) or set(policy) != PACKAGE_INVENTORY_KEYS:
        raise ValueError(f"Package path policy is invalid for {rid}.")
    expected_files = policy.get("files")
    if not isinstance(expected_files, int) or isinstance(expected_files, bool) or expected_files < 1:
        raise ValueError(f"Package path count is invalid for {rid}.")
    require_sha256(policy.get("sha256"), f"Package path inventory {rid}")
    paths = sorted(
        path.relative_to(root).as_posix()
        for path in common.regular_files(root, PACKAGE_INVENTORY_EXCLUDED)
    )
    digest = hashlib.sha256(common.canonical_json({"paths": paths})).hexdigest()
    if len(paths) != expected_files or digest != policy["sha256"]:
        raise ValueError("Package paths do not match the independently reviewed inventory.")


def verify_native_payloads(root: pathlib.Path, rid: str, value: Any) -> None:
    if not isinstance(value, dict) or set(value) != set(common.SUPPORTED_RIDS):
        raise ValueError("Native payload policy does not cover every RID.")
    policy = value.get(rid)
    if not isinstance(policy, dict) or not policy:
        raise ValueError(f"Native payload policy is invalid for {rid}.")
    expected: dict[str, str] = {}
    for raw_path, digest in policy.items():
        if not isinstance(raw_path, str):
            raise ValueError(f"Native payload path is invalid for {rid}.")
        relative = str(common.safe_relative(raw_path))
        require_sha256(digest, f"Native payload {relative}")
        expected[relative] = digest
    observed: dict[str, str] = {}
    for path in common.regular_files(root):
        with path.open("rb") as stream:
            if stream.read(4) != b"\x7fELF":
                continue
        relative = path.relative_to(root).as_posix()
        observed[relative] = common.sha256_file(path)
    if observed != expected:
        raise ValueError("Native payloads do not match the independently reviewed inventory.")


def verify_corresponding_source(root: pathlib.Path, value: Any) -> None:
    if not isinstance(value, dict) or set(value) != SOURCE_KEYS:
        raise ValueError("Corresponding-source policy is invalid.")
    relative = value.get("path")
    if relative != "licenses/totk-dfps-source":
        raise ValueError("Corresponding-source path is invalid.")
    expected_files = value.get("files")
    if not isinstance(expected_files, int) or isinstance(expected_files, bool) or expected_files < 1:
        raise ValueError("Corresponding-source file count is invalid.")
    require_sha256(value.get("sha256"), "Corresponding-source tree")
    source = root.joinpath(*pathlib.PurePosixPath(relative).parts)
    actual_files, actual_digest = content_tree_identity(source)
    if actual_files != expected_files or actual_digest != value["sha256"]:
        raise ValueError("Corresponding-source tree does not match the reviewed complete inventory.")


def content_tree_identity(root: pathlib.Path) -> tuple[int, str]:
    records: common.JsonObject = {}
    for path in common.regular_files(root):
        relative = path.relative_to(root).as_posix()
        records[relative] = {
            "bytes": path.stat().st_size,
            "sha256": common.sha256_file(path),
        }
    digest = hashlib.sha256(common.canonical_json({"files": records})).hexdigest()
    return len(records), digest


def verify_managed_package_notices(
    root: pathlib.Path,
    rid: str,
    value: Any,
    packages: dict[str, common.JsonObject],
) -> None:
    if not isinstance(value, dict) or set(value) != set(common.SUPPORTED_RIDS):
        raise ValueError("Managed package notice policy does not cover every RID.")
    policy = value.get(rid)
    if not isinstance(policy, dict) or set(policy) != PACKAGE_NOTICE_KEYS:
        raise ValueError(f"Managed package notice policy is invalid for {rid}.")
    expected_files = policy.get("files")
    if not isinstance(expected_files, int) or isinstance(expected_files, bool) or expected_files < 1:
        raise ValueError(f"Managed package notice file count is invalid for {rid}.")
    require_sha256(policy.get("sha256"), f"Managed package notice tree {rid}")
    notice_root = root / "licenses/packages"
    actual_files, actual_digest = content_tree_identity(notice_root)
    if actual_files != expected_files or actual_digest != policy["sha256"]:
        raise ValueError("Managed package notice tree does not match the reviewed policy.")
    metadata_path = notice_root / "PACKAGE-METADATA.json"
    metadata = common.strict_json(metadata_path.read_bytes(), "PACKAGE-METADATA.json")
    common.require_schema(metadata, 1, "PACKAGE-METADATA.json")
    if set(metadata) != {"packages", "schemaVersion"}:
        raise ValueError("Managed package metadata has unexpected fields.")
    records = metadata.get("packages")
    if not isinstance(records, list) or not all(isinstance(record, dict) for record in records):
        raise ValueError("Managed package metadata has an invalid package inventory.")
    by_identity: dict[str, common.JsonObject] = {}
    for record in records:
        if set(record) != PACKAGE_METADATA_KEYS:
            raise ValueError("Managed package metadata record has unexpected fields.")
        identity = record.get("identity")
        if not isinstance(identity, str) or identity in by_identity:
            raise ValueError("Managed package metadata contains a duplicate identity.")
        strings = (
            "authors",
            "declaredLicense",
            "graphContentHash",
            "nupkgSha512",
            "nuspecPath",
        )
        if not all(isinstance(record.get(key), str) and record[key] for key in strings):
            raise ValueError(f"Managed package metadata is incomplete: {identity}")
        if not isinstance(record.get("copyright"), str) or not isinstance(
            record.get("projectUrl"), str
        ):
            raise ValueError(f"Managed package attribution is invalid: {identity}")
        if not isinstance(record.get("repository"), dict):
            raise ValueError(f"Managed package repository metadata is invalid: {identity}")
        require_sha256(record.get("nuspecSha256"), f"Managed package nuspec {identity}")
        require_sha256(record.get("nupkgSha256"), f"Managed package archive {identity}")
        notices = record.get("notices")
        if not isinstance(notices, list) or not notices:
            raise ValueError(f"Managed package has no license evidence: {identity}")
        for notice in notices:
            verify_package_notice_record(notice_root, notice, identity)
        by_identity[identity] = record
    if set(by_identity) != set(packages):
        raise ValueError("Managed package notices do not exactly cover dependency packages.")
    for identity, graph_metadata in packages.items():
        expected_hash = graph_metadata.get("sha512")
        if by_identity[identity]["graphContentHash"] != expected_hash:
            raise ValueError(f"Managed package content hash mismatch: {identity}")


def verify_package_notice_record(
    root: pathlib.Path, value: Any, identity: str
) -> None:
    if not isinstance(value, dict) or set(value) != {"path", "sha256"}:
        raise ValueError(f"Managed package notice record is invalid: {identity}")
    raw_path = value.get("path")
    if not isinstance(raw_path, str):
        raise ValueError(f"Managed package notice path is invalid: {identity}")
    relative = common.safe_relative(raw_path)
    require_sha256(value.get("sha256"), f"Managed package notice {identity}/{raw_path}")
    path = root.joinpath(*relative.parts)
    if path.is_symlink() or not path.is_file() or common.sha256_file(path) != value["sha256"]:
        raise ValueError(f"Managed package notice identity mismatch: {identity}/{raw_path}")


def verify_producer_codeview_paths(root: pathlib.Path, value: Any) -> None:
    if not isinstance(value, list) or not all(isinstance(record, dict) for record in value):
        raise ValueError("Producer CodeView path policy is invalid.")
    expected: list[common.JsonObject] = []
    for record in value:
        if set(record) != CODEVIEW_KEYS:
            raise ValueError("Producer CodeView path record has unexpected fields.")
        assembly = record.get("assembly")
        producer_path = record.get("path")
        if not isinstance(assembly, str) or not isinstance(producer_path, str):
            raise ValueError("Producer CodeView path record is invalid.")
        common.safe_relative(assembly)
        require_sha256(record.get("assemblySha256"), f"Producer assembly {assembly}")
        expected.append(record)
    observed: list[common.JsonObject] = []
    for assembly in portable_executables(root):
        relative = assembly.relative_to(root).as_posix()
        data = assembly.read_bytes()
        digest = common.sha256_file(assembly)
        for producer_path in debug_record_paths(data, relative):
            if is_absolute_producer_path(producer_path):
                observed.append(
                    {
                        "assembly": relative,
                        "assemblySha256": digest,
                        "path": producer_path,
                    }
                )
    key = lambda record: (record["assembly"], record["path"])
    if sorted(observed, key=key) != sorted(expected, key=key):
        raise ValueError("Managed assemblies contain unreviewed producer CodeView paths.")


def verify_embedded_portable_pdbs(root: pathlib.Path, value: Any) -> None:
    if not isinstance(value, list) or not all(
        isinstance(record, dict) and set(record) == EMBEDDED_PDB_KEYS for record in value
    ):
        raise ValueError("Embedded portable PDB policy is invalid.")
    for record in value:
        assembly = record.get("assembly")
        if not isinstance(assembly, str):
            raise ValueError("Embedded portable PDB record is invalid.")
        common.safe_relative(assembly)
        require_sha256(record.get("assemblySha256"), f"Embedded PDB assembly {assembly}")
    observed = [
        {"assembly": path.relative_to(root).as_posix(), "assemblySha256": common.sha256_file(path)}
        for path in portable_executables(root)
        if DEBUG_EMBEDDED_PORTABLE_PDB in debug_types(path.read_bytes(), path.name)
    ]
    key = lambda record: record["assembly"]
    if sorted(observed, key=key) != sorted(value, key=key):
        raise ValueError("Managed assemblies contain unreviewed embedded portable PDBs.")


def portable_executables(root: pathlib.Path) -> list[pathlib.Path]:
    result = []
    for path in common.regular_files(root):
        with path.open("rb") as stream:
            magic = stream.read(2)
        if magic == b"MZ" or path.suffix.casefold() in PORTABLE_EXECUTABLE_SUFFIXES:
            result.append(path)
    return result


def debug_directory(data: bytes, label: str) -> list[tuple[int, bytes]]:
    """Return (type, raw data) for every entry of a PE file's debug directory."""
    try:
        if data[:2] != b"MZ":
            raise ValueError("missing MZ header")
        header = struct.unpack_from("<I", data, 0x3C)[0]
        if data[header:header + 4] != b"PE\0\0":
            raise ValueError("missing PE signature")
        sections, optional_size = struct.unpack_from("<H12xH", data, header + 6)
        optional = header + 24
        magic = struct.unpack_from("<H", data, optional)[0]
        directories = {0x10B: 96, 0x20B: 112}.get(magic)
        if directories is None:
            raise ValueError("unsupported optional header")
        count = struct.unpack_from("<I", data, optional + directories - 4)[0]
        if count <= 6:
            return []
        address, size = struct.unpack_from("<II", data, optional + directories + 6 * 8)
        if size == 0:
            return []
        if size % 28:
            raise ValueError("debug directory size is not a whole number of entries")
        offset = file_offset(data, optional + optional_size, sections, address)
        entries = []
        for index in range(size // 28):
            _, _, _, _, kind, length, _, pointer = struct.unpack_from(
                "<IIHHIIII", data, offset + index * 28
            )
            if pointer + length > len(data):
                raise ValueError("debug data is outside the file")
            entries.append((kind, data[pointer:pointer + length]))
        return entries
    except (struct.error, ValueError) as error:
        raise ValueError(f"Portable executable debug metadata is malformed: {label}: {error}") from error


def file_offset(data: bytes, table: int, sections: int, address: int) -> int:
    for index in range(sections):
        virtual_size, virtual_address, raw_size, raw_pointer = struct.unpack_from(
            "<IIII", data, table + index * 40 + 8
        )
        if virtual_address <= address < virtual_address + max(virtual_size, raw_size):
            offset = raw_pointer + address - virtual_address
            if offset >= len(data):
                break
            return offset
    raise ValueError("debug directory is outside every section")


def debug_types(data: bytes, label: str) -> set[int]:
    return {kind for kind, _ in debug_directory(data, label)}


def debug_record_paths(data: bytes, label: str) -> list[str]:
    paths = []
    entries = debug_directory(data, label)
    if not entries:
        raise ValueError(f"Portable executable has no debug directory: {label}")
    for kind, record in entries:
        if kind not in DEBUG_TYPES:
            raise ValueError(f"Portable executable has an unreviewed debug record type {kind}: {label}")
        magic = DEBUG_PATH_MAGIC.get(kind)
        if magic is None:
            continue
        if len(record) < 25 or record[:4] != magic or b"\0" not in record[24:]:
            raise ValueError(f"Portable executable debug path record is malformed: {label}")
        raw, padding = record[24:].split(b"\0", 1)
        expected_size = DEBUG_PATH_RECORD_SIZE.get(kind, 24 + len(raw) + 1)
        if not raw or len(record) != expected_size or padding.strip(b"\0"):
            raise ValueError(f"Portable executable debug path record has hidden bytes: {label}")
        try:
            paths.append(raw.decode("utf-8"))
        except UnicodeDecodeError as error:
            raise ValueError(f"Portable executable debug path is not UTF-8: {label}") from error
    return paths


def is_absolute_producer_path(value: str) -> bool:
    """Return whether a debug path needs an exact policy entry.

    Only paths under a known source-mapped `/_/` root and bare file names are
    self-evidently free of producer locations; every other form must be
    explicitly allowlisted.
    """
    if any(
        unicodedata.category(character).startswith(("C", "Z")) and character != " "
        for character in value
    ) or value != value.strip():
        return True
    if value.startswith("/_/") and not any(character in value for character in "\\:"):
        parts = value[3:].split("/")
        return parts[0] not in MAPPED_SOURCE_ROOTS or any(part in {"", ".", ".."} for part in parts)
    return any(separator in value for separator in ("/", "\\", ":"))


def verify_redistribution_sources(
    root: pathlib.Path, rid: str, value: Any
) -> None:
    if not isinstance(value, dict) or set(value) != set(common.SUPPORTED_RIDS):
        raise ValueError("Redistribution source policy does not cover every RID.")
    records = value.get(rid)
    if not isinstance(records, list) or not records:
        raise ValueError(f"Redistribution source policy is empty for {rid}.")
    seen: set[str] = set()
    for record in records:
        if not isinstance(record, dict) or set(record) != SOURCE_RECORD_KEYS:
            raise ValueError("Redistribution source record is invalid.")
        raw_path = record.get("path")
        if not isinstance(raw_path, str) or raw_path in seen:
            raise ValueError("Redistribution source path is invalid or duplicated.")
        seen.add(raw_path)
        relative = common.safe_relative(raw_path)
        require_sha256(record.get("sha256"), f"Redistribution source {raw_path}")
        path = root.joinpath(*relative.parts)
        if path.is_symlink() or not path.is_file() or common.sha256_file(path) != record["sha256"]:
            raise ValueError(f"Redistribution source identity mismatch: {raw_path}")
    source_root = root / "licenses/sources"
    observed = {
        path.relative_to(root).as_posix()
        for path in common.regular_files(source_root)
    }
    if observed != seen:
        raise ValueError("Redistribution source tree contains missing or unreviewed files.")


def require_sha256(value: Any, label: str) -> None:
    if (
        not isinstance(value, str)
        or len(value) != 64
        or any(character not in "0123456789abcdef" for character in value)
    ):
        raise ValueError(f"{label} has an invalid SHA-256.")
