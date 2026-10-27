#!/usr/bin/env python3
"""Restore the buildable v1 core source tree from pinned, verified inputs."""

from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import shutil
import subprocess
import sys
from dataclasses import dataclass
from typing import Any


HERE = pathlib.Path(__file__).resolve().parent
REPOSITORY = HERE.parents[1]
RECOVERY_MANIFEST = HERE / "sources.json"
RELEASE_MANIFEST = HERE / "NX-BUILD-SOURCES.json"
JsonObject = dict[str, Any]
SourceMetadata = tuple[str, int]


@dataclass(frozen=True)
class RecoveryPlan:
    source_revision: str
    upstream_revision: str
    source_manifest_sha256: str
    kenji_revision: str
    later_revision: str
    coverage: dict[str, int]
    source_entries: JsonObject
    core_entries: dict[str, SourceMetadata]
    history: dict[str, JsonObject]
    later_sources: dict[str, JsonObject]
    reconstructed: dict[str, JsonObject]
    unavailable: dict[str, JsonObject]
    auxiliary: dict[str, JsonObject]
    linked_runtime: dict[str, JsonObject]


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def relative_path(value: str) -> pathlib.PurePosixPath:
    windows = pathlib.PureWindowsPath(value)
    path = pathlib.PurePosixPath(value)
    if (
        "\\" in value
        or ":" in value
        or "\0" in value
        or windows.drive
        or windows.root
        or path.is_absolute()
        or not path.parts
        or any(part in {"", ".", ".."} for part in path.parts)
    ):
        raise ValueError(f"Unsafe recovery path: {value!r}")
    return path


def local_path(root: pathlib.Path, value: str) -> pathlib.Path:
    relative = relative_path(value)
    base = root.resolve()
    candidate = base.joinpath(*relative.parts)
    if not candidate.resolve(strict=False).is_relative_to(base):
        raise ValueError(f"Recovery path escapes its root: {value!r}")
    return candidate


def read_regular_file(path: pathlib.Path) -> bytes:
    if path.is_symlink() or not path.is_file():
        raise FileNotFoundError(f"Required regular file is missing: {path}")
    return path.read_bytes()


def verify_bytes(data: bytes, expected_sha256: str, expected_bytes: int, label: str) -> None:
    actual_sha256 = sha256(data)
    if len(data) != expected_bytes or actual_sha256 != expected_sha256:
        raise ValueError(
            f"{label} failed verification: expected {expected_bytes} bytes/{expected_sha256}, "
            f"got {len(data)} bytes/{actual_sha256}"
        )


def git(repository: pathlib.Path, *arguments: str, binary: bool = False) -> str | bytes:
    command = ["git", "-C", str(repository), *arguments]
    return subprocess.check_output(command, text=not binary)


def require_revision(repository: pathlib.Path, expected: str, label: str) -> None:
    actual = str(git(repository, "rev-parse", "HEAD")).strip()
    if actual != expected:
        raise ValueError(f"{label} must be checked out at {expected}; found {actual}")


def read_revision_file(repository: pathlib.Path, revision: str, relative: str) -> bytes:
    path = str(relative_path(relative))
    return bytes(git(repository, "cat-file", "blob", f"{revision}:{path}", binary=True))


def read_history_file(repository: pathlib.Path, relative: str, entry: JsonObject) -> bytes:
    path = str(relative_path(relative))
    commit = require_git_oid(entry.get("gitCommit"), f"Kenji history source {path} Git commit")
    expected_blob = require_git_oid(entry.get("gitBlob"), f"Kenji history source {path} Git blob")
    actual_blob = str(git(repository, "rev-parse", f"{commit}:{path}")).strip()
    if actual_blob != expected_blob:
        raise ValueError(
            f"Kenji history source {path} is not blob {expected_blob} at commit {commit}; "
            f"found {actual_blob}"
        )
    return read_revision_file(repository, commit, path)


def write_file(output: pathlib.Path, relative: str, data: bytes) -> None:
    destination = local_path(output, relative)
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_bytes(data)


