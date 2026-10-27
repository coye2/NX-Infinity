#!/usr/bin/env python3
"""Focused tests for source restoration and worker smoke validation."""

from __future__ import annotations

import importlib.util
import json
import os
import pathlib
import subprocess
import sys
import tempfile
import unittest
from types import ModuleType


HERE = pathlib.Path(__file__).resolve().parent
sys.dont_write_bytecode = True


def load_module(name: str, path: pathlib.Path) -> ModuleType:
    specification = importlib.util.spec_from_file_location(name, path)
    if specification is None or specification.loader is None:
        raise RuntimeError(f"Unable to load test target: {path}")
    module = importlib.util.module_from_spec(specification)
    sys.modules[name] = module
    specification.loader.exec_module(module)
    return module


restore = load_module("nx_source_restore", HERE / "restore.py")
smoke = load_module("nx_worker_smoke", HERE / "smoke_worker.py")


class ScratchTestCase(unittest.TestCase):
    def setUp(self) -> None:
        scratch_value = os.environ.get("NXI_TEST_SCRATCH")
        if not scratch_value:
            self.fail("NXI_TEST_SCRATCH must name an existing scratch directory.")
        self.scratch = pathlib.Path(scratch_value).resolve()
        if not self.scratch.is_dir():
            self.fail(f"NXI_TEST_SCRATCH is not a directory: {self.scratch}")

    def temporary_directory(self) -> tempfile.TemporaryDirectory[str]:
        return tempfile.TemporaryDirectory(prefix="nx-source-test-", dir=self.scratch)


class RestoreTests(ScratchTestCase):
    def test_release_plan_is_complete(self) -> None:
        plan = restore.load_plan()
        self.assertEqual(4081, len(plan.core_entries))
        self.assertEqual(62, len(plan.reconstructed))
        self.assertEqual(1, len(plan.unavailable))
        self.assertEqual(11, len({entry["gitCommit"] for entry in plan.history.values()}))
        self.assertEqual(12, len(restore.kenji_revisions(plan)))

    def test_relative_paths_reject_escape(self) -> None:
        unsafe = (
            "",
            ".",
            "../escape",
            "/absolute",
            "core/../escape",
            "\\absolute",
            "..\\escape",
            "core\\..\\escape",
            "C:\\outside",
            "C:/outside",
            "file:stream",
        )
        for value in unsafe:
            with self.subTest(value=value), self.assertRaises(ValueError):
                restore.relative_path(value)

    def test_local_path_rejects_symlinked_parent_escape(self) -> None:
        with self.temporary_directory() as directory:
            scratch = pathlib.Path(directory)
            root, outside = scratch / "root", scratch / "outside"
            root.mkdir()
            outside.mkdir()
            try:
                (root / "escape").symlink_to(outside, target_is_directory=True)
            except OSError as error:
                self.skipTest(f"Directory symlinks are unavailable: {error}")
            with self.assertRaisesRegex(ValueError, "escapes its root"):
                restore.local_path(root, "escape/file.cs")

    def test_revision_reads_ignore_crlf_worktree_conversion(self) -> None:
        with self.temporary_directory() as directory:
            repository = pathlib.Path(directory) / "repository"
            repository.mkdir()
            self.run_git(repository, "init", "--quiet")
            self.run_git(repository, "config", "user.name", "NX Source Test")
            self.run_git(repository, "config", "user.email", "source-test@example.invalid")
            source = repository / "source.cs"
            source.write_bytes(b"first\nsecond\n")
            self.run_git(repository, "add", "source.cs")
            self.run_git(repository, "commit", "--quiet", "-m", "fixture")
            revision = self.run_git(repository, "rev-parse", "HEAD").strip()
            self.run_git(repository, "config", "core.autocrlf", "true")
            source.unlink()
            self.run_git(repository, "checkout", "--", "source.cs")
            self.assertEqual(b"first\r\nsecond\r\n", source.read_bytes())
            self.assertEqual(
                b"first\nsecond\n",
                restore.read_revision_file(repository, revision, "source.cs"),
            )

    def test_history_source_is_bound_to_commit_path_and_blob(self) -> None:
        with self.temporary_directory() as directory:
            repository = pathlib.Path(directory) / "repository"
            repository.mkdir()
            self.run_git(repository, "init", "--quiet")
            self.run_git(repository, "config", "user.name", "NX Source Test")
            self.run_git(repository, "config", "user.email", "source-test@example.invalid")
            source = repository / "source.cs"
            source.write_bytes(b"pinned\n")
            self.run_git(repository, "add", "source.cs")
            self.run_git(repository, "commit", "--quiet", "-m", "fixture")
            commit = self.run_git(repository, "rev-parse", "HEAD").strip()
            blob = self.run_git(repository, "rev-parse", f"{commit}:source.cs").strip()
            entry = {"gitCommit": commit, "gitBlob": blob}

            self.assertEqual(b"pinned\n", restore.read_history_file(repository, "source.cs", entry))
            entry["gitBlob"] = "0" * 40
            with self.assertRaisesRegex(ValueError, "is not blob"):
                restore.read_history_file(repository, "source.cs", entry)

    def test_hash_sensitive_repository_sources_force_lf(self) -> None:
        paths = (
            "src/core/overlays/src/Ryujinx.HLE/Switch.cs",
            "src/runtime/NxInfinity.Runtime/NetworkPolicy.cs",
        )
        for path in paths:
            output = restore.git(restore.REPOSITORY, "check-attr", "eol", "--", path)
            with self.subTest(path=path):
                self.assertEqual(f"{path}: eol: lf\n", output)

    def test_byte_verification_rejects_wrong_identity(self) -> None:
        data = b"verified"
        restore.verify_bytes(data, restore.sha256(data), len(data), "fixture")
        with self.assertRaisesRegex(ValueError, "failed verification"):
            restore.verify_bytes(data, "0" * 64, len(data), "fixture")

    def test_json_loader_rejects_duplicate_fields(self) -> None:
        with self.temporary_directory() as directory:
            path = pathlib.Path(directory) / "duplicate.json"
            path.write_text('{"value": 1, "value": 2}', encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "Duplicate JSON field"):
                restore.load_json(path)

    @staticmethod
    def run_git(repository: pathlib.Path, *arguments: str) -> str:
        completed = subprocess.run(
            ["git", "-C", str(repository), *arguments],
            check=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
        )
        return completed.stdout


