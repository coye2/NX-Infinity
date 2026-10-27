#!/usr/bin/env python3
"""Run the native x64 launcher under Xvfb and validate its probe report."""

from __future__ import annotations

import argparse
import math
import os
import pathlib
import signal
import subprocess
import time
from typing import Any

import common


CORE_REVISION = "67591deee07ab2ce86fc9b72c42e8b64c95ba1b3"
REQUIRED_CLAIMS = {
    "commercialGameQualified",
    "officialUpgradeParity",
    "physicalAudioVerified",
    "physicalControllerVerified",
    "saveLoadVerified",
    "targetHardwareQualified",
    "visualVerified",
}
REPORT_KEYS = {
    "actualPlatform",
    "appAssemblyHashScope",
    "appAssemblySha256",
    "appAssemblySha256After",
    "application",
    "automaticGameplayPass",
    "claims",
    "completedUtc",
    "contentExplicitlySelected",
    "elapsedSeconds",
    "endReason",
    "errorCodes",
    "exitStatus",
    "externalCancellationRequested",
    "guiEventLoopUsed",
    "installationVerification",
    "kind",
    "lastFailure",
    "launchAcknowledged",
    "launchPhases",
    "limitations",
    "loadedIdentity",
    "manifest",
    "mode",
    "prerequisiteIssueCodes",
    "processExecutableSha256",
    "processExecutableSha256After",
    "registeredContentSha256",
    "requestedBackend",
    "requestedSeconds",
    "runId",
    "schemaVersion",
    "sdkAssemblyHashScope",
    "sdkAssemblySha256",
    "sdkAssemblySha256After",
    "sdkReportSha256",
    "sdkVersion",
    "settings",
    "shutdown",
    "startedUtc",
    "startupSeconds",
    "telemetrySamples",
    "telemetryScope",
    "validationPhase",
    "version",
    "windowCloseReason",
}
INSTALLATION_KEYS = {
    "adapterVersion",
    "method",
    "observedBackendId",
    "observedRevision",
    "qualification",
    "succeeded",
}
MANIFEST_KEYS = {
    "backendId",
    "qualification",
    "revision",
    "sha256",
    "sha256After",
    "unchanged",
}
SHUTDOWN_KEYS = {
    "cleanupAttempted",
    "cleanupCompleted",
    "failureCode",
    "forcedTermination",
    "graceful",
}
EXPECTED_LIMITATIONS = [
    "Only this process's actual GUI, SDK and core observations are recorded.",
    "A probe never initializes a game, renderer or audio output.",
    "No automated input was injected by validation mode.",
    "Visual, audible, physical-controller and save/load observations require separate user reporting.",
    "Single-file publishing may make individual app/SDK assembly hashes unavailable; the process executable hash identifies the actual bundle.",
    "No raw logs, selected file paths, library notes, game bytes, keys or firmware are included.",
]
EXPECTED_PREREQUISITE_ISSUES = {"firmware_missing", "header_key_missing", "keys_missing"}
TELEMETRY_SCOPE = (
    "Approximately one-second backend telemetry observations, not a per-present "
    "frame-time trace or upstream-overhead benchmark."
)