def load_json(path: pathlib.Path) -> JsonObject:
    def object_without_duplicates(pairs: list[tuple[str, Any]]) -> JsonObject:
        value: JsonObject = {}
        for key, item in pairs:
            if key in value:
                raise ValueError(f"Duplicate JSON field {key!r}: {path}")
            value[key] = item
        return value

    value = json.loads(read_regular_file(path), object_pairs_hook=object_without_duplicates)
    if not isinstance(value, dict):
        raise ValueError(f"Expected a JSON object: {path}")
    return value


def mapping_field(container: JsonObject, key: str, label: str) -> JsonObject:
    value = container.get(key)
    if not isinstance(value, dict):
        raise ValueError(f"{label} field {key!r} must be an object.")
    return value


def indexed_entries(container: JsonObject, key: str) -> dict[str, JsonObject]:
    value = container.get(key)
    if not isinstance(value, list):
        raise ValueError(f"Recovery manifest field {key!r} must be an array.")

    result: dict[str, JsonObject] = {}
    for index, entry in enumerate(value):
        if not isinstance(entry, dict) or not isinstance(entry.get("path"), str):
            raise ValueError(f"Recovery manifest field {key!r} has an invalid entry at index {index}.")
        path = str(relative_path(entry["path"]))
        if path != entry["path"] or path in result:
            raise ValueError(f"Recovery manifest field {key!r} has a repeated or non-canonical path: {path!r}")
        result[path] = entry
    return result


def require_lower_hex(value: Any, length: int, label: str) -> str:
    if not isinstance(value, str) or len(value) != length:
        raise ValueError(f"{label} must contain {length} lowercase hexadecimal characters.")
    if any(character not in "0123456789abcdef" for character in value):
        raise ValueError(f"{label} must contain {length} lowercase hexadecimal characters.")
    return value


def require_sha256(value: Any, label: str) -> str:
    return require_lower_hex(value, 64, label)


def require_git_oid(value: Any, label: str) -> str:
    return require_lower_hex(value, 40, label)


def require_byte_count(value: Any, label: str) -> int:
    if not isinstance(value, int) or isinstance(value, bool) or value < 0:
        raise ValueError(f"{label} must be a non-negative integer byte count.")
    return value


def collect_core_entries(source_entries: JsonObject) -> dict[str, SourceMetadata]:
    result: dict[str, SourceMetadata] = {}
    for release_path, metadata in source_entries.items():
        if not isinstance(release_path, str) or not isinstance(metadata, dict):
            raise ValueError("Release source manifest contains an invalid source entry.")
        if not release_path.startswith("core/"):
            continue
        relative = release_path.removeprefix("core/")
        if str(relative_path(relative)) != relative:
            raise ValueError(f"Release source manifest has a non-canonical path: {release_path!r}")
        result[relative] = (
            require_sha256(metadata.get("sha256"), f"Release source {release_path}"),
            require_byte_count(metadata.get("bytes"), f"Release source {release_path}"),
        )
    return result


def verify_exact_entry(
    core_entries: dict[str, SourceMetadata],
    path: str,
    entry: JsonObject,
    hash_key: str,
    bytes_key: str,
) -> None:
    expected_sha256, expected_bytes = core_entries[path]
    if require_sha256(entry.get(hash_key), f"Recovery source {path}") != expected_sha256:
        raise ValueError(f"Recovery source hash disagrees with the release manifest: {path}")
    if require_byte_count(entry.get(bytes_key), f"Recovery source {path}") != expected_bytes:
        raise ValueError(f"Recovery source byte count disagrees with the release manifest: {path}")


def validate_classifications(
    core_entries: dict[str, SourceMetadata],
    classifications: dict[str, dict[str, JsonObject]],
) -> dict[str, str]:
    classified_by: dict[str, str] = {}
    for category, entries in classifications.items():
        for path in entries:
            if path in classified_by:
                raise ValueError(f"Core source {path!r} has two classifications.")
            if path not in core_entries:
                raise ValueError(f"Classification {category!r} names an unknown core source: {path}")
            classified_by[path] = category
    return classified_by


def validate_exact_sources(
    core_entries: dict[str, SourceMetadata],
    history: dict[str, JsonObject],
    later_sources: dict[str, JsonObject],
) -> None:
    for path, entry in history.items():
        verify_exact_entry(core_entries, path, entry, "sha256", "bytes")
        require_git_oid(entry.get("gitBlob"), f"Kenji history source {path} Git blob")
        require_git_oid(entry.get("gitCommit"), f"Kenji history source {path} Git commit")
    for path, entry in later_sources.items():
        verify_exact_entry(core_entries, path, entry, "sha256", "bytes")


