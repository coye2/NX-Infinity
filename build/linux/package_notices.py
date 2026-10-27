#!/usr/bin/env python3
"""Generate complete, deterministic NuGet package notice evidence."""

from __future__ import annotations

import base64
import hashlib
import json
import pathlib
import zipfile
import xml.etree.ElementTree as ElementTree
from typing import Any

import common


HERE = pathlib.Path(__file__).resolve().parent
OUTPUT_ROOT = pathlib.PurePosixPath("licenses/packages")
NOTICE_BASENAMES = {
    "copying",
    "copying.md",
    "copying.txt",
    "licence",
    "licence.md",
    "licence.txt",
    "license",
    "license.md",
    "license.txt",
    "notice",
    "notice.md",
    "notice.txt",
    "third-party-notices.txt",
    "thirdpartynotices.txt",
}
CANONICAL_LICENSES = {
    "Apache-2.0": HERE / "notices/Shaderc-Apache-2.0.txt",
    "LGPL-2.1-only": HERE / "notices/FFmpeg-LGPL-2.1-or-later.txt",
    "MIT": HERE / "notices/Package-MIT.txt",
}
LICENSE_URLS = {
    "http://opensource.org/licenses/MIT": "MIT",
    "http://www.apache.org/licenses/LICENSE-2.0": "Apache-2.0",
}
LICENSE_OVERRIDES = {
    "gommon/2.7.1.1": ("MIT", "content/LICENSE"),
    "shaderc.net/0.1.0": ("MIT", None),
}
STATIC_NOTICE_OVERRIDES = {
    "shaderc.net/0.1.0": HERE / "notices/Shaderc.net-MIT.txt",
}


def generate(root: pathlib.Path, nuget_root: pathlib.Path) -> tuple[int, str]:
    output = root.joinpath(*OUTPUT_ROOT.parts)
    if output.exists() or output.is_symlink():
        raise FileExistsError(f"Package notice destination already exists: {output}")
    output.mkdir(parents=True)
    packages = package_libraries(root)
    records: list[common.JsonObject] = []
    attribution_lines = [
        "# Managed package attributions",
        "",
        "This inventory covers every NuGet package in the launcher and core dependency graphs.",
        "Package archive identities and extracted notice bytes are recorded in PACKAGE-METADATA.json.",
        "",
    ]
    for identity in sorted(packages, key=str.casefold):
        record = package_record(identity, packages[identity], output, nuget_root)
        records.append(record)
        attribution_lines.append(attribution_line(record))
    write_bytes(
        output / "PACKAGE-METADATA.json",
        common.canonical_json({"packages": records, "schemaVersion": 1}),
    )
    write_bytes(
        output / "ATTRIBUTIONS.md",
        ("\n".join(attribution_lines) + "\n").encode("utf-8"),
    )
    return tree_identity(output)


def package_libraries(root: pathlib.Path) -> dict[str, common.JsonObject]:
    result: dict[str, common.JsonObject] = {}
    paths = (root / "NxInfinity.deps.json", root / "cores/nxi/NxInfinity.Core.deps.json")
    for path in paths:
        value = common.strict_json(path.read_bytes(), path.name)
        libraries = value.get("libraries")
        if not isinstance(libraries, dict):
            raise ValueError(f"{path.name} has no library inventory.")
        for identity, metadata in libraries.items():
            if not isinstance(metadata, dict) or metadata.get("type") != "package":
                continue
            if identity in result and result[identity] != metadata:
                raise ValueError(f"Package metadata differs between dependency graphs: {identity}")
            result[identity] = metadata
    if not result:
        raise ValueError("Published products have no NuGet package inventory.")
    return result