def validate_report(path: pathlib.Path) -> common.JsonObject:
    if path.is_symlink() or not path.is_file() or path.stat().st_size > 1024 * 1024:
        raise ValueError("Native Linux smoke report is missing or too large.")
    report = common.strict_json(path.read_bytes(), "Native Linux smoke report")
    common.require_schema(report, 1, "Native Linux smoke report")
    installation = require_object(report, "installationVerification")
    manifest = require_object(report, "manifest")
    shutdown = require_object(report, "shutdown")
    claims = require_object(report, "claims")
    if (
        set(report) != REPORT_KEYS
        or set(installation) != INSTALLATION_KEYS
        or set(manifest) != MANIFEST_KEYS
        or set(shutdown) != SHUTDOWN_KEYS
        or set(claims) != REQUIRED_CLAIMS
        or report.get("kind") != "nx-infinity-local-validation"
        or report.get("application") != "NX Infinity"
        or report.get("version") != "1.0.0"
        or report.get("sdkVersion") != "0.3.2-dev"
        or report.get("actualPlatform") != "linux"
        or report.get("guiEventLoopUsed") is not True
        or report.get("automaticGameplayPass") is not False
        or report.get("mode") != "probe-only"
        or report.get("requestedBackend") != "nxi"
        or report.get("requestedSeconds") != 1
        or report.get("contentExplicitlySelected") is not False
        or report.get("registeredContentSha256") is not None
        or report.get("loadedIdentity") is not None
        or report.get("startupSeconds") is not None
        or report.get("launchAcknowledged") is not False
        or report.get("endReason") != "probe_completed"
        or report.get("exitStatus") != 0
        or isinstance(report.get("exitStatus"), bool)
        or report.get("errorCodes") != []
        or report.get("launchPhases") != []
        or report.get("validationPhase") != "prerequisites"
        or report.get("windowCloseReason") is not None
        or report.get("externalCancellationRequested") is not False
        or report.get("lastFailure") is not None
        or report.get("telemetrySamples") != []
        or report.get("settings") != []
        or report.get("telemetryScope") != TELEMETRY_SCOPE
        or report.get("limitations") != EXPECTED_LIMITATIONS
        or not exact_string_set(
            report.get("prerequisiteIssueCodes"), EXPECTED_PREREQUISITE_ISSUES
        )
        or not valid_hex(report.get("runId"), 32)
        or not isinstance(report.get("startedUtc"), str)
        or not isinstance(report.get("completedUtc"), str)
        or not finite_nonnegative(report.get("elapsedSeconds"))
        or not valid_hex(report.get("sdkReportSha256"), 64)
        or report.get("appAssemblyHashScope") != "individual-assembly-file"
        or report.get("sdkAssemblyHashScope") != "individual-assembly-file"
        or not matching_sha256(report, "appAssemblySha256", "appAssemblySha256After")
        or not matching_sha256(report, "processExecutableSha256", "processExecutableSha256After")
        or not matching_sha256(report, "sdkAssemblySha256", "sdkAssemblySha256After")
        or installation.get("method") != "public-BackendInstallation-and-worker-handshake"
        or installation.get("succeeded") is not True
        or installation.get("observedBackendId") != "nxi"
        or str(installation.get("observedRevision", "")).casefold() != CORE_REVISION
        or installation.get("adapterVersion") != "1.0.0-nxi"
        or installation.get("qualification") != "unqualified"
        or manifest.get("backendId") != "nxi"
        or str(manifest.get("revision", "")).casefold() != CORE_REVISION
        or manifest.get("qualification") != "unqualified"
        or not matching_sha256(manifest, "sha256", "sha256After")
        or manifest.get("unchanged") is not True
        or shutdown.get("cleanupAttempted") is not True
        or shutdown.get("cleanupCompleted") is not True
        or shutdown.get("graceful") is not True
        or shutdown.get("forcedTermination") is not False
        or shutdown.get("failureCode") is not None
        or any(value is not False for value in claims.values())
    ):
        raise ValueError("Native Linux smoke report did not prove a clean probe.")
    return report


def require_object(value: common.JsonObject, key: str) -> common.JsonObject:
    child = value.get(key)
    if not isinstance(child, dict):
        raise ValueError(f"Native Linux smoke report is missing {key}.")
    return child


def matching_sha256(value: common.JsonObject, before: str, after: str) -> bool:
    digest = value.get(before)
    return valid_hex(digest, 64) and value.get(after) == digest


def valid_hex(value: Any, length: int) -> bool:
    return (
        isinstance(value, str)
        and len(value) == length
        and all(character in "0123456789abcdef" for character in value)
    )


def exact_string_set(value: Any, expected: set[str]) -> bool:
    return (
        isinstance(value, list)
        and len(value) == len(expected)
        and all(isinstance(item, str) for item in value)
        and set(value) == expected
    )


def finite_nonnegative(value: Any) -> bool:
    return (
        isinstance(value, (int, float))
        and not isinstance(value, bool)
        and math.isfinite(value)
        and value >= 0
    )


def run(args: argparse.Namespace) -> None:
    enforce_required_self_nice()
    command, report = prepare_command(args)
    process = subprocess.Popen(command, start_new_session=True)
    try:
        required = os.environ.get("NXI_REQUIRED_NICE")
        if required is not None:
            observe_required_payloads(process, int(required))
        wait_for_smoke(process, command)
    except BaseException:
        stop_process_group(process)
        raise
    validate_report(report)