def validate_reconstructed_sources(
    core_entries: dict[str, SourceMetadata],
    reconstructed: dict[str, JsonObject],
    unavailable: dict[str, JsonObject],
) -> None:
    for entries in (reconstructed, unavailable):
        for path, entry in entries.items():
            verify_exact_entry(core_entries, path, entry, "releasedSha256", "releasedBytes")
    for path, entry in reconstructed.items():
        require_sha256(entry.get("overlaySha256"), f"Reconstructed overlay {path}")
        require_byte_count(entry.get("overlayBytes"), f"Reconstructed overlay {path}")
        require_sha256(entry.get("evidenceAssemblySha256"), f"Evidence assembly for {path}")


def validate_auxiliary(
    recovery: JsonObject,
    core_entries: dict[str, SourceMetadata],
) -> dict[str, JsonObject]:
    auxiliary = indexed_entries(recovery, "auxiliary")
    overlap = sorted(set(auxiliary).intersection(core_entries))
    if overlap:
        raise ValueError(f"Auxiliary source is already represented by the release manifest: {overlap[0]}")
    for path, entry in auxiliary.items():
        require_sha256(entry.get("sha256"), f"Auxiliary source {path}")
        require_byte_count(entry.get("bytes"), f"Auxiliary source {path}")
    return auxiliary


def validate_linked_runtime(recovery: JsonObject, source_entries: JsonObject) -> dict[str, JsonObject]:
    linked_runtime = indexed_entries(recovery, "linkedRuntimeSources")
    for path, entry in linked_runtime.items():
        if not path.startswith("src/runtime/NxInfinity.Runtime/"):
            raise ValueError(f"Linked runtime source is outside NxInfinity.Runtime: {path}")
        release_path = path.removeprefix("src/")
        metadata = source_entries.get(release_path)
        if not isinstance(metadata, dict):
            raise ValueError(f"Linked runtime source is absent from the release manifest: {path}")
        expected = (
            require_sha256(metadata.get("sha256"), f"Release source {release_path}"),
            require_byte_count(metadata.get("bytes"), f"Release source {release_path}"),
        )
        actual = (
            require_sha256(entry.get("sha256"), f"Linked runtime source {path}"),
            require_byte_count(entry.get("bytes"), f"Linked runtime source {path}"),
        )
        if actual != expected:
            raise ValueError(f"Linked runtime source disagrees with the release manifest: {path}")
    return linked_runtime


def normalized_coverage(recovery: JsonObject) -> dict[str, int]:
    coverage = mapping_field(recovery, "coverage", "Recovery manifest")
    keys = (
        "coreManifestFiles",
        "exactAtKenjiRevision",
        "exactKenjiHistory",
        "exactLaterRyujinx",
        "reconstructedFromManagedIL",
        "unavailable",
        "auxiliaryDecompilerSplits",
    )
    return {key: require_byte_count(coverage.get(key), f"Recovery coverage {key}") for key in keys}


def validate_coverage(plan: RecoveryPlan, classified_by: dict[str, str]) -> None:
    measured = {
        "coreManifestFiles": len(plan.core_entries),
        "exactAtKenjiRevision": len(plan.core_entries) - len(classified_by),
        "exactKenjiHistory": len(plan.history),
        "exactLaterRyujinx": len(plan.later_sources),
        "reconstructedFromManagedIL": len(plan.reconstructed),
        "unavailable": len(plan.unavailable),
        "auxiliaryDecompilerSplits": len(plan.auxiliary),
    }
    if measured != plan.coverage:
        raise ValueError(f"Recovery coverage changed: expected {plan.coverage!r}; measured {measured!r}")