def package_record(
    identity: str,
    graph_metadata: common.JsonObject,
    output: pathlib.Path,
    nuget_root: pathlib.Path,
) -> common.JsonObject:
    package_id, version = split_identity(identity)
    expected_sha512 = graph_metadata.get("sha512")
    if not isinstance(expected_sha512, str) or not expected_sha512:
        raise ValueError(f"Dependency graph has no package hash: {identity}")
    package = (
        nuget_root
        / package_id.casefold()
        / version
        / f"{package_id.casefold()}.{version}.nupkg"
    )
    if package.is_symlink() or not package.is_file():
        raise ValueError(f"NuGet package archive is unavailable: {identity}")
    package_bytes = package.read_bytes()
    observed_sha512 = base64.b64encode(hashlib.sha512(package_bytes).digest()).decode("ascii")
    sidecar = package.with_suffix(package.suffix + ".sha512")
    metadata_path = package.parent / ".nupkg.metadata"
    if (
        sidecar.is_symlink()
        or not sidecar.is_file()
        or sidecar.read_text(encoding="ascii").strip() != observed_sha512
    ):
        raise ValueError(f"NuGet package archive SHA-512 mismatch: {identity}")
    cache_metadata = common.strict_json(metadata_path.read_bytes(), f"{identity}/.nupkg.metadata")
    content_hash = cache_metadata.get("contentHash")
    if expected_sha512 != f"sha512-{content_hash}":
        raise ValueError(f"NuGet package content hash mismatch: {identity}")
    with zipfile.ZipFile(package) as archive:
        nuspec_name, nuspec, metadata = read_nuspec(archive, identity)
        declared, license_key, required_member = license_declaration(identity, metadata)
        notice_members = legal_notice_members(archive, required_member)
        notices = write_package_notices(
            output,
            identity,
            archive,
            notice_members,
            license_key,
        )
    return {
        "authors": element_text(metadata, "authors"),
        "copyright": element_text(metadata, "copyright"),
        "declaredLicense": declared,
        "graphContentHash": expected_sha512,
        "identity": identity,
        "notices": notices,
        "nuspecPath": nuspec_name,
        "nuspecSha256": hashlib.sha256(nuspec).hexdigest(),
        "nupkgSha256": hashlib.sha256(package_bytes).hexdigest(),
        "nupkgSha512": observed_sha512,
        "projectUrl": element_text(metadata, "projectUrl"),
        "repository": repository_metadata(metadata),
    }


def read_nuspec(
    archive: zipfile.ZipFile, identity: str
) -> tuple[str, bytes, ElementTree.Element]:
    names = [item.filename for item in archive.infolist() if item.filename.casefold().endswith(".nuspec")]
    if len(names) != 1:
        raise ValueError(f"NuGet package must contain exactly one nuspec: {identity}")
    data = archive.read(names[0])
    document = ElementTree.fromstring(data)
    metadata = next(
        (element for element in document.iter() if local_name(element) == "metadata"),
        None,
    )
    if metadata is None:
        raise ValueError(f"NuGet package nuspec has no metadata: {identity}")
    return names[0], data, metadata


def license_declaration(
    identity: str, metadata: ElementTree.Element
) -> tuple[str, str, str | None]:
    normalized = identity.casefold()
    override = LICENSE_OVERRIDES.get(normalized)
    if override is not None:
        return f"reviewed-override:{override[0]}", override[0], override[1]
    license_element = child(metadata, "license")
    if license_element is not None:
        value = (license_element.text or "").strip()
        kind = license_element.get("type", "")
        if kind == "expression" and value in CANONICAL_LICENSES:
            return f"expression:{value}", value, None
        if kind == "file" and value:
            return f"file:{value}", "package-file", value
        raise ValueError(f"Unsupported NuGet license declaration: {identity}")
    license_url = element_text(metadata, "licenseUrl")
    license_key = LICENSE_URLS.get(license_url)
    if license_key is None:
        raise ValueError(f"NuGet package has no reviewed license declaration: {identity}")
    return f"url:{license_url}", license_key, None


