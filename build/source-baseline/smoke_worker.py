#!/usr/bin/env python3
"""Exercise the rebuilt core host's dependency inventory and protocol hello path."""

from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import subprocess
from typing import Any


JsonObject = dict[str, Any]
AssetRecord = tuple[str, str, str, JsonObject]
HERE = pathlib.Path(__file__).resolve().parent
PUBLISH_INVENTORY = HERE / "host-publish-inventory.json"
SOURCE_REVISION = "67591deee07ab2ce86fc9b72c42e8b64c95ba1b3"
ASSET_KINDS = ("runtime", "resources", "native", "runtimeTargets")


def load_object(path: pathlib.Path, maximum_bytes: int, label: str) -> JsonObject:
    def unique_object(pairs: list[tuple[str, Any]]) -> JsonObject:
        result: JsonObject = {}
        for key, value in pairs:
            if key in result:
                raise RuntimeError(f"{label} contains duplicate JSON field {key!r}.")
            result[key] = value
        return result

    if path.is_symlink() or not path.is_file():
        raise RuntimeError(f"{label} is missing: {path}")
    if path.stat().st_size > maximum_bytes:
        raise RuntimeError(f"{label} exceeds {maximum_bytes} bytes.")
    value = json.loads(path.read_bytes(), object_pairs_hook=unique_object)
    if not isinstance(value, dict):
        raise RuntimeError(f"{label} is not a JSON object.")
    return value


def safe_relative_path(value: str, label: str) -> pathlib.PurePosixPath:
    windows = pathlib.PureWindowsPath(value)
    path = pathlib.PurePosixPath(value)
    if "\0" in value or "\\" in value or ":" in value or windows.drive or windows.root or path.is_absolute():
        raise RuntimeError(f"{label} has an unsafe path: {value!r}")
    if not path.parts or any(part in {"", ".", ".."} for part in path.parts):
        raise RuntimeError(f"{label} has an unsafe path: {value!r}")
    return path


def expected_inventory(path: pathlib.Path) -> tuple[str, list[str], int, str]:
    value = load_object(path, 64 * 1024, "Expected publish inventory")
    libraries = value.get("libraries")
    assets = value.get("assets")
    if value.get("schemaVersion") != 1 or not isinstance(value.get("target"), str):
        raise RuntimeError("Expected publish inventory has an unsupported schema.")
    if not isinstance(libraries, list) or not all(isinstance(library, str) for library in libraries):
        raise RuntimeError("Expected publish libraries must be strings.")
    if libraries != sorted(set(libraries)):
        raise RuntimeError("Expected publish libraries must be unique sorted strings.")
    if not isinstance(assets, dict):
        raise RuntimeError("Expected publish inventory is malformed.")
    count, digest = assets.get("count"), assets.get("sha256")
    if not isinstance(count, int) or isinstance(count, bool) or count < 0:
        raise RuntimeError("Expected publish asset count is invalid.")
    if (
        not isinstance(digest, str)
        or len(digest) != 64
        or any(character not in "0123456789abcdef" for character in digest)
    ):
        raise RuntimeError("Expected publish asset digest is invalid.")
    return value["target"], libraries, count, digest


def asset_records(target: JsonObject) -> list[AssetRecord]:
    records: list[AssetRecord] = []
    for library, body in target.items():
        if not isinstance(library, str) or not isinstance(body, dict):
            raise RuntimeError("Worker dependency target contains an invalid library.")
        for kind in ASSET_KINDS:
            assets = body.get(kind, {})
            if not isinstance(assets, dict):
                raise RuntimeError(f"Worker dependency assets are invalid: {library}/{kind}")
            for path, metadata in assets.items():
                if not isinstance(path, str) or not isinstance(metadata, dict):
                    raise RuntimeError(f"Worker dependency asset is invalid: {library}/{kind}")
                safe_relative_path(path, "Worker dependency asset")
                records.append((library, kind, path, metadata))
    return sorted(records, key=lambda record: record[:3])


def asset_digest(records: list[AssetRecord]) -> str:
    canonical = "\n".join(
        json.dumps(record, separators=(",", ":"), sort_keys=True) for record in records
    )
    return hashlib.sha256(canonical.encode("utf-8")).hexdigest()


def published_asset(root: pathlib.Path, record: AssetRecord) -> pathlib.Path:
    _, kind, source, metadata = record
    path = safe_relative_path(source, "Worker dependency asset")
    if kind == "resources":
        locale = metadata.get("locale")
        if not isinstance(locale, str) or len(safe_relative_path(locale, "Resource locale").parts) != 1:
            raise RuntimeError(f"Worker resource locale is invalid: {locale!r}")
        path = pathlib.PurePosixPath(locale, path.name)
    elif kind != "runtimeTargets":
        path = pathlib.PurePosixPath(path.name)
    destination = root.joinpath(*path.parts)
    if not destination.resolve(strict=False).is_relative_to(root.resolve()):
        raise RuntimeError(f"Worker dependency asset escapes the publish root: {source!r}")
    return destination