class SmokeTests(ScratchTestCase):
    DISPOSED_EVENT = {"v": 1, "event": "state", "data": {"state": "disposed"}}

    def create_worker_fixture(self) -> pathlib.Path:
        directory = pathlib.Path(self.temporary.name)
        worker = directory / "NxInfinity.Core.dll"
        worker.write_bytes(b"fixture")
        libraries = {
            "NxInfinity.Core/1.0.0": {"type": "project"},
            "Package/1.2.3": {"type": "package"},
        }
        target = {
            "NxInfinity.Core/1.0.0": {
                "runtime": {"NxInfinity.Core.dll": {}},
            },
            "Package/1.2.3": {
                "runtime": {
                    "lib/net8.0/Package.dll": {"assemblyVersion": "1.0.0.0"},
                },
                "resources": {
                    "lib/net8.0/fr/Package.resources.dll": {"locale": "fr"},
                },
                "runtimeTargets": {
                    "runtimes/linux-x64/native/libPackage.so": {
                        "assetType": "native",
                        "rid": "linux-x64",
                    },
                },
            },
        }
        worker.with_suffix(".deps.json").write_text(
            json.dumps(
                {
                    "libraries": libraries,
                    "targets": {".NETCoreApp,Version=v10.0": target},
                }
            ),
            encoding="utf-8",
        )
        worker.with_suffix(".runtimeconfig.json").write_text("{}", encoding="utf-8")
        self.inventory = directory / "inventory.json"
        self.inventory.write_text(
            json.dumps(
                {
                    "schemaVersion": 1,
                    "target": ".NETCoreApp,Version=v10.0",
                    "libraries": sorted(libraries),
                    "assets": {
                        "count": 4,
                        "sha256": "cac2ba22fc9eba01bc722cb67d4df684b19a3fa866be6bb0844272452f1f83ee",
                    },
                }
            ),
            encoding="utf-8",
        )
        (directory / "Package.dll").write_bytes(b"fixture")
        (directory / "fr").mkdir()
        (directory / "fr/Package.resources.dll").write_bytes(b"fixture")
        native = directory / "runtimes/linux-x64/native"
        native.mkdir(parents=True)
        (native / "libPackage.so").write_bytes(b"fixture")
        return worker

    def setUp(self) -> None:
        super().setUp()
        self.temporary = self.temporary_directory()
        self.addCleanup(self.temporary.cleanup)

    def test_dependency_inventory_accepts_complete_publish(self) -> None:
        worker = self.create_worker_fixture()
        self.assertEqual((2, 4), smoke.verify_dependencies(worker, self.inventory))

    def test_dependency_inventory_rejects_missing_declared_asset(self) -> None:
        worker = self.create_worker_fixture()
        (worker.parent / "Package.dll").unlink()
        with self.assertRaisesRegex(RuntimeError, "omitted declared assets"):
            smoke.verify_dependencies(worker, self.inventory)

    def test_dependency_inventory_rejects_omitted_asset_record(self) -> None:
        worker = self.create_worker_fixture()
        dependencies = json.loads(worker.with_suffix(".deps.json").read_text(encoding="utf-8"))
        package = dependencies["targets"][".NETCoreApp,Version=v10.0"]["Package/1.2.3"]
        del package["runtimeTargets"]
        worker.with_suffix(".deps.json").write_text(json.dumps(dependencies), encoding="utf-8")
        with self.assertRaisesRegex(RuntimeError, "asset inventory differs"):
            smoke.verify_dependencies(worker, self.inventory)

    def test_dependency_inventory_rejects_omitted_package(self) -> None:
        worker = self.create_worker_fixture()
        dependencies = json.loads(worker.with_suffix(".deps.json").read_text(encoding="utf-8"))
        del dependencies["libraries"]["Package/1.2.3"]
        del dependencies["targets"][".NETCoreApp,Version=v10.0"]["Package/1.2.3"]
        worker.with_suffix(".deps.json").write_text(json.dumps(dependencies), encoding="utf-8")
        with self.assertRaisesRegex(RuntimeError, "inventory differs"):
            smoke.verify_dependencies(worker, self.inventory)

    def test_dependency_loader_rejects_duplicate_fields(self) -> None:
        path = pathlib.Path(self.temporary.name) / "duplicate.json"
        path.write_text('{"libraries": {}, "libraries": {}}', encoding="utf-8")
        with self.assertRaisesRegex(RuntimeError, "duplicate JSON field"):
            smoke.load_object(path, 1024, "Fixture")

    def test_hello_requires_identity_and_capability(self) -> None:
        response: dict[str, object] = {
            "v": 1,
            "id": 1,
            "ok": True,
            "result": {
                "protocolVersion": 1,
                "backendId": "nxi",
                "backendRevision": smoke.SOURCE_REVISION,
                "capabilities": ["nxi.probe"],
            },
        }
        output = json.dumps(response) + "\n" + json.dumps(self.DISPOSED_EVENT)
        self.assertEqual("nxi", smoke.verify_hello(output)["backendId"])
        response["result"]["capabilities"] = []
        with self.assertRaisesRegex(RuntimeError, "omitted the nxi.probe"):
            smoke.verify_hello(json.dumps(response) + "\n" + json.dumps(self.DISPOSED_EVENT))

    def test_hello_rejects_malformed_capabilities_and_extra_responses(self) -> None:
        response = {
            "v": 1,
            "id": 1,
            "ok": True,
            "result": {
                "protocolVersion": 1,
                "backendId": "nxi",
                "backendRevision": smoke.SOURCE_REVISION,
                "capabilities": "nxi.probe",
            },
        }
        disposed = json.dumps(self.DISPOSED_EVENT)
        with self.assertRaisesRegex(RuntimeError, "unique string array"):
            smoke.verify_hello(json.dumps(response) + "\n" + disposed)
        response["result"]["capabilities"] = ["nxi.probe"]
        encoded = json.dumps(response)
        with self.assertRaisesRegex(RuntimeError, "3 protocol messages"):
            smoke.verify_hello(encoded + "\n" + encoded + "\n" + disposed)
        with self.assertRaisesRegex(RuntimeError, "Unexpected worker teardown event"):
            smoke.verify_hello(encoded + "\n" + json.dumps({"v": 1, "event": "state"}))


if __name__ == "__main__":
    unittest.main()