def load_plan() -> RecoveryPlan:
    recovery = load_json(RECOVERY_MANIFEST)
    release = load_json(RELEASE_MANIFEST)
    if recovery.get("schemaVersion") != 1 or release.get("schemaVersion") != 1:
        raise ValueError("Unsupported source recovery manifest schema.")

    release_info = mapping_field(recovery, "release", "Recovery manifest")
    repositories = mapping_field(recovery, "repositories", "Recovery manifest")
    source_entries = mapping_field(release, "sources", "Release source manifest")
    source_revision = require_git_oid(release_info.get("sourceRevision"), "Release source revision")
    upstream_revision = require_git_oid(release_info.get("upstreamRevision"), "Release upstream revision")
    manifest_sha256 = require_sha256(release_info.get("sourceManifestSha256"), "Release source manifest")
    if sha256(read_regular_file(RELEASE_MANIFEST)) != manifest_sha256:
        raise ValueError("The checked-in release source manifest has changed.")
    if release.get("sourceRevision") != source_revision or release.get("upstreamRevision") != upstream_revision:
        raise ValueError("Release revisions do not match the recovery manifest.")

    history = indexed_entries(recovery, "exactKenjiHistory")
    later_sources = indexed_entries(recovery, "exactLaterRyujinx")
    reconstructed = indexed_entries(recovery, "reconstructed")
    unavailable = indexed_entries(recovery, "unavailable")
    core_entries = collect_core_entries(source_entries)
    classifications = {
        "exactKenjiHistory": history,
        "exactLaterRyujinx": later_sources,
        "reconstructed": reconstructed,
        "unavailable": unavailable,
    }
    classified_by = validate_classifications(core_entries, classifications)
    validate_exact_sources(core_entries, history, later_sources)
    validate_reconstructed_sources(core_entries, reconstructed, unavailable)

    kenji = mapping_field(repositories, "kenji", "Recovery repositories")
    later = mapping_field(repositories, "laterRyujinx", "Recovery repositories")
    plan = RecoveryPlan(
        source_revision=source_revision,
        upstream_revision=upstream_revision,
        source_manifest_sha256=manifest_sha256,
        kenji_revision=require_git_oid(kenji.get("revision"), "Kenji revision"),
        later_revision=require_git_oid(later.get("revision"), "Later Ryujinx revision"),
        coverage=normalized_coverage(recovery),
        source_entries=source_entries,
        core_entries=core_entries,
        history=history,
        later_sources=later_sources,
        reconstructed=reconstructed,
        unavailable=unavailable,
        auxiliary=validate_auxiliary(recovery, core_entries),
        linked_runtime=validate_linked_runtime(recovery, source_entries),
    )
    if plan.kenji_revision != upstream_revision:
        raise ValueError("Pinned Kenji revision does not match the release upstream revision.")
    validate_coverage(plan, classified_by)
    return plan


def kenji_revisions(plan: RecoveryPlan) -> list[str]:
    history_revisions = {str(entry["gitCommit"]) for entry in plan.history.values()}
    history_revisions.discard(plan.kenji_revision)
    return [plan.kenji_revision, *sorted(history_revisions)]


def restore_core(
    plan: RecoveryPlan,
    kenji: pathlib.Path,
    later: pathlib.Path,
    output: pathlib.Path,
) -> dict[str, int]:
    counts = {"kenji": 0, "kenjiHistory": 0, "laterRyujinx": 0, "reconstructed": 0}
    for relative, (expected_sha256, expected_bytes) in sorted(plan.core_entries.items()):
        if relative in plan.unavailable:
            continue
        if relative in plan.reconstructed:
            entry = plan.reconstructed[relative]
            data = read_regular_file(local_path(REPOSITORY / "src/core/overlays", relative))
            verify_bytes(data, entry["overlaySha256"], entry["overlayBytes"], f"overlay {relative}")
            counts["reconstructed"] += 1
        elif relative in plan.later_sources:
            data = read_revision_file(later, plan.later_revision, relative)
            verify_bytes(data, expected_sha256, expected_bytes, f"later source {relative}")
            counts["laterRyujinx"] += 1
        elif relative in plan.history:
            data = read_history_file(kenji, relative, plan.history[relative])
            verify_bytes(data, expected_sha256, expected_bytes, f"Kenji history source {relative}")
            counts["kenjiHistory"] += 1
        else:
            data = read_revision_file(kenji, plan.kenji_revision, relative)
            verify_bytes(data, expected_sha256, expected_bytes, f"Kenji source {relative}")
            counts["kenji"] += 1
        write_file(output, relative, data)
    return counts