def verify_asset_files(worker: pathlib.Path, records: list[AssetRecord]) -> None:
    missing = []
    for record in records:
        destination = published_asset(worker.parent, record)
        if destination.is_symlink() or not destination.is_file():
            missing.append(str(destination.relative_to(worker.parent)))
    if missing:
        raise RuntimeError(f"Worker publish omitted declared assets: {sorted(set(missing))!r}")


def verify_dependencies(
    worker: pathlib.Path,
    inventory_path: pathlib.Path = PUBLISH_INVENTORY,
) -> tuple[int, int]:
    dependencies = load_object(worker.with_suffix(".deps.json"), 4 * 1024 * 1024, "Worker dependency manifest")
    target_name, expected_libraries, expected_assets, expected_digest = expected_inventory(inventory_path)
    libraries, targets = dependencies.get("libraries"), dependencies.get("targets")
    if not isinstance(libraries, dict) or not isinstance(targets, dict):
        raise RuntimeError("Worker dependency manifest has no library or target inventory.")
    if sorted(libraries) != expected_libraries or set(targets) != {target_name}:
        raise RuntimeError("Worker dependency library or target inventory differs from the pinned baseline.")
    target = targets[target_name]
    if not isinstance(target, dict) or sorted(target) != expected_libraries:
        raise RuntimeError("Worker dependency target libraries differ from the pinned baseline.")
    records = asset_records(target)
    if len(records) != expected_assets or asset_digest(records) != expected_digest:
        raise RuntimeError("Worker dependency asset inventory differs from the pinned baseline.")
    verify_asset_files(worker, records)
    load_object(worker.with_suffix(".runtimeconfig.json"), 1024 * 1024, "Worker runtime configuration")
    return len(libraries), len(records)


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dotnet", required=True, type=pathlib.Path)
    parser.add_argument("--worker", required=True, type=pathlib.Path)
    return parser.parse_args()


def run_worker(dotnet: pathlib.Path, worker: pathlib.Path) -> subprocess.CompletedProcess[str]:
    request = json.dumps({"v": 1, "id": 1, "method": "hello", "params": {}}) + "\n"
    completed = subprocess.run(
        [str(dotnet.resolve()), str(worker)],
        input=request,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        timeout=30,
        check=False,
    )
    if completed.returncode != 0:
        raise RuntimeError(
            f"Worker exited with {completed.returncode}.\n"
            f"stdout:\n{completed.stdout}\nstderr:\n{completed.stderr}"
        )
    return completed


def hello_identity(response: Any) -> tuple[dict[str, Any], JsonObject]:
    if not isinstance(response, dict) or not isinstance(response.get("result"), dict):
        raise RuntimeError("Worker hello response is not a result object.")
    result = response["result"]
    actual = {
        "v": response.get("v"),
        "id": response.get("id"),
        "ok": response.get("ok"),
        "protocolVersion": result.get("protocolVersion"),
        "backendId": result.get("backendId"),
        "backendRevision": result.get("backendRevision"),
    }
    return actual, result


def verify_disposed_event(response: Any) -> None:
    expected = {"v": 1, "event": "state", "data": {"state": "disposed"}}
    if response != expected:
        raise RuntimeError(f"Unexpected worker teardown event: {response!r}")


def verify_hello(stdout: str) -> dict[str, Any]:
    lines = [line for line in stdout.splitlines() if line.strip()]
    if len(lines) != 2:
        raise RuntimeError(
            f"Worker returned {len(lines)} protocol messages; expected one hello response and one teardown event."
        )
    actual, result = hello_identity(json.loads(lines[0]))
    expected = {
        "v": 1,
        "id": 1,
        "ok": True,
        "protocolVersion": 1,
        "backendId": "nxi",
        "backendRevision": SOURCE_REVISION,
    }
    if actual != expected:
        raise RuntimeError(f"Unexpected worker hello response: {actual!r}")
    capabilities = result.get("capabilities")
    if (
        not isinstance(capabilities, list)
        or not all(isinstance(capability, str) for capability in capabilities)
        or len(capabilities) != len(set(capabilities))
    ):
        raise RuntimeError("Worker hello capabilities are not a unique string array.")
    if "nxi.probe" not in capabilities:
        raise RuntimeError("Worker hello response omitted the nxi.probe capability.")
    verify_disposed_event(json.loads(lines[1]))
    return actual


def main() -> int:
    arguments = parse_arguments()
    worker = arguments.worker.resolve()
    if worker.suffix.lower() != ".dll" or worker.is_symlink() or not worker.is_file():
        raise RuntimeError(f"Worker must be a published managed assembly: {worker}")
    dependency_count, asset_count = verify_dependencies(worker)
    completed = run_worker(arguments.dotnet, worker)
    actual = verify_hello(completed.stdout)
    report = {
        "workerHello": "passed",
        "backendId": actual["backendId"],
        "backendRevision": actual["backendRevision"],
        "dependencyLibraries": dependency_count,
        "dependencyAssets": asset_count,
        "exitCode": completed.returncode,
    }
    print(json.dumps(report, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
