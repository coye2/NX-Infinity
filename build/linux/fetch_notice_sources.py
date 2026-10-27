#!/usr/bin/env python3
"""Fetch immutable third-party notice texts into external build scratch."""

from __future__ import annotations

import argparse
import hashlib
import pathlib
import urllib.request
from typing import Any

import common


HERE = pathlib.Path(__file__).resolve().parent
MAX_NOTICE_BYTES = 2 * 1024 * 1024


def fetch(output: pathlib.Path) -> None:
    output = output.absolute()
    if output.exists() or output.is_symlink():
        raise FileExistsError(f"Notice output must not already exist: {output}")
    policy = common.strict_json(
        (HERE / "NOTICE-SOURCES.json").read_bytes(), "NOTICE-SOURCES.json"
    )
    common.require_schema(policy, 1, "NOTICE-SOURCES.json")
    if set(policy) != {"notices", "schemaVersion"}:
        raise ValueError("Notice source policy has unexpected fields.")
    records = policy.get("notices")
    if not isinstance(records, list) or not records:
        raise ValueError("Notice source policy is empty.")
    output.mkdir(parents=True)
    seen: set[str] = set()
    for record in records:
        fetch_notice(output, record, seen)


def fetch_notice(output: pathlib.Path, value: Any, seen: set[str]) -> None:
    if not isinstance(value, dict) or set(value) != {"path", "sha256", "url"}:
        raise ValueError("Notice source record is invalid.")
    raw_path = value.get("path")
    expected = value.get("sha256")
    url = value.get("url")
    if (
        not isinstance(raw_path, str)
        or not isinstance(expected, str)
        or len(expected) != 64
        or not isinstance(url, str)
        or not url.startswith("https://raw.githubusercontent.com/")
    ):
        raise ValueError("Notice source record has invalid values.")
    relative = common.safe_relative(raw_path)
    if len(relative.parts) != 1 or raw_path in seen:
        raise ValueError(f"Notice source path must be a unique file name: {raw_path}")
    seen.add(raw_path)
    request = urllib.request.Request(url, headers={"User-Agent": "NX-Infinity-source-fetch/1"})
    with urllib.request.urlopen(request, timeout=30) as response:
        data = response.read(MAX_NOTICE_BYTES + 1)
    if len(data) > MAX_NOTICE_BYTES:
        raise ValueError(f"Notice source exceeds the size bound: {raw_path}")
    if hashlib.sha256(data).hexdigest() != expected:
        raise ValueError(f"Notice source identity mismatch: {raw_path}")
    destination = output / raw_path
    destination.write_bytes(data)
    destination.chmod(0o644)


def arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, type=pathlib.Path)
    return parser.parse_args()


if __name__ == "__main__":
    fetch(arguments().output)
