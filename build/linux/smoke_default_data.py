#!/usr/bin/env python3
"""Exercise normal launcher startup and prove default Linux data stays external."""

from __future__ import annotations

import argparse
import hashlib
import os
import pathlib
import signal
import subprocess
import time
from typing import Any

import common
import smoke_frontend


STARTUP_EVIDENCE = "nx-services/locks/profiles.lock"
REPORT_KEYS = {
    "evidenceMtimeNs",
    "evidencePath",
    "expectedDataRoot",
    "externalDataRoot",
    "freshEvidence",
    "kind",
    "launcherSha256",
    "markerSha256After",
    "markerSha256Before",
    "notBeforeNs",
    "phase",
    "schemaVersion",
}
PHASES = {"after-replacement", "before-replacement"}


def run(args: argparse.Namespace) -> None:
    smoke_frontend.enforce_required_self_nice()
    launcher = regular_file(args.launcher, "Native launcher")
    xvfb_run = regular_file(args.xvfb_run, "xvfb-run")
    expected_root = pathlib.Path(args.expected_data_root).resolve()
    marker = regular_file(args.marker, "Preservation marker")
    report = report_destination(args.report)
    if args.phase not in PHASES:
        raise ValueError(f"Unsupported default-data smoke phase: {args.phase}")
    expected_from_environment = pathlib.Path(require_environment("XDG_DATA_HOME")).resolve() / "NX Infinity"
    if expected_root != expected_from_environment:
        raise ValueError("Expected data root does not match XDG_DATA_HOME/NX Infinity.")
    install_root = launcher.parent.resolve()
    if expected_root.is_relative_to(install_root):
        raise ValueError("Default user data must remain outside the application directory.")
    before = digest(marker)
    evidence = expected_root / STARTUP_EVIDENCE
    remove_stale_evidence(evidence)
    not_before_ns = time.time_ns()
    command = [str(xvfb_run), "-a", str(launcher)]
    process = subprocess.Popen(command, start_new_session=True)
    try:
        evidence_mtime_ns = observe_startup(process, evidence, not_before_ns)
    finally:
        stop_process_group(process)
    after = digest(marker)
    if after != before:
        raise ValueError("Normal launcher startup changed the preservation marker.")
    if not fresh_evidence(evidence, not_before_ns):
        raise ValueError("Normal launcher startup did not use the expected data root.")
    value: common.JsonObject = {
        "evidenceMtimeNs": evidence_mtime_ns,
        "evidencePath": STARTUP_EVIDENCE,
        "expectedDataRoot": "XDG_DATA_HOME/NX Infinity",
        "externalDataRoot": True,
        "freshEvidence": True,
        "kind": "nx-infinity-default-data-start",
        "launcherSha256": digest(launcher),
        "markerSha256After": after,
        "markerSha256Before": before,
        "notBeforeNs": not_before_ns,
        "phase": args.phase,
        "schemaVersion": 1,
    }
    validate_report(value, args.phase)
    with report.open("xb") as stream:
        stream.write(common.canonical_json(value))
        stream.flush()
        os.fsync(stream.fileno())


def remove_stale_evidence(path: pathlib.Path) -> None:
    if path.is_symlink() or (path.exists() and not path.is_file()):
        raise ValueError(f"Startup evidence must be an ordinary file: {path}")
    if path.exists():
        path.unlink()


def fresh_evidence(path: pathlib.Path, not_before_ns: int) -> bool:
    return (
        not path.is_symlink()
        and path.is_file()
        and path.stat().st_mtime_ns >= not_before_ns
    )


def observe_startup(
    process: subprocess.Popen[Any], evidence: pathlib.Path, not_before_ns: int
) -> int:
    deadline = time.monotonic() + 20
    announced: set[int] = set()
    required_value = os.environ.get("NXI_REQUIRED_NICE")
    observed_launcher = required_value is None
    while process.poll() is None and time.monotonic() < deadline:
        if required_value is not None:
            commands = smoke_frontend.verify_process_tree_nice(
                process.pid, int(required_value), announced
            )
            observed_launcher |= any("NxInfinity " in command for command in commands)
        if observed_launcher and fresh_evidence(evidence, not_before_ns):
            return evidence.stat().st_mtime_ns
        time.sleep(0.05)
    if process.poll() is not None:
        raise RuntimeError(f"Normal launcher exited before startup completed: {process.returncode}")
    raise TimeoutError("Normal launcher did not initialize its default data root within 20 seconds.")


def validate_report(value: common.JsonObject, expected_phase: str) -> None:
    common.require_schema(value, 1, "Default-data smoke report")
    if (
        set(value) != REPORT_KEYS
        or value.get("kind") != "nx-infinity-default-data-start"
        or value.get("phase") != expected_phase
        or value.get("expectedDataRoot") != "XDG_DATA_HOME/NX Infinity"
        or value.get("evidencePath") != STARTUP_EVIDENCE
        or value.get("externalDataRoot") is not True
        or value.get("freshEvidence") is not True
        or not smoke_frontend.valid_hex(value.get("launcherSha256"), 64)
        or not smoke_frontend.valid_hex(value.get("markerSha256Before"), 64)
        or value.get("markerSha256After") != value.get("markerSha256Before")
        or not positive_integer(value.get("notBeforeNs"))
        or not positive_integer(value.get("evidenceMtimeNs"))
        or value["evidenceMtimeNs"] < value["notBeforeNs"]
    ):
        raise ValueError("Default-data smoke report did not prove a fresh external start.")


def positive_integer(value: Any) -> bool:
    return isinstance(value, int) and not isinstance(value, bool) and value > 0


def stop_process_group(process: subprocess.Popen[Any]) -> None:
    if process.poll() is not None:
        return
    try:
        os.killpg(process.pid, signal.SIGTERM)
    except ProcessLookupError:
        return
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
        process.wait()


def regular_file(value: str, label: str) -> pathlib.Path:
    path = pathlib.Path(value).absolute()
    if path.is_symlink() or not path.is_file():
        raise ValueError(f"{label} is unavailable: {path}")
    return path


def report_destination(value: str) -> pathlib.Path:
    path = pathlib.Path(value).absolute()
    if path.exists() or path.is_symlink():
        raise FileExistsError(f"Default-data smoke report must not already exist: {path}")
    path.parent.mkdir(parents=True, exist_ok=True)
    return path


def require_environment(name: str) -> str:
    value = os.environ.get(name)
    if not value:
        raise ValueError(f"{name} must identify the isolated test data directory.")
    return value


def digest(path: pathlib.Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--launcher", required=True)
    parser.add_argument("--xvfb-run", required=True)
    parser.add_argument("--expected-data-root", required=True)
    parser.add_argument("--marker", required=True)
    parser.add_argument("--phase", required=True, choices=sorted(PHASES))
    parser.add_argument("--report", required=True)
    return parser.parse_args()


if __name__ == "__main__":
    run(arguments())