def legal_notice_members(
    archive: zipfile.ZipFile, required_member: str | None
) -> list[str]:
    names = {
        item.filename
        for item in archive.infolist()
        if not item.is_dir()
        and pathlib.PurePosixPath(item.filename).name.casefold() in NOTICE_BASENAMES
    }
    if required_member is not None:
        matches = [item.filename for item in archive.infolist() if item.filename == required_member]
        if len(matches) != 1:
            raise ValueError(f"Declared NuGet license file is missing: {required_member}")
        names.add(required_member)
    return sorted(names)


def write_package_notices(
    output: pathlib.Path,
    identity: str,
    archive: zipfile.ZipFile,
    member_names: list[str],
    license_key: str,
) -> list[common.JsonObject]:
    package_id, version = split_identity(identity)
    package_root = output / "files" / package_id.casefold() / version
    notices: list[common.JsonObject] = []
    for member_name in member_names:
        relative = common.safe_relative(member_name)
        destination = package_root.joinpath(*relative.parts)
        data = archive.read(member_name)
        write_bytes(destination, data)
        notices.append(notice_record(output, destination))
    static = STATIC_NOTICE_OVERRIDES.get(identity.casefold())
    if static is not None:
        destination = package_root / static.name
        write_bytes(destination, static.read_bytes())
        notices.append(notice_record(output, destination))
    canonical = CANONICAL_LICENSES.get(license_key)
    if canonical is not None:
        destination = output / "licenses" / f"{license_key}.txt"
        if not destination.exists():
            write_bytes(destination, canonical.read_bytes())
        notices.append(notice_record(output, destination))
    if not notices:
        raise ValueError(f"NuGet package has no distributable license evidence: {identity}")
    unique = {record["path"]: record for record in notices}
    return [unique[path] for path in sorted(unique)]


def notice_record(root: pathlib.Path, path: pathlib.Path) -> common.JsonObject:
    return {
        "path": path.relative_to(root).as_posix(),
        "sha256": common.sha256_file(path),
    }


def attribution_line(record: common.JsonObject) -> str:
    authors = record["authors"] or "not stated"
    copyright_value = record["copyright"] or "not stated"
    return (
        f"- `{record['identity']}` — {record['declaredLicense']}; "
        f"authors: {authors}; copyright: {copyright_value}"
    )


def repository_metadata(metadata: ElementTree.Element) -> common.JsonObject:
    repository = child(metadata, "repository")
    return dict(sorted(repository.attrib.items())) if repository is not None else {}


def child(metadata: ElementTree.Element, name: str) -> ElementTree.Element | None:
    return next((element for element in metadata if local_name(element) == name), None)


def element_text(metadata: ElementTree.Element, name: str) -> str:
    element = child(metadata, name)
    return (element.text or "").strip() if element is not None else ""


def local_name(element: ElementTree.Element) -> str:
    return element.tag.rsplit("}", 1)[-1]


def split_identity(identity: str) -> tuple[str, str]:
    if "/" not in identity:
        raise ValueError(f"Invalid NuGet package identity: {identity}")
    package_id, version = identity.rsplit("/", 1)
    if not package_id or not version:
        raise ValueError(f"Invalid NuGet package identity: {identity}")
    return package_id, version


def tree_identity(root: pathlib.Path) -> tuple[int, str]:
    records: common.JsonObject = {}
    for path in common.regular_files(root):
        relative = path.relative_to(root).as_posix()
        records[relative] = {
            "bytes": path.stat().st_size,
            "sha256": common.sha256_file(path),
        }
    digest = hashlib.sha256(common.canonical_json({"files": records})).hexdigest()
    return len(records), digest


def write_bytes(path: pathlib.Path, data: bytes) -> None:
    if path.exists() or path.is_symlink():
        if path.is_symlink() or not path.is_file() or path.read_bytes() != data:
            raise ValueError(f"Package notice collision: {path}")
        return
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)