def enforce_required_self_nice() -> None:
    required_value = os.environ.get("NXI_REQUIRED_NICE")
    if required_value is None:
        return
    required = int(required_value)
    os.setpriority(os.PRIO_PROCESS, 0, required)
    observed = os.getpriority(os.PRIO_PROCESS, 0)
    if observed != required:
        raise RuntimeError(f"Smoke validator must run at nice {required}; observed {observed}.")
    print(f"priority: smoke validator python pid={os.getpid()}: nice {observed}", flush=True)


def prepare_command(args: argparse.Namespace) -> tuple[list[str], pathlib.Path]:
    launcher = regular_input(args.launcher, "Native launcher")
    xvfb_run = regular_input(args.xvfb_run, "xvfb-run")
    data_root = pathlib.Path(args.data_root).absolute()
    report = pathlib.Path(args.report).absolute()
    if data_root.is_symlink():
        raise ValueError(f"Smoke data root must not be a symbolic link: {data_root}")
    if report.is_symlink() or report.exists():
        raise FileExistsError(f"Smoke report must not already exist: {report}")
    data_root.mkdir(parents=True, exist_ok=True)
    report.parent.mkdir(parents=True, exist_ok=True)
    return [
        str(xvfb_run),
        "-a",
        str(launcher),
        "--validate",
        "--backend",
        "nxi",
        "--probe",
        "--data-root",
        str(data_root),
        "--report",
        str(report),
        "--seconds",
        "1",
    ], report


def regular_input(value: str, label: str) -> pathlib.Path:
    path = pathlib.Path(value).absolute()
    if path.is_symlink() or not path.is_file():
        raise ValueError(f"{label} is unavailable: {path}")
    return path


def observe_required_payloads(process: subprocess.Popen[Any], required: int) -> None:
    deadline = time.monotonic() + 5
    observed: set[str] = set()
    announced: set[int] = set()
    while process.poll() is None and time.monotonic() < deadline:
        observed.update(verify_process_tree_nice(process.pid, required, announced))
        if has_required_payloads(observed):
            return
        time.sleep(0.01)
    if not any("NxInfinity " in value for value in observed):
        raise RuntimeError("Smoke priority check did not observe the native launcher payload.")
    raise RuntimeError("Smoke priority check did not observe the native core payload.")


def has_required_payloads(commands: set[str]) -> bool:
    return any("NxInfinity " in value for value in commands) and any(
        "NxInfinity.Core" in value for value in commands
    )


def wait_for_smoke(process: subprocess.Popen[Any], command: list[str]) -> None:
    try:
        return_code = process.wait(timeout=120)
    except subprocess.TimeoutExpired:
        stop_process_group(process)
        raise TimeoutError("Native Linux launcher smoke exceeded 120 seconds.")
    if return_code != 0:
        raise subprocess.CalledProcessError(return_code, command)


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


def verify_process_tree_nice(root: int, required: int, announced: set[int]) -> set[str]:
    snapshot = process_snapshot()
    processes = {root}
    while True:
        discovered = {pid for pid, (parent, _, _) in snapshot.items() if parent in processes}
        additions = discovered - processes
        if not additions:
            break
        processes.update(additions)
    for pid in processes:
        try:
            os.setpriority(os.PRIO_PROCESS, pid, required)
        except ProcessLookupError:
            pass
    verified = process_snapshot()
    observed_commands: set[str] = set()
    for pid in sorted(processes):
        if pid not in snapshot:
            continue
        _, prior, command = snapshot[pid]
        observed = verified.get(pid, (0, prior, command))[1]
        if observed != required:
            raise RuntimeError(f"Smoke payload {command} must run at nice {required}; observed {observed}.")
        observed_commands.add(command)
        if pid not in announced:
            print(f"priority: smoke payload {command}: pid {pid}: nice {observed}", flush=True)
            announced.add(pid)
    return observed_commands


def process_snapshot() -> dict[int, tuple[int, int, str]]:
    output = subprocess.check_output(["ps", "-eo", "pid=,ppid=,ni=,args="], text=True)
    result: dict[int, tuple[int, int, str]] = {}
    for line in output.splitlines():
        fields = line.strip().split(None, 3)
        try:
            if len(fields) == 4:
                result[int(fields[0])] = (int(fields[1]), int(fields[2]), fields[3])
        except ValueError:
            continue
    return result


def arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--launcher", required=True)
    parser.add_argument("--xvfb-run", required=True)
    parser.add_argument("--data-root", required=True)
    parser.add_argument("--report", required=True)
    return parser.parse_args()


if __name__ == "__main__":
    run(arguments())