def restore_auxiliary(plan: RecoveryPlan, output: pathlib.Path) -> None:
    for path, entry in plan.auxiliary.items():
        data = read_regular_file(local_path(REPOSITORY / "src/core/overlays", path))
        verify_bytes(data, entry["sha256"], entry["bytes"], f"auxiliary source {path}")
        write_file(output, path, data)


def restore_runtime(plan: RecoveryPlan, output: pathlib.Path) -> None:
    for path, entry in plan.linked_runtime.items():
        source = local_path(REPOSITORY, path)
        data = read_regular_file(source)
        verify_bytes(data, entry["sha256"], entry["bytes"], f"runtime source {path}")
        write_file(output, f"runtime/NxInfinity.Runtime/{source.name}", data)


def verify_restored_counts(plan: RecoveryPlan, counts: dict[str, int]) -> None:
    expected = {
        "kenji": plan.coverage["exactAtKenjiRevision"],
        "kenjiHistory": plan.coverage["exactKenjiHistory"],
        "laterRyujinx": plan.coverage["exactLaterRyujinx"],
        "reconstructed": plan.coverage["reconstructedFromManagedIL"],
    }
    if counts != expected or sum(counts.values()) + len(plan.unavailable) != len(plan.core_entries):
        raise ValueError(f"Restored source counts changed: expected {expected!r}; restored {counts!r}")


def write_receipt(plan: RecoveryPlan, counts: dict[str, int], output: pathlib.Path) -> JsonObject:
    receipt = {
        "schemaVersion": 1,
        "sourceRevision": plan.source_revision,
        "upstreamRevision": plan.upstream_revision,
        "sourceManifestSha256": plan.source_manifest_sha256,
        "restored": counts,
        "unavailable": sorted(plan.unavailable),
        "auxiliaryDecompilerSplits": len(plan.auxiliary),
    }
    (output / "SOURCE-RECOVERY-RECEIPT.json").write_text(
        json.dumps(receipt, indent=2) + "\n", encoding="utf-8"
    )
    return receipt


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Restore the v1 core source from pinned Kenji commit/path sources, "
            "later Ryujinx, and reviewed managed-IL reconstructions."
        )
    )
    parser.add_argument("--print-kenji-revisions", action="store_true")
    parser.add_argument("--kenji-repo", type=pathlib.Path)
    parser.add_argument("--later-repo", type=pathlib.Path)
    parser.add_argument("--output", type=pathlib.Path)
    arguments = parser.parse_args()
    repository_arguments = (arguments.kenji_repo, arguments.later_repo, arguments.output)
    if arguments.print_kenji_revisions:
        if any(value is not None for value in repository_arguments):
            parser.error("--print-kenji-revisions cannot be combined with restoration arguments.")
    elif any(value is None for value in repository_arguments):
        parser.error("--kenji-repo, --later-repo and --output are required for restoration.")
    return arguments


def main() -> int:
    arguments = parse_arguments()
    plan = load_plan()
    if arguments.print_kenji_revisions:
        sys.stdout.write("\n".join(kenji_revisions(plan)) + "\n")
        return 0

    assert arguments.kenji_repo is not None
    assert arguments.later_repo is not None
    assert arguments.output is not None
    kenji = arguments.kenji_repo.resolve()
    later = arguments.later_repo.resolve()
    require_revision(kenji, plan.kenji_revision, "Kenji source")
    require_revision(later, plan.later_revision, "Later Ryujinx source")

    output = arguments.output.resolve()
    if output.exists():
        raise FileExistsError(f"Output path already exists; recovery never overwrites it: {output}")
    output.mkdir(parents=True)

    counts = restore_core(plan, kenji, later, output)
    restore_auxiliary(plan, output)
    restore_runtime(plan, output)
    verify_restored_counts(plan, counts)
    shutil.copyfile(REPOSITORY / "licenses/Ryujinx-MIT.txt", output / "LICENSE.txt")
    shutil.copyfile(REPOSITORY / "licenses/Ryujinx-Team-MIT.txt", output / "RYUJINX-LICENSE.txt")
    receipt = write_receipt(plan, counts, output)
    json.dump(receipt, sys.stdout, indent=2)
    sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
