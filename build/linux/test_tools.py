#!/usr/bin/env python3
"""Focused tests for deterministic native Linux archives."""

from __future__ import annotations

import importlib.util
import base64
import dataclasses
import gzip
import hashlib
import io
import json
import os
import pathlib
import shlex
import shutil
import struct
import subprocess
import sys
import tarfile
import tempfile
import time
import unittest
import unittest.mock
import zipfile
from types import ModuleType, SimpleNamespace


HERE = pathlib.Path(__file__).resolve().parent
sys.dont_write_bytecode = True
sys.path.insert(0, str(HERE))


def load_module(name: str, path: pathlib.Path) -> ModuleType:
    specification = importlib.util.spec_from_file_location(name, path)
    if specification is None or specification.loader is None:
        raise RuntimeError(f"Unable to load test target: {path}")
    module = importlib.util.module_from_spec(specification)
    sys.modules[name] = module
    specification.loader.exec_module(module)
    return module


common = load_module("nx_linux_common", HERE / "common.py")
package = load_module("nx_linux_package", HERE / "package.py")
verify = load_module("nx_linux_verify", HERE / "verify.py")
smoke = load_module("nx_linux_smoke", HERE / "smoke_frontend.py")
default_smoke = load_module("nx_linux_default_smoke", HERE / "smoke_default_data.py")
distribution = sys.modules["distribution"]
package_notices = sys.modules["package_notices"]
source_bundle = sys.modules["source_bundle"]


class ScratchTestCase(unittest.TestCase):
    def setUp(self) -> None:
        scratch_value = os.environ.get("NXI_TEST_SCRATCH")
        if not scratch_value:
            self.fail("NXI_TEST_SCRATCH must name an existing scratch directory.")
        self.scratch = pathlib.Path(scratch_value).resolve()
        if not self.scratch.is_dir():
            self.fail(f"NXI_TEST_SCRATCH is not a directory: {self.scratch}")
        self.policy_sha256 = verify.verify_dependencies.NATIVE_DEPENDENCIES_SHA256

    def tearDown(self) -> None:
        verify.verify_dependencies.NATIVE_DEPENDENCIES_SHA256 = self.policy_sha256

    def temporary_directory(self) -> tempfile.TemporaryDirectory[str]:
        return tempfile.TemporaryDirectory(prefix="nx-linux-test-", dir=self.scratch)


class CommonTests(unittest.TestCase):
    def test_checked_in_dependency_policy_matches_verifier_pin(self) -> None:
        self.assertEqual(
            verify.verify_dependencies.NATIVE_DEPENDENCIES_SHA256,
            common.sha256_file(HERE / "NATIVE-DEPENDENCIES.json"),
        )
        policy = json.loads((HERE / "NATIVE-DEPENDENCIES.json").read_text())
        recorded = {
            dependency["packageSha256"]
            for dependency in policy["dependencies"]
            if dependency["packageSha256"] is not None
        }
        self.assertEqual(set(distribution.PACKAGE_HASHES.values()), recorded)

    def test_external_notice_and_source_policies_are_pinned(self) -> None:
        notices = json.loads((HERE / "NOTICE-SOURCES.json").read_text())
        self.assertEqual(1, notices["schemaVersion"])
        paths = [record["path"] for record in notices["notices"]]
        self.assertEqual(len(paths), len(set(paths)))
        for record in notices["notices"]:
            self.assertTrue(record["url"].startswith("https://raw.githubusercontent.com/"))
            self.assertEqual(64, len(record["sha256"]))
        native = json.loads((HERE / "NATIVE-DEPENDENCIES.json").read_text())
        ffmpeg = next(
            dependency
            for dependency in native["dependencies"]
            if dependency["id"] == "Ryujinx.Graphics.Nvdec.Dependencies.Linux"
        )
        for rid, source in source_bundle.FFMPEG_SOURCES.items():
            records = {
                record["path"]: record["sha256"]
                for record in native["redistributionSources"][rid]
            }
            self.assertEqual(
                source.archive_sha256,
                records[f"licenses/sources/{source.archive_name}"],
            )
            self.assertEqual(
                source.configuration_sha256,
                records[f"licenses/sources/{source_bundle.CONFIGURATION_NAME}"],
            )
            self.assertEqual(source.revision, ffmpeg["sourceRevisions"][rid])
            self.assertEqual(source.configuration, ffmpeg["buildConfigurations"][rid])
        sdl = next(
            dependency
            for dependency in native["dependencies"]
            if dependency["id"] == "Ryujinx.SDL3-CS"
        )
        self.assertIsNone(sdl["sourceRevisions"]["linux-arm64"])

    def test_safe_relative_rejects_cross_platform_escapes(self) -> None:
        unsafe = (
            "",
            ".",
            "../escape",
            "/absolute",
            "core/../escape",
            "\\absolute",
            "..\\escape",
            "C:\\outside",
            "file:stream",
            "CON/file",
            "src/Foo.cs. ",
            "src/right-to-left-\u202e.txt",
            "src/e\u0301.txt",
            "src/delete-\u007f.txt",
            "src/c1-\u0085.txt",
            "src/line-separator-\u2028.txt",
            "src/./alias.txt",
            "src//alias.txt",
            "src/alias.txt/",
            "./src/alias.txt",
        )
        for value in unsafe:
            with self.subTest(value=value), self.assertRaises(ValueError):
                common.safe_relative(value)

    def test_portable_paths_reject_case_collisions(self) -> None:
        with self.assertRaisesRegex(ValueError, "collision"):
            common.validate_portable_paths(("cores/nxi/Foo.dll", "cores/nxi/foo.dll"))

    def test_strict_json_rejects_duplicates_and_boolean_schema(self) -> None:
        with self.assertRaisesRegex(ValueError, "Duplicate JSON field"):
            common.strict_json(b'{"schemaVersion":1,"schemaVersion":2}', "fixture")
        with self.assertRaisesRegex(ValueError, "schemaVersion"):
            common.require_schema({"schemaVersion": True}, 1, "fixture")

    def test_elf_machine_reads_both_supported_architectures(self) -> None:
        self.assertEqual(62, common.elf_machine(fake_elf("linux-x64")))
        self.assertEqual(183, common.elf_machine(fake_elf("linux-arm64")))
        with self.assertRaisesRegex(ValueError, "ELF"):
            common.elf_machine(b"MZ")


class PackageTests(ScratchTestCase):
    def test_backend_manifest_covers_every_core_file(self) -> None:
        with self.temporary_directory() as directory:
            core = pathlib.Path(directory) / "core"
            write(core / "NxInfinity.Core", fake_elf("linux-x64"), executable=True)
            write(core / "managed.dll", b"managed")
            manifest = package.write_backend_manifest(core, "linux-x64")

            self.assertFalse(manifest["managedAssembly"])
            self.assertEqual("NxInfinity.Core", manifest["entryPoint"])
            self.assertEqual(
                {"NxInfinity.Core", "managed.dll"},
                set(manifest["files"]),
            )

    def test_generated_manifests_ignore_restrictive_umask(self) -> None:
        with self.temporary_directory() as directory:
            root = pathlib.Path(directory) / "package"
            core = root / "cores/nxi"
            write(core / "NxInfinity.Core", fake_elf("linux-x64"), executable=True)
            previous = os.umask(0o077)
            try:
                package.write_backend_manifest(core, "linux-x64")
                package.write_package_manifest(root, "linux-x64", 1_793_068_160)
            finally:
                os.umask(previous)
            self.assertEqual(
                0o644,
                (core / package.BACKEND_MANIFEST).stat().st_mode & 0o777,
            )
            self.assertEqual(
                0o644,
                (root / package.BUILD_MANIFEST).stat().st_mode & 0o777,
            )

    def test_backend_manifest_binds_name_and_source_url(self) -> None:
        with self.temporary_directory() as directory:
            root = fixture_tree(pathlib.Path(directory), "linux-x64")
            path = root / "cores/nxi/backend.json"
            manifest = package.write_backend_manifest(path.parent, "linux-x64")
            manifest["backendName"] = "Injected backend"
            manifest["sourceUrl"] = "https://example.invalid/phishing"
            write(path, common.canonical_json(manifest))
            with self.assertRaisesRegex(ValueError, "identity"):
                verify.verify_backend_manifest(root)

    def test_core_builds_disable_and_map_debug_paths(self) -> None:
        command = package.core_build_command(
            pathlib.Path("/sdk/dotnet"),
            pathlib.Path("/checkout/core"),
            "src/Ryujinx.HLE/Ryujinx.HLE.csproj",
            "linux-x64",
        )
        self.assertIn("-p:DebugSymbols=false", command)
        self.assertIn("-p:DebugType=None", command)
        self.assertIn("-p:PathMap=/checkout/core=/_/nx-core", command)
        self.assertIn("--no-incremental", command)

    def test_core_debug_artifacts_are_removed_before_publish(self) -> None:
        with self.temporary_directory() as directory:
            source = pathlib.Path(directory)
            for project in (
                "Ryujinx.HLE",
                "Ryujinx.Graphics.Vulkan",
                "Ryujinx.Audio.Backends.OpenAL",
            ):
                for suffix in (pathlib.Path(), pathlib.Path("linux-x64")):
                    write(source / "src" / project / "bin/Release/net9.0" / suffix / "test.pdb", b"path")
            package.remove_core_debug_artifacts(source, "linux-x64")
            self.assertEqual([], list(source.rglob("*.pdb")))

    def test_checksum_destination_rejects_a_preexisting_symlink(self) -> None:
        with self.temporary_directory() as directory:
            scratch = pathlib.Path(directory)
            archive = scratch / "package.tar.gz"
            outside = scratch / "outside"
            outside.write_bytes(b"must remain unchanged")
            checksum = archive.with_suffix(".gz.sha256")
            try:
                checksum.symlink_to(outside)
            except OSError as error:
                self.skipTest(f"File symlinks are unavailable: {error}")
            with self.assertRaisesRegex(FileExistsError, "Checksum destination"):
                package.checksum_destination(archive)
            self.assertEqual(b"must remain unchanged", outside.read_bytes())

    def test_publish_tree_rejects_symlinks_instead_of_dereferencing(self) -> None:
        with self.temporary_directory() as directory:
            scratch = pathlib.Path(directory)
            source, destination = scratch / "source", scratch / "destination"
            source.mkdir()
            outside = scratch / "outside"
            outside.write_bytes(b"host data")
            try:
                (source / "linked").symlink_to(outside)
            except OSError as error:
                self.skipTest(f"File symlinks are unavailable: {error}")
            with self.assertRaisesRegex(ValueError, "symbolic link"):
                package.copy_tree(source, destination)
            self.assertFalse(destination.exists())

    def test_native_notice_copy_rejects_wrong_package_hash(self) -> None:
        with self.temporary_directory() as directory:
            scratch = pathlib.Path(directory)
            package_path = scratch / "nuget/fixture/1/fixture.1.nupkg"
            write(package_path, b"wrong")
            previous_hashes = distribution.PACKAGE_HASHES
            previous_sources = distribution.NOTICE_SOURCES
            try:
                distribution.PACKAGE_HASHES = {("fixture", "1"): "0" * 64}
                distribution.NOTICE_SOURCES = ()
                with self.assertRaisesRegex(ValueError, "identity mismatch"):
                    distribution.copy_native_notices(scratch / "package", scratch / "nuget")
            finally:
                distribution.PACKAGE_HASHES = previous_hashes
                distribution.NOTICE_SOURCES = previous_sources

    def test_native_notice_copy_reads_the_verified_package_archive(self) -> None:
        with self.temporary_directory() as directory:
            scratch = pathlib.Path(directory)
            package_path = scratch / "nuget/fixture/1/fixture.1.nupkg"
            package_path.parent.mkdir(parents=True)
            with zipfile.ZipFile(package_path, "w") as archive:
                archive.writestr("NOTICE.txt", b"notice from package\n")
            write(package_path.parent / "NOTICE.txt", b"tampered extracted notice\n")
            previous_hashes = distribution.PACKAGE_HASHES
            previous_sources = distribution.NOTICE_SOURCES
            try:
                distribution.PACKAGE_HASHES = {
                    ("fixture", "1"): common.sha256_file(package_path)
                }
                distribution.NOTICE_SOURCES = (
                    ("fixture", "1", "NOTICE.txt", "Fixture-NOTICE.txt"),
                )
                root = scratch / "package"
                distribution.copy_native_notices(root, scratch / "nuget")
                self.assertEqual(
                    b"notice from package\n",
                    (root / "licenses/native/Fixture-NOTICE.txt").read_bytes(),
                )
            finally:
                distribution.PACKAGE_HASHES = previous_hashes
                distribution.NOTICE_SOURCES = previous_sources

    def test_runtime_notices_come_from_the_pinned_runtime_pack(self) -> None:
        with self.temporary_directory() as directory:
            scratch = pathlib.Path(directory)
            version = distribution.RUNTIME_PACK_VERSION
            packages = {}
            for rid in ("linux-x64", "linux-arm64"):
                package_id = f"microsoft.netcore.app.runtime.{rid}"
                package_path = scratch / f"nuget/{package_id}/{version}/{package_id}.{version}.nupkg"
                package_path.parent.mkdir(parents=True)
                with zipfile.ZipFile(package_path, "w") as archive:
                    archive.writestr("LICENSE.TXT", f"license {rid}\n")
                    archive.writestr("THIRD-PARTY-NOTICES.TXT", f"notices {rid}\n")
                write(package_path.parent / "THIRD-PARTY-NOTICES.TXT", b"tampered extracted notice\n")
                packages[rid] = common.sha256_file(package_path)
            with unittest.mock.patch.object(distribution, "RUNTIME_PACK_HASHES", packages):
                for rid in packages:
                    root = scratch / f"package-{rid}"
                    with self.subTest(rid=rid):
                        distribution.copy_runtime_notices(root, scratch / "nuget", rid)
                        native = root / "licenses/native"
                        self.assertEqual(f"license {rid}\n".encode(), (native / "dotnet-LICENSE.txt").read_bytes())
                        self.assertEqual(
                            f"notices {rid}\n".encode(), (native / "dotnet-THIRD-PARTY-NOTICES.txt").read_bytes()
                        )
                with self.assertRaisesRegex(ValueError, "Unsupported"):
                    distribution.copy_runtime_notices(scratch / "unsupported", scratch / "nuget", "win-x64")
            with (
                unittest.mock.patch.object(distribution, "RUNTIME_PACK_HASHES", {"linux-x64": "0" * 64}),
                self.assertRaisesRegex(ValueError, "identity mismatch"),
            ):
                distribution.copy_runtime_notices(scratch / "wrong", scratch / "nuget", "linux-x64")
            self.assertFalse((scratch / "wrong").exists())

    def test_managed_package_notice_uses_graph_and_archive_hashes(self) -> None:
        with self.temporary_directory() as directory:
            scratch = pathlib.Path(directory)
            package_root = scratch / "nuget/fixture.package/1.0.0"
            package_path = package_root / "fixture.package.1.0.0.nupkg"
            package_root.mkdir(parents=True)
            nuspec = (
                '<?xml version="1.0"?>'
                '<package><metadata><id>Fixture.Package</id><version>1.0.0</version>'
                '<authors>Fixture Authors</authors><copyright>Fixture Copyright</copyright>'
                '<license type="expression">MIT</license></metadata></package>'
            ).encode()
            with zipfile.ZipFile(package_path, "w") as archive:
                archive.writestr("Fixture.Package.nuspec", nuspec)
            package_bytes = package_path.read_bytes()
            archive_sha512 = base64.b64encode(
                hashlib.sha512(package_bytes).digest()
            ).decode("ascii")
            write(
                package_path.with_suffix(package_path.suffix + ".sha512"),
                (archive_sha512 + "\n").encode(),
            )
            content_hash = "fixture-content-hash"
            write(
                package_root / ".nupkg.metadata",
                common.canonical_json(
                    {
                        "contentHash": content_hash,
                        "source": "https://example.invalid/v3/index.json",
                        "version": 2,
                    }
                ),
            )
            output = scratch / "output"
            output.mkdir()
            record = package_notices.package_record(
                "Fixture.Package/1.0.0",
                {"sha512": f"sha512-{content_hash}", "type": "package"},
                output,
                scratch / "nuget",
            )
            self.assertEqual("expression:MIT", record["declaredLicense"])
            self.assertEqual(f"sha512-{content_hash}", record["graphContentHash"])
            self.assertEqual(1, len(record["notices"]))

    def test_archive_is_reproducible_and_verified(self) -> None:
        with self.temporary_directory() as directory:
            scratch = pathlib.Path(directory)
            root = fixture_tree(scratch, "linux-x64")
            package.write_backend_manifest(root / "cores/nxi", "linux-x64")
            package.write_package_manifest(root, "linux-x64", 1_793_068_160)
            first = scratch / "first.tar.gz"
            second = scratch / "second.tar.gz"

            package.create_archive(root, first, 1_793_068_160)
            package.create_archive(root, second, 1_793_068_160)

            self.assertEqual(common.sha256_file(first), common.sha256_file(second))
            summary = verify.verify_archive(first, "linux-x64")
            self.assertEqual("linux-x64", summary["runtimeIdentifier"])
            self.assertEqual(10, summary["elfFiles"])

    def test_verifier_rejects_wrong_architecture(self) -> None:
        with self.temporary_directory() as directory:
            root = fixture_tree(pathlib.Path(directory), "linux-x64")
            write(root / "libSkiaSharp.so", fake_elf("linux-arm64"))
            write_native_dependency_manifest(root, "linux-x64")
            package.write_backend_manifest(root / "cores/nxi", "linux-x64")
            package.write_package_manifest(root, "linux-x64", 1_793_068_160)
            with self.assertRaisesRegex(ValueError, "architecture"):
                verify.verify_tree(root, "linux-x64")

    def test_verifier_requires_elf64_little_endian(self) -> None:
        with self.temporary_directory() as directory:
            scratch = pathlib.Path(directory)
            for index, (offset, value) in enumerate(((4, 1), (5, 2))):
                root = fixture_tree(scratch / str(index), "linux-x64")
                path = root / "libSkiaSharp.so"
                data = bytearray(path.read_bytes())
                data[offset] = value
                write(path, bytes(data))
                with self.subTest(offset=offset), self.assertRaisesRegex(
                    ValueError, "64-bit little-endian"
                ):
                    verify.verify_elf_architecture(
                        root, common.regular_files(root), "linux-x64"
                    )

    def test_package_inventory_excludes_user_content_classes(self) -> None:
        cases = (
            "data/library.json",
            "games/title.xci",
            "games/title.nsz",
            "games/title.xcz",
            "keys/prod.keys",
            "firmware/0100000000000000.nca",
            "translation/python/python.exe",
            "misc/title.keys",
        )
        with self.temporary_directory() as directory:
            scratch = pathlib.Path(directory)
            for index, relative in enumerate(cases):
                root = fixture_tree(scratch / str(index), "linux-x64")
                write(root / relative, b"prohibited")
                with self.subTest(relative=relative), self.assertRaisesRegex(
                    ValueError, "user-data/content"
                ):
                    verify.verify_root(root, "linux-x64")

    def test_independent_inventory_rejects_new_paths_and_native_payloads(self) -> None:
        with self.temporary_directory() as directory:
            scratch = pathlib.Path(directory)
            added = fixture_tree(scratch / "added", "linux-x64")
            write(added / "unreviewed.bin", b"unreviewed package content")
            package.write_backend_manifest(added / "cores/nxi", "linux-x64")
            package.write_package_manifest(added, "linux-x64", 1_793_068_160)
            with self.assertRaisesRegex(ValueError, "Package paths"):
                verify.verify_tree(added, "linux-x64")

            changed = fixture_tree(scratch / "changed", "linux-x64")
            write(changed / "README-LINUX.md", fake_elf("linux-x64"))
            package.write_backend_manifest(changed / "cores/nxi", "linux-x64")
            package.write_package_manifest(changed, "linux-x64", 1_793_068_160)
            with self.assertRaisesRegex(ValueError, "Native payloads"):
                verify.verify_tree(changed, "linux-x64")

    def test_verifier_rejects_release_blocking_dependency(self) -> None:
        with self.temporary_directory() as directory:
            root = fixture_tree(pathlib.Path(directory), "linux-x64")
            path = root / "cores/nxi/NxInfinity.Core.deps.json"
            dependencies = json.loads(path.read_text())
            identity = "System.Text.Json/8.0.0"
            dependencies["libraries"][identity] = {}
            dependencies["targets"][".NETCoreApp,Version=v10.0/linux-x64"][identity] = {}
            write(path, common.canonical_json(dependencies))
            policy = {
                "runtimeTarget": ".NETCoreApp,Version=v10.0/linux-x64",
                "sha256": common.sha256_file(path),
            }
            with self.assertRaisesRegex(ValueError, "vulnerable package"):
                verify.verify_dependencies.verify_graph(path, "linux-x64", "core", policy)

    def test_verifier_binds_source_baseline_and_system_dependencies(self) -> None:
        with self.temporary_directory() as directory:
            root = fixture_tree(pathlib.Path(directory), "linux-x64")
            package.write_backend_manifest(root / "cores/nxi", "linux-x64")
            manifest = package.write_package_manifest(root, "linux-x64", 1_793_068_160)
            for field, replacement in (
                ("sourceBaseline", "0" * 40),
                ("systemDependencies", ["anything"]),
            ):
                changed = dict(manifest)
                changed[field] = replacement
                (root / package.BUILD_MANIFEST).write_bytes(common.canonical_json(changed))
                with self.subTest(field=field), self.assertRaisesRegex(ValueError, "identity"):
                    verify.verify_build_manifest(root, "linux-x64")

    def test_verifier_rejects_complete_dependency_graph_drift(self) -> None:
        mutations = ("extra-library", "target-only-vulnerability", "wrong-runtime-target")
        with self.temporary_directory() as directory:
            scratch = pathlib.Path(directory)
            for index, mutation in enumerate(mutations):
                root = fixture_tree(scratch / str(index), "linux-x64")
                path = root / "cores/nxi/NxInfinity.Core.deps.json"
                dependencies = json.loads(path.read_text())
                runtime_target = ".NETCoreApp,Version=v10.0/linux-x64"
                if mutation == "extra-library":
                    identity = "Injected.Windows.TranslationRuntime/99.0.0"
                    dependencies["libraries"][identity] = {}
                    dependencies["targets"][runtime_target][identity] = {}
                elif mutation == "target-only-vulnerability":
                    dependencies["targets"][runtime_target]["System.Text.Json/8.0.0"] = {}
                else:
                    dependencies["runtimeTarget"]["name"] = ".NETCoreApp,Version=v10.0/win-x64"
                write(path, common.canonical_json(dependencies))
                with self.subTest(mutation=mutation), self.assertRaisesRegex(
                    ValueError, "reviewed policy"
                ):
                    verify.verify_dependencies.verify(root, "linux-x64")

    def test_verifier_checks_launcher_dependencies_and_exact_native_paths(self) -> None:
        with self.temporary_directory() as directory:
            root = fixture_tree(pathlib.Path(directory), "linux-x64")
            graph_path = root / "NxInfinity.deps.json"
            original = graph_path.read_bytes()
            dependencies = json.loads(original)
            identity = "System.Private.Uri/4.3.0"
            dependencies["libraries"][identity] = {}
            dependencies["targets"][".NETCoreApp,Version=v10.0/linux-x64"][identity] = {}
            write(graph_path, common.canonical_json(dependencies))
            policy = {
                "runtimeTarget": ".NETCoreApp,Version=v10.0/linux-x64",
                "sha256": common.sha256_file(graph_path),
            }
            with self.assertRaisesRegex(ValueError, "vulnerable package"):
                verify.verify_dependencies.verify_graph(
                    graph_path, "linux-x64", "launcher", policy
                )

            write(graph_path, original)
            nested = root / "nested/libSkiaSharp.so"
            nested.parent.mkdir()
            (root / "libSkiaSharp.so").rename(nested)
            manifest = verify.verify_dependencies.load_manifest(root)
            assets = next(
                dependency["assets"]
                for dependency in manifest["dependencies"]
                if dependency["id"] == "SkiaSharp.NativeAssets.Linux"
            )
            with self.assertRaisesRegex(ValueError, "identity mismatch"):
                verify.verify_dependencies.validate_assets(root, "linux-x64", assets)

    def test_verifier_binds_notice_bytes_and_complete_gpl_source(self) -> None:
        with self.temporary_directory() as directory:
            root = fixture_tree(pathlib.Path(directory), "linux-x64")
            notice = root / "licenses/native/Avalonia-MIT.txt"
            original_notice = notice.read_bytes()
            notice.write_bytes(b"not a license\n")
            with self.assertRaisesRegex(ValueError, "notice identity mismatch"):
                verify.verify_dependencies.verify(root, "linux-x64")
            notice.write_bytes(original_notice)

            source = root / "licenses/totk-dfps-source"
            policy = verify.verify_dependencies.load_manifest(root)["correspondingSource"]
            write(source / "injected.cpp", b"unreviewed source\n")
            with self.assertRaisesRegex(ValueError, "complete inventory"):
                verify.verify_dependencies.verify_corresponding_source(root, policy)
            (source / "injected.cpp").unlink()
            (source / "NX-NOTICE.md").unlink()
            with self.assertRaisesRegex(ValueError, "complete inventory"):
                verify.verify_dependencies.verify_corresponding_source(root, policy)

    def test_verifier_rejects_unreviewed_producer_codeview_path(self) -> None:
        with self.temporary_directory() as directory:
            scratch = pathlib.Path(directory)
            root = fixture_tree(scratch / "drive", "linux-x64")
            write(
                root / "NxInfinity.dll",
                fake_pe(((2, codeview_record("C:\\unreviewed\\checkout\\NxInfinity.pdb")),)),
            )
            with self.assertRaisesRegex(ValueError, "CodeView"):
                verify.verify_dependencies.verify(root, "linux-x64")

            for index, producer_path in enumerate(
                (
                    "\\\\producer\\share\\Injected.pdb",
                    "C:\\producer\\caf\u00e9\\Injected.pdb",
                    "/home/k\u00e9kekes/MicroCom.Runtime.pdb",
                    "\\producer\\Injected.pdb",
                    "/_/../home/producer/Injected.pdb",
                    " /home/producer/Injected.pdb",
                    "file:///home/producer/Injected.pdb",
                    "producer/checkout/Injected.pdb",
                    "/_/src\\..\\..\\home\\Injected.pdb",
                    "Injected.pdb\u2028",
                    "/_//home/producer/Injected.pdb",
                    "/_/home/producer/Injected.pdb",
                    "/_/./home/producer/Injected.pdb",
                    "/_/C:/Users/producer/Injected.pdb",
                    "/_/src/./Injected.pdb",
                )
            ):
                case = scratch / f"case-{index}"
                write(case / "Injected.DLL", fake_pe(((2, codeview_record(producer_path)),)))
                with self.subTest(path=producer_path), self.assertRaisesRegex(ValueError, "CodeView"):
                    verify.verify_dependencies.verify_producer_codeview_paths(case, [])

    def test_codeview_policy_parses_debug_records_structurally(self) -> None:
        with self.temporary_directory() as directory:
            scratch = pathlib.Path(directory)
            producer_path = "C:\\producer\\caf\u00e9\\Reviewed.pdb"
            reviewed = scratch / "reviewed"
            write(reviewed / "Reviewed.dll", fake_pe(((2, codeview_record(producer_path)),)))
            record = {
                "assembly": "Reviewed.dll",
                "assemblySha256": common.sha256_file(reviewed / "Reviewed.dll"),
                "path": producer_path,
            }
            verify.verify_dependencies.verify_producer_codeview_paths(reviewed, [record])

            mapped = scratch / "mapped"
            write(
                mapped / "Mapped.dll",
                fake_pe(
                    (
                        (2, codeview_record("/_/src/Mapped/Mapped.pdb")),
                        (16, b""),
                        (21, codeview_record("Mapped.r2rmap", b"R2RM")),
                    )
                ),
            )
            verify.verify_dependencies.verify_producer_codeview_paths(mapped, [])

            failures = {
                "not UTF-8": fake_pe(((2, b"RSDS" + bytes(20) + b"C:\\\xff.pdb\0"),)),
                "unreviewed debug record type": fake_pe(((99, b"payload"),)),
                "malformed": b"MZ not a portable executable",
                "no debug directory": fake_pe(()),
                "hidden bytes": fake_pe(
                    ((2, codeview_record("/_/a.pdb") + b"/home/producer/secret.pdb\0"),)
                ),
                "hidden bytes in R2RM": fake_pe(
                    ((21, codeview_record("a.r2rmap", b"R2RM")[:-1] + b"x"),)
                ),
                "hidden bytes in short R2RM": fake_pe(
                    ((21, codeview_record("a.r2rmap", b"R2RM")[:40]),)
                ),
            }
            for message, data in failures.items():
                case = scratch / message.replace(" ", "-")
                write(case / "Injected.dll", data)
                with self.subTest(message=message), self.assertRaisesRegex(
                    ValueError, message.removesuffix(" in R2RM").removesuffix(" in short R2RM")
                ):
                    verify.verify_dependencies.verify_producer_codeview_paths(case, [])

    def test_embedded_portable_pdbs_require_reviewed_assembly_hashes(self) -> None:
        with self.temporary_directory() as directory:
            root = pathlib.Path(directory)
            write(root / "Embedded.dll", fake_pe(((17, b"MPDB" + bytes(8)),)))
            with self.assertRaisesRegex(ValueError, "embedded portable PDB"):
                verify.verify_dependencies.verify_embedded_portable_pdbs(root, [])
            record = {
                "assembly": "Embedded.dll",
                "assemblySha256": common.sha256_file(root / "Embedded.dll"),
            }
            verify.verify_dependencies.verify_embedded_portable_pdbs(root, [record])
            write(root / "Embedded.dll", fake_pe(((17, b"MPDB" + bytes(9)),)))
            with self.assertRaisesRegex(ValueError, "embedded portable PDB"):
                verify.verify_dependencies.verify_embedded_portable_pdbs(root, [record])

    def test_verifier_requires_declared_system_libraries(self) -> None:
        with self.temporary_directory() as directory:
            root = fixture_tree(pathlib.Path(directory), "linux-x64")
            library = root / "cores/nxi/libSDL3.so"
            cases = (
                (("libc.so.6", "libstdc++.so.6", "libavutil.so.58", "ld-linux-x86-64.so.2"), None),
                (("libunreviewed.so.1",), "undeclared system library"),
                (("liblttng-ust.so.0",), "undeclared system library"),
                (("ld-linux-aarch64.so.1",), "undeclared system library"),
            )
            for needed, failure in cases:
                write(library, fake_elf_needing("linux-x64", needed))
                files = common.regular_files(root)
                with self.subTest(needed=needed):
                    if failure is None:
                        verify.verify_system_libraries(root, files, "linux-x64")
                    else:
                        with self.assertRaisesRegex(ValueError, failure):
                            verify.verify_system_libraries(root, files, "linux-x64")
            stripped = bytearray(fake_elf_needing("linux-x64", ("libunreviewed.so.1",)))
            struct.pack_into("<H", stripped, 0x38, 0)
            write(library, bytes(stripped))
            with self.assertRaisesRegex(ValueError, "not a loadable"):
                verify.verify_system_libraries(root, common.regular_files(root), "linux-x64")
            write(library, fake_elf_needing("linux-x64", ("libc.so.6",)))
            write(root / "libcoreclrtraceptprovider.so", fake_elf_needing("linux-x64", ("liblttng-ust.so.0",)))
            verify.verify_system_libraries(root, common.regular_files(root), "linux-x64")
            self.assertIn("libstdc++.so.6", " ".join(verify.SYSTEM_DEPENDENCIES))
            self.assertEqual(package.SYSTEM_DEPENDENCIES, verify.SYSTEM_DEPENDENCIES)

    def test_raw_git_archive_reads_each_hostile_input(self) -> None:
        with self.temporary_directory() as directory:
            scratch = pathlib.Path(directory)
            repository, revision = git_fixture_repository(scratch / "ffmpeg")
            archive = fixture_archive_arguments(revision)
            stream = fixture_git(repository, *archive)
            hostile = hostile_git_environment(scratch)
            xdg = {**fixture_environment(), "XDG_CONFIG_HOME": hostile["XDG_CONFIG_HOME"]}
            self.assertNotEqual(stream, fixture_git(repository, *archive, environment=xdg))
            for name in ("GIT_DIR", "GIT_CONFIG_PARAMETERS", "GIT_CONFIG_COUNT"):
                isolated = {**fixture_environment(), name: hostile[name]}
                self.assertNotEqual(0, git_returncode(repository, archive, isolated), name)
            home = {**fixture_environment(), "HOME": hostile["HOME"]}
            home.pop("GIT_CONFIG_GLOBAL")
            self.assertNotEqual(0, git_returncode(repository, archive, home))
            for setting in REPOSITORY_ARCHIVE_SETTINGS:
                fixture_git(repository, "config", *setting)
                self.assertNotEqual(stream, fixture_git(repository, *archive), setting)
                fixture_git(repository, "config", "--unset", setting[0])
            self.assertEqual(stream, fixture_git(repository, *archive))
            replace_fixture_blob(repository, revision)
            self.assertNotEqual(stream, fixture_git(repository, *archive))

    def test_ffmpeg_source_bundle_ignores_hostile_inputs(self) -> None:
        with self.temporary_directory() as directory:
            scratch = pathlib.Path(directory)
            repository, revision = git_fixture_repository(scratch / "ffmpeg")
            stream = fixture_git(repository, *fixture_archive_arguments(revision))
            for setting in REPOSITORY_ARCHIVE_SETTINGS:
                fixture_git(repository, "config", *setting)
            replace_fixture_blob(repository, revision)
            root = scratch / "package"
            with (
                unittest.mock.patch.object(source_bundle, "FFMPEG_SOURCES", fixture_sources(revision, stream)),
                unittest.mock.patch.dict(os.environ, hostile_git_environment(scratch)),
            ):
                source_bundle.add_ffmpeg_source(root, repository, "linux-x64")
            self.assertEqual(stream, (root / "licenses/sources/ffmpeg-fixture-source.tar").read_bytes())
        for source in source_bundle.FFMPEG_SOURCES.values():
            self.assertTrue(source.archive_name.endswith("-source.tar"))

    def test_ffmpeg_source_bundle_refuses_local_attributes(self) -> None:
        with self.temporary_directory() as directory:
            scratch = pathlib.Path(directory)
            repository, revision = git_fixture_repository(scratch / "ffmpeg")
            stream = fixture_git(repository, *fixture_archive_arguments(revision))
            attributes = repository / ".git/info/attributes"
            empty = scratch / "empty-attributes"
            write(empty, b"")
            refused = {
                "contents": lambda: write(attributes, b"configure export-ignore\n"),
                "dangling": lambda: attributes.symlink_to(scratch / "missing-attributes"),
                "empty-symlink": lambda: attributes.symlink_to(empty),
                "directory": lambda: attributes.mkdir(),
            }
            with unittest.mock.patch.object(source_bundle, "FFMPEG_SOURCES", fixture_sources(revision, stream)):
                write(attributes, b"")
                source_bundle.add_ffmpeg_source(scratch / "empty", repository, "linux-x64")
                attributes.unlink()
                for name, create in refused.items():
                    create()
                    with self.subTest(attributes=name), self.assertRaisesRegex(ValueError, "local archive attributes"):
                        source_bundle.add_ffmpeg_source(scratch / name, repository, "linux-x64")
                    if attributes.is_dir() and not attributes.is_symlink():
                        attributes.rmdir()
                    else:
                        attributes.unlink()

    def test_ffmpeg_source_bundle_fails_closed(self) -> None:
        with self.temporary_directory() as directory:
            scratch = pathlib.Path(directory)
            repository, revision = git_fixture_repository(scratch / "ffmpeg")
            stream = fixture_git(repository, *fixture_archive_arguments(revision))
            source = fixture_sources(revision, stream)["linux-x64"]
            linked = scratch / "linked"
            linked.symlink_to(repository)
            not_repository = scratch / "not-a-repository"
            write(not_repository, b"")
            tar = "ffmpeg-fixture-source.tar"
            failures = (
                ("archive", dataclasses.replace(source, archive_sha256="0" * 64), repository,
                 "linux-x64", "archive identity mismatch", ()),
                ("configuration", dataclasses.replace(source, configuration_sha256="0" * 64), repository,
                 "linux-x64", "build-configuration identity mismatch", (tar,)),
                ("abbreviated", dataclasses.replace(source, revision=revision[:12]), repository,
                 "linux-x64", "pinned revision", ()),
                ("symlinked", source, linked, "linux-x64", "unavailable", ()),
                ("file", source, not_repository, "linux-x64", "unavailable", ()),
                ("rid", source, repository, "linux-arm64", "Unsupported", ()),
            )
            for name, candidate, location, rid, message, remaining in failures:
                root = scratch / name
                with (
                    self.subTest(failure=name),
                    unittest.mock.patch.object(source_bundle, "FFMPEG_SOURCES", {"linux-x64": candidate}),
                ):
                    with self.assertRaisesRegex(ValueError, message):
                        source_bundle.add_ffmpeg_source(root, location, rid)
                    left = sorted(path.name for path in root.rglob("*") if path.is_file()) if root.exists() else []
                    self.assertEqual(list(remaining), left)

    def test_ffmpeg_source_bundle_refuses_existing_destinations(self) -> None:
        with self.temporary_directory() as directory:
            scratch = pathlib.Path(directory)
            repository, revision = git_fixture_repository(scratch / "ffmpeg")
            stream = fixture_git(repository, *fixture_archive_arguments(revision))
            tar = "ffmpeg-fixture-source.tar"
            configuration = source_bundle.CONFIGURATION_NAME
            regular_files_left = {
                ("existing", tar): [tar],
                ("existing", configuration): sorted([tar, configuration]),
                ("dangling", tar): [],
                ("dangling", configuration): [tar],
            }
            with unittest.mock.patch.object(source_bundle, "FFMPEG_SOURCES", fixture_sources(revision, stream)):
                for (kind, name), expected in regular_files_left.items():
                    root = scratch / f"{kind}-{name}"
                    occupied = root / "licenses/sources" / name
                    target = scratch / f"missing-{name}"
                    if kind == "existing":
                        write(occupied, b"kept\n")
                    else:
                        occupied.parent.mkdir(parents=True)
                        occupied.symlink_to(target)
                    with self.subTest(kind=kind, name=name):
                        with self.assertRaises(FileExistsError):
                            source_bundle.add_ffmpeg_source(root, repository, "linux-x64")
                        left = sorted(
                            path.name for path in root.rglob("*") if path.is_file() and not path.is_symlink()
                        )
                        self.assertEqual(expected, left)
                        if kind == "existing":
                            self.assertEqual(b"kept\n", occupied.read_bytes())
                        else:
                            self.assertTrue(occupied.is_symlink())
                            self.assertFalse(target.exists())

    def test_ffmpeg_source_bundle_ignores_system_config(self) -> None:
        with self.temporary_directory() as directory:
            scratch = pathlib.Path(directory)
            repository, revision = git_fixture_repository(scratch / "ffmpeg")
            archive = fixture_archive_arguments(revision)
            stream = fixture_git(repository, *archive)
            broken = scratch / "system-gitconfig"
            write(broken, b"[core\nbroken\n")
            system = git_wrapper(scratch / "system", f"GIT_CONFIG_SYSTEM={shlex.quote(str(broken))} {real_git()}")
            reading = {**fixture_environment(), "PATH": system}
            reading.pop("GIT_CONFIG_NOSYSTEM")
            self.assertNotEqual(0, git_returncode(repository, archive, reading))
            with (
                unittest.mock.patch.object(source_bundle, "FFMPEG_SOURCES", fixture_sources(revision, stream)),
                unittest.mock.patch.dict(os.environ, {"PATH": system}),
            ):
                source_bundle.add_ffmpeg_source(scratch / "package", repository, "linux-x64")
            self.assertEqual(stream, (scratch / "package/licenses/sources/ffmpeg-fixture-source.tar").read_bytes())

    def test_ffmpeg_source_bundle_requires_git_2_45(self) -> None:
        with self.temporary_directory() as directory:
            scratch = pathlib.Path(directory)
            repository, revision = git_fixture_repository(scratch / "ffmpeg")
            stream = fixture_git(repository, *fixture_archive_arguments(revision))
            accepted_by_output = {
                "git version 2.44.9": False,
                "not git": False,
                "git version 2.45.0": True,
                "git version 3.0.0": True,
            }
            with unittest.mock.patch.object(source_bundle, "FFMPEG_SOURCES", fixture_sources(revision, stream)):
                for index, (output, accepted) in enumerate(accepted_by_output.items()):
                    fake = git_wrapper(
                        scratch / f"git-{index}",
                        f'if [ "$1" = --version ]; then echo "{output}"; exit 0; fi\n{real_git()}',
                    )
                    root = scratch / f"package-{index}"
                    with self.subTest(output=output), unittest.mock.patch.dict(os.environ, {"PATH": fake}):
                        if accepted:
                            source_bundle.add_ffmpeg_source(root, repository, "linux-x64")
                        else:
                            with self.assertRaisesRegex(ValueError, "git 2.45 or later"):
                                source_bundle.add_ffmpeg_source(root, repository, "linux-x64")
                        self.assertEqual(accepted, root.exists())

    def test_ffmpeg_source_bundle_never_fetches_missing_objects(self) -> None:
        with self.temporary_directory() as directory:
            scratch = pathlib.Path(directory)
            promisor, revision = git_fixture_repository(scratch / "promisor")
            archive = fixture_archive_arguments(revision)
            stream = fixture_git(promisor, *archive)
            fixture_git(promisor, "config", "uploadpack.allowFilter", "true")
            fixture_git(promisor, "config", "uploadpack.allowAnySHA1InWant", "true")
            partial = scratch / "partial"
            fixture_git(
                scratch, "clone", "-q", "--no-checkout", "--filter=blob:none", promisor.as_uri(), str(partial)
            )
            root = scratch / "package"
            with (
                unittest.mock.patch.object(source_bundle, "FFMPEG_SOURCES", fixture_sources(revision, stream)),
                self.assertRaises(subprocess.CalledProcessError) as raised,
            ):
                source_bundle.add_ffmpeg_source(root, partial, "linux-x64")
            self.assertIn("archive", raised.exception.cmd)
            self.assertFalse((root / "licenses/sources/ffmpeg-fixture-source.tar").exists())
            self.assertEqual(stream, fixture_git(partial, *archive))

    def test_packaged_documents_link_inside_the_archive(self) -> None:
        with self.temporary_directory() as directory:
            root = pathlib.Path(directory) / "package"
            package.add_distribution_files(root)
            readme = (root / "README-LINUX.md").read_text(encoding="utf-8")
            self.assertNotIn("](../", readme)
            self.assertIn("`build/linux/README.md` in the NX Infinity source repository", readme)
            licensing = (root / "licenses/LICENSING.md").read_text(encoding="utf-8")
            self.assertIn("](repository/Ryujinx-Team-MIT.txt)", licensing)
            self.assertIn("](totk-dfps-source/)", licensing)
            verify.verify_document_links(root, common.regular_files(root))
            changed = root / "README-LINUX.md"
            for allowed in (
                "<https://example.invalid/page>",
                "[upper](HTTPS://example.invalid/page)",
                "[directory](licenses/)",
                "[directory-dot](licenses/.)",
            ):
                changed.write_text(readme + "\n" + allowed + "\n", encoding="utf-8")
                with self.subTest(allowed=allowed):
                    verify.verify_document_links(root, common.regular_files(root))
            for injected in (
                "[broken](missing.md)",
                "[escape](../../outside.md)",
                "[ref]: missing.txt",
                "[drive](C:/outside.md)",
                "[quoted](missing.md 'title')",
                "[paren](missing.md (title))",
                "[angle](<missing file.md>)",
                "[angle-ref]: <missing file.md>",
                '<a href="missing.md">html</a>',
                "> [quoted-ref]: missing.md",
                "- [list-ref]: missing.md",
                "1. [ordered-ref]: missing.md",
                "    [indented-ref]: missing.md",
                "[esc\\]aped]: missing.md\n\n[link][esc\\]aped]",
                "[\\]]: missing.md",
                '<img srcset="missing.png">',
                '<object data="missing.bin"></object>',
                '<video poster="missing.png"></video>',
                '<div>\n<img/src="missing.png">\n</div>',
                '<details>\n<a/href="missing.md">x</a>\n</details>',
                "[scheme](missing.md:fragment)",
                "[ftp](ftp://example.invalid/missing)",
                "[backslash](licenses/\\.\\./../README-LINUX.md)",
                "[backslash-ref]: licenses/\\.\\./../README-LINUX.md",
                "[entity](licenses/&period;&period;/../README-LINUX.md)",
                "[numeric](licenses/&#46;&#46;/../README-LINUX.md)",
                "[fragment-escape](README-LINUX.md#\\.)",
                "[fragment-entity](README-LINUX.md#&period;)",
                "<missing.md:x>",
                "<file:///etc/passwd>",
                "<ftp://example.invalid/missing>",
                "[no-authority](https:missing.md)",
                "[upper-no-authority](HTTP:../../outside)",
                "[http-prefix](http-missing.md)",
                "[file-slash](licenses/LICENSE/)",
                "[file-dot](licenses/LICENSE/.)",
                "[file-parent](licenses/LICENSE/x/..)",
                "[up](../)",
                "<FILE:///etc/passwd>",
            ):
                changed.write_text(readme + "\n" + injected + "\n", encoding="utf-8")
                with self.subTest(link=injected), self.assertRaisesRegex(ValueError, "link is broken"):
                    verify.verify_document_links(root, common.regular_files(root))

    def test_launcher_must_not_offer_unsupported_online_actions(self) -> None:
        self.assertIn(b"Hello Kitty & Friends", verify.FORBIDDEN_UNSUPPORTED_UI)
        with self.temporary_directory() as directory:
            root = fixture_tree(pathlib.Path(directory), "linux-x64")
            launcher = root / "NxInfinity.dll"
            for marker in verify.FORBIDDEN_UNSUPPORTED_UI:
                write(launcher, fake_pe() + marker.decode("ascii").encode("utf-16-le"))
                with self.subTest(marker=marker), self.assertRaisesRegex(ValueError, "unsupported online"):
                    verify.verify_release_security(root, "linux-x64")

    def test_archive_stream_must_be_one_canonical_gzip_member(self) -> None:
        epoch = 1_793_068_160
        with self.temporary_directory() as directory:
            scratch = pathlib.Path(directory)
            root = fixture_tree(scratch, "linux-x64")
            package.write_backend_manifest(root / "cores/nxi", "linux-x64")
            package.write_package_manifest(root, "linux-x64", epoch)
            original = scratch / "original.tar.gz"
            package.create_archive(root, original, epoch)
            verify.verify_archive(original, "linux-x64")
            compressed = original.read_bytes()
            payload = gzip.decompress(compressed)
            hidden = payload + b"NXI-HIDDEN-PAYLOAD".ljust(512, b"\0")
            mutations = {
                "trailing": (compressed + b"NXI-UNMANIFESTED-TRAILING-PAYLOAD", "after its gzip member"),
                "second-member": (compressed + gzip.compress(b"NXI-SECOND-MEMBER"), "after its gzip member"),
                "file-name": (gzip_bytes(payload, epoch, "hidden-payload"), "gzip header"),
                "after-tar-end": (gzip_bytes(hidden, epoch), "canonical serialization"),
                "mtime": (gzip_bytes(payload, epoch + 1), "timestamp"),
                "truncated": (compressed[:-12], "truncated"),
                "extra-flags": (compressed[:8] + b"\x02" + compressed[9:], "gzip header"),
                "operating-system": (compressed[:9] + b"\x03" + compressed[10:], "gzip header"),
                "checksum": (compressed[:-8] + bytes([compressed[-8] ^ 1]) + compressed[-7:], "corrupt"),
            }
            for label, (data, message) in mutations.items():
                archive = scratch / f"{label}.tar.gz"
                archive.write_bytes(data)
                with self.subTest(mutation=label), self.assertRaisesRegex(ValueError, message):
                    verify.verify_archive(archive, "linux-x64")

    def test_desktop_installer_uses_absolute_portable_launcher(self) -> None:
        with self.temporary_directory() as directory:
            scratch = pathlib.Path(directory)
            root = scratch / "package with 100% spaces"
            package.add_distribution_files(root)
            installer = root / "install-desktop.sh"
            installer.chmod(0o755)
            environment = os.environ.copy()
            environment["HOME"] = str(scratch / "home")
            environment["XDG_DATA_HOME"] = str(scratch / "xdg data")
            subprocess.run([str(installer)], check=True, env=environment)

            desktop = scratch / "xdg data/applications/nx-infinity.desktop"
            text = desktop.read_text(encoding="utf-8")
            escaped_launcher = str(root / "nx-infinity").replace("%", "%%")
            self.assertIn(f'Exec="{escaped_launcher}"', text)
            self.assertIn(f"Icon={root}/share/icons/hicolor/256x256/apps/nx-infinity.png", text)

    def test_desktop_installer_rejects_control_character_path(self) -> None:
        with self.temporary_directory() as directory:
            scratch = pathlib.Path(directory)
            root = scratch / "bad\npath"
            package.add_distribution_files(root)
            installer = root / "install-desktop.sh"
            installer.chmod(0o755)
            environment = os.environ.copy()
            environment["HOME"] = str(scratch / "home")
            environment["XDG_DATA_HOME"] = str(scratch / "xdg")
            result = subprocess.run(
                [str(installer)],
                env=environment,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
            )
            self.assertNotEqual(0, result.returncode)
            self.assertIn("control characters", result.stderr)

    def test_frontend_report_requires_clean_probe(self) -> None:
        with self.temporary_directory() as directory:
            report = pathlib.Path(directory) / "report.json"
            digest = "0" * 64
            value = {
                "schemaVersion": 1,
                "kind": "nx-infinity-local-validation",
                "runId": "0" * 32,
                "startedUtc": "2026-10-27T00:00:00+00:00",
                "completedUtc": "2026-10-27T00:00:01+00:00",
                "application": "NX Infinity",
                "version": "1.0.0",
                "sdkVersion": "0.3.2-dev",
                "actualPlatform": "linux",
                "guiEventLoopUsed": True,
                "automaticGameplayPass": False,
                "mode": "probe-only",
                "requestedBackend": "nxi",
                "requestedSeconds": 1,
                "contentExplicitlySelected": False,
                "registeredContentSha256": None,
                "appAssemblySha256": digest,
                "appAssemblySha256After": digest,
                "appAssemblyHashScope": "individual-assembly-file",
                "processExecutableSha256": digest,
                "processExecutableSha256After": digest,
                "sdkAssemblySha256": digest,
                "sdkAssemblySha256After": digest,
                "sdkAssemblyHashScope": "individual-assembly-file",
                "loadedIdentity": None,
                "startupSeconds": None,
                "elapsedSeconds": 1.0,
                "launchAcknowledged": False,
                "endReason": "probe_completed",
                "exitStatus": 0,
                "errorCodes": [],
                "launchPhases": [],
                "validationPhase": "prerequisites",
                "windowCloseReason": None,
                "externalCancellationRequested": False,
                "lastFailure": None,
                "prerequisiteIssueCodes": [
                    "keys_missing",
                    "header_key_missing",
                    "firmware_missing",
                ],
                "telemetrySamples": [],
                "telemetryScope": smoke.TELEMETRY_SCOPE,
                "settings": [],
                "sdkReportSha256": digest,
                "installationVerification": {
                    "method": "public-BackendInstallation-and-worker-handshake",
                    "succeeded": True,
                    "observedBackendId": "nxi",
                    "observedRevision": package.CORE_REVISION,
                    "adapterVersion": "1.0.0-nxi",
                    "qualification": "unqualified",
                },
                "manifest": {
                    "backendId": "nxi",
                    "revision": package.CORE_REVISION,
                    "qualification": "unqualified",
                    "sha256": digest,
                    "sha256After": digest,
                    "unchanged": True,
                },
                "shutdown": {
                    "cleanupAttempted": True,
                    "cleanupCompleted": True,
                    "graceful": True,
                    "forcedTermination": False,
                    "failureCode": None,
                },
                "claims": {
                    "visualVerified": False,
                    "physicalAudioVerified": False,
                    "physicalControllerVerified": False,
                    "saveLoadVerified": False,
                    "commercialGameQualified": False,
                    "targetHardwareQualified": False,
                    "officialUpgradeParity": False,
                },
                "limitations": smoke.EXPECTED_LIMITATIONS,
            }
            report.write_text(json.dumps(value), encoding="utf-8")
            self.assertEqual(value, smoke.validate_report(report))
            clean = json.loads(json.dumps(value))
            value["claims"]["visualVerified"] = True
            report.write_text(json.dumps(value), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "clean probe"):
                smoke.validate_report(report)
            value["claims"]["visualVerified"] = False
            del value["claims"]["saveLoadVerified"]
            report.write_text(json.dumps(value), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "clean probe"):
                smoke.validate_report(report)

            contradictions = {
                "automaticGameplayPass": True,
                "contentExplicitlySelected": True,
                "registeredContentSha256": "1" * 64,
                "loadedIdentity": {"programId": "injected"},
                "launchAcknowledged": True,
                "errorCodes": ["injected"],
                "launchPhases": ["gameplay"],
                "validationPhase": "gameplay",
            }
            for field, replacement in contradictions.items():
                changed = json.loads(json.dumps(clean))
                changed[field] = replacement
                report.write_text(json.dumps(changed), encoding="utf-8")
                with self.subTest(field=field), self.assertRaisesRegex(
                    ValueError, "clean probe"
                ):
                    smoke.validate_report(report)
            extra_fields = (
                ("top-level", (), "gameplayQualified"),
                ("installation", ("installationVerification",), "gameplayQualified"),
                ("manifest", ("manifest",), "gameplayQualified"),
            )
            for label, parents, field in extra_fields:
                changed = json.loads(json.dumps(clean))
                target = changed
                for parent in parents:
                    target = target[parent]
                target[field] = True
                report.write_text(json.dumps(changed), encoding="utf-8")
                with self.subTest(extra=label), self.assertRaisesRegex(
                    ValueError, "clean probe"
                ):
                    smoke.validate_report(report)
            changed = json.loads(json.dumps(clean))
            changed["limitations"] = ["Vulkan gameplay is qualified on Linux."]
            report.write_text(json.dumps(changed), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "clean probe"):
                smoke.validate_report(report)

    def test_frontend_smokes_cover_explicit_and_default_data_roots(self) -> None:
        with self.temporary_directory() as directory:
            args = SimpleNamespace(
                launcher="/bin/true",
                xvfb_run="/bin/true",
                data_root=str(pathlib.Path(directory) / "explicit-data"),
                report=str(pathlib.Path(directory) / "default-data-report.json"),
            )
            command, _ = smoke.prepare_command(args)
        self.assertIn("--data-root", command)
        workflow = (HERE.parents[1] / ".github/workflows/checks.yml").read_text(encoding="utf-8")
        self.assertIn('export NUGET_PACKAGES="$RUNNER_TEMP/nuget-packages"', workflow)
        self.assertIn("smoke_default_data.py", workflow)
        self.assertIn("update-preservation.marker", workflow)
        self.assertIn("--phase before-replacement", workflow)
        self.assertIn("--phase after-replacement", workflow)
        self.assertIn("linux-default-data-before.json", workflow)
        self.assertIn("linux-default-data-after.json", workflow)

    def test_default_data_smoke_rejects_stale_startup_evidence(self) -> None:
        with self.temporary_directory() as directory:
            evidence = pathlib.Path(directory) / default_smoke.STARTUP_EVIDENCE
            write(evidence, b"stale lock")
            os.utime(evidence, ns=(1, 1))
            process = subprocess.Popen(["/usr/bin/sleep", "0.05"])
            try:
                with self.assertRaisesRegex(RuntimeError, "exited"):
                    default_smoke.observe_startup(process, evidence, time.time_ns())
            finally:
                if process.poll() is None:
                    process.terminate()
                process.wait()

    def test_default_data_report_is_exact_and_fresh(self) -> None:
        digest = "0" * 64
        value = {
            "evidenceMtimeNs": 2,
            "evidencePath": default_smoke.STARTUP_EVIDENCE,
            "expectedDataRoot": "XDG_DATA_HOME/NX Infinity",
            "externalDataRoot": True,
            "freshEvidence": True,
            "kind": "nx-infinity-default-data-start",
            "launcherSha256": digest,
            "markerSha256After": digest,
            "markerSha256Before": digest,
            "notBeforeNs": 1,
            "phase": "after-replacement",
            "schemaVersion": 1,
        }
        default_smoke.validate_report(value, "after-replacement")
        for field, replacement in (
            ("freshEvidence", False),
            ("evidenceMtimeNs", 0),
            ("markerSha256After", "1" * 64),
        ):
            changed = dict(value)
            changed[field] = replacement
            with self.subTest(field=field), self.assertRaisesRegex(
                ValueError, "fresh external start"
            ):
                default_smoke.validate_report(changed, "after-replacement")
        changed = dict(value)
        changed["gameplayQualified"] = True
        with self.assertRaisesRegex(ValueError, "fresh external start"):
            default_smoke.validate_report(changed, "after-replacement")

    def test_dynamic_audit_has_a_narrow_trace_provider_exception(self) -> None:
        workflow = (HERE.parents[1] / ".github/workflows/checks.yml").read_text(encoding="utf-8")
        self.assertIn('"$missing" == "liblttng-ust.so.0"', workflow)
        self.assertIn('Shared library: [liblttng-ust.so.0]', workflow)
        self.assertIn('"$trace_providers" -eq 2', workflow)
        self.assertNotIn('!= "libcoreclrtraceptprovider.so"', workflow)

    def test_frontend_smoke_rejects_linked_launcher(self) -> None:
        with self.temporary_directory() as directory:
            scratch = pathlib.Path(directory)
            launcher = scratch / "launcher"
            try:
                launcher.symlink_to("/bin/true")
            except OSError as error:
                self.skipTest(f"File symlinks are unavailable: {error}")
            args = SimpleNamespace(
                launcher=str(launcher),
                xvfb_run="/bin/true",
                data_root=str(scratch / "data"),
                report=str(scratch / "report.json"),
            )
            with self.assertRaisesRegex(ValueError, "unavailable"):
                smoke.prepare_command(args)

    def test_archive_rejects_traversal_duplicates_links_and_special_files(self) -> None:
        with self.temporary_directory() as directory:
            scratch = pathlib.Path(directory)
            root = "NX-Infinity-1.0.0-linux-x64"
            cases = (
                ("traversal", [(f"{root}/../escape", tarfile.REGTYPE)]),
                ("duplicate", [(f"{root}/file", tarfile.REGTYPE), (f"{root}/file", tarfile.REGTYPE)]),
                ("symlink", [(f"{root}/link", tarfile.SYMTYPE)]),
                ("hardlink", [(f"{root}/hardlink", tarfile.LNKTYPE)]),
                ("special", [(f"{root}/device", tarfile.CHRTYPE)]),
                ("dot-alias", [(f"{root}/./file", tarfile.REGTYPE)]),
                ("double-slash", [(f"{root}//file", tarfile.REGTYPE)]),
                ("trailing-slash", [(f"{root}/file/", tarfile.REGTYPE)]),
                ("sparse", [(f"{root}/file", tarfile.GNUTYPE_SPARSE)]),
            )
            for label, entries in cases:
                archive = scratch / f"{label}.tar.gz"
                write_archive(archive, root, entries)
                with self.subTest(label=label), self.assertRaises(ValueError):
                    verify.verify_archive(archive, "linux-x64")

    def test_archive_rejects_unsafe_raw_mode(self) -> None:
        with self.temporary_directory() as directory:
            scratch = pathlib.Path(directory)
            root = "NX-Infinity-1.0.0-linux-x64"
            executable = f"{root}/NxInfinity"
            archive = scratch / "unsafe-mode.tar.gz"
            write_archive(
                archive,
                root,
                ((executable, tarfile.REGTYPE),),
                {executable: 0o4777},
            )
            with self.assertRaisesRegex(ValueError, "unsafe mode"):
                verify.verify_archive(archive, "linux-x64")


def fixture_tree(scratch: pathlib.Path, rid: str) -> pathlib.Path:
    root = scratch / f"NX-Infinity-1.0.0-{rid}"
    machine = fake_elf(rid)
    write(root / "NxInfinity", machine, executable=True)
    write(root / "createdump", b"runtime crash helper", executable=True)
    write(root / "NxInfinity.dll", fake_pe())
    write(root / "nx-infinity", b"#!/bin/sh\n", executable=True)
    write(root / "install-desktop.sh", b"#!/bin/sh\n", executable=True)
    write(root / "libSkiaSharp.so", machine)
    write(root / "libHarfBuzzSharp.so", machine)
    write_dependency_graph(
        root / "NxInfinity.deps.json",
        rid,
        verify.verify_dependencies.REQUIRED_LIBRARY_IDENTITIES["launcher"],
    )
    write(root / "cores/nxi/NxInfinity.Core", machine, executable=True)
    write(root / "cores/nxi/createdump", b"runtime crash helper", executable=True)
    core_libraries = set(verify.verify_dependencies.REQUIRED_LIBRARY_IDENTITIES["core"])
    if rid == "linux-x64":
        core_libraries.add("opentk.redist.glfw/3.4.0.44")
    write_dependency_graph(
        root / "cores/nxi/NxInfinity.Core.deps.json",
        rid,
        core_libraries,
    )
    write(root / "cores/nxi/libSDL3.so", machine)
    write(root / "cores/nxi/libshaderc_shared.so", machine)
    write(root / "cores/nxi/libavcodec.so.60", machine)
    write(root / "cores/nxi/libavutil.so.58", machine)
    write(root / "cores/nxi/libSkiaSharp.so", machine)
    if rid == "linux-x64":
        write(root / "cores/nxi/libglfw.so.3", machine)
    write(root / "share/applications/nx-infinity.desktop", b"[Desktop Entry]\n")
    write(root / "share/icons/hicolor/256x256/apps/nx-infinity.png", b"png")
    write(root / "README-LINUX.md", b"# Linux\n")
    write_native_dependency_manifest(root, rid)
    return root


def write_native_dependency_manifest(root: pathlib.Path, rid: str) -> None:
    dependency_manifest = json.loads((HERE / "NATIVE-DEPENDENCIES.json").read_text())
    for relative in verify.REQUIRED_DISTRIBUTION_FILES:
        write(root / relative, b"distribution evidence\n")
    written_notices: dict[str, bytes] = {}
    for dependency in dependency_manifest["dependencies"]:
        for notice in dependency["notices"]:
            name = notice["path"]
            data = written_notices.setdefault(name, f"fixture notice: {name}\n".encode())
            write(root / "licenses/native" / name, data)
            notice["sha256"] = common.sha256_file(root / "licenses/native" / name)
        for record in dependency["assets"].get(rid, []):
            record["sha256"] = common.sha256_file(root / record["path"])
    graphs = {
        "launcher": root / "NxInfinity.deps.json",
        "core": root / "cores/nxi/NxInfinity.Core.deps.json",
    }
    for graph, path in graphs.items():
        dependency_manifest["dependencyGraphs"][rid][graph]["sha256"] = common.sha256_file(path)
    source = root / "licenses/totk-dfps-source"
    files, digest = verify.verify_dependencies.content_tree_identity(source)
    dependency_manifest["correspondingSource"] = {
        "files": files,
        "path": "licenses/totk-dfps-source",
        "sha256": digest,
    }
    package_root = root / "licenses/packages"
    shared_notice = package_root / "licenses/MIT.txt"
    write(shared_notice, b"fixture managed package license\n")
    package_records = []
    for identity, metadata in package_notices.package_libraries(root).items():
        package_records.append(
            {
                "authors": "Fixture authors",
                "copyright": "Fixture copyright",
                "declaredLicense": "expression:MIT",
                "graphContentHash": metadata["sha512"],
                "identity": identity,
                "notices": [
                    {
                        "path": "licenses/MIT.txt",
                        "sha256": common.sha256_file(shared_notice),
                    }
                ],
                "nuspecPath": f"{identity.rsplit('/', 1)[0]}.nuspec",
                "nuspecSha256": "1" * 64,
                "nupkgSha256": "2" * 64,
                "nupkgSha512": "fixture-archive-sha512",
                "projectUrl": "https://example.invalid/fixture",
                "repository": {},
            }
        )
    write(
        package_root / "PACKAGE-METADATA.json",
        common.canonical_json(
            {"packages": sorted(package_records, key=lambda item: item["identity"]), "schemaVersion": 1}
        ),
    )
    write(package_root / "ATTRIBUTIONS.md", b"# Fixture package attributions\n")
    package_files, package_digest = verify.verify_dependencies.content_tree_identity(
        package_root
    )
    dependency_manifest["managedPackageNotices"][rid] = {
        "files": package_files,
        "sha256": package_digest,
    }
    dependency_manifest["producerCodeViewPaths"] = []
    dependency_manifest["embeddedPortablePdbAssemblies"] = []
    source_records = []
    for index, relative in enumerate(
        (
            "licenses/sources/FFmpeg-BUILD-CONFIGURATION.txt",
            "licenses/sources/ffmpeg-source.tar",
        )
    ):
        path = root / relative
        write(path, f"fixture source {index}\n".encode())
        source_records.append(
            {"path": relative, "sha256": common.sha256_file(path)}
        )
    dependency_manifest["redistributionSources"][rid] = source_records
    native_payloads = {}
    for path in common.regular_files(root):
        with path.open("rb") as stream:
            if stream.read(4) == b"\x7fELF":
                native_payloads[path.relative_to(root).as_posix()] = common.sha256_file(path)
    dependency_manifest["nativePayloads"][rid] = dict(sorted(native_payloads.items()))
    package_paths = sorted(
        path.relative_to(root).as_posix()
        for path in common.regular_files(
            root, verify.verify_dependencies.PACKAGE_INVENTORY_EXCLUDED
        )
    )
    dependency_manifest["packageInventory"][rid] = {
        "files": len(package_paths),
        "sha256": hashlib.sha256(
            common.canonical_json({"paths": package_paths})
        ).hexdigest(),
    }
    manifest_path = root / "licenses/NATIVE-DEPENDENCIES.json"
    write(manifest_path, common.canonical_json(dependency_manifest))
    verify.verify_dependencies.NATIVE_DEPENDENCIES_SHA256 = common.sha256_file(manifest_path)


def write_dependency_graph(path: pathlib.Path, rid: str, identities: set[str]) -> None:
    runtime_target = f".NETCoreApp,Version=v10.0/{rid}"
    identities = identities | {
        f"runtimepack.Microsoft.NETCore.App.Runtime.{rid}/10.0.13"
    }
    runtime_identity = f"runtimepack.Microsoft.NETCore.App.Runtime.{rid}/10.0.13"
    libraries = {
        identity: {
            "sha512": f"sha512-fixture-{identity.casefold()}",
            "type": "package",
        }
        for identity in sorted(identities)
        if identity != runtime_identity
    }
    libraries[runtime_identity] = {"type": "runtimepack"}
    value = {
        "compilationOptions": {},
        "libraries": libraries,
        "runtimes": {},
        "runtimeTarget": {"name": runtime_target, "signature": ""},
        "targets": {
            ".NETCoreApp,Version=v10.0": {},
            runtime_target: libraries,
        },
    }
    write(path, common.canonical_json(value))


def write_archive(
    path: pathlib.Path,
    root: str,
    entries: tuple[tuple[str, bytes], ...],
    mode_overrides: dict[str, int] | None = None,
) -> None:
    modes = mode_overrides or {}
    with path.open("xb") as raw, gzip.GzipFile(
        filename="", mode="wb", fileobj=raw, mtime=1, compresslevel=6
    ) as compressed, tarfile.open(fileobj=compressed, mode="w") as archive:
        root_info = tarfile.TarInfo(root + "/")
        root_info.type = tarfile.DIRTYPE
        root_info.mode = 0o755
        root_info.mtime = 1
        archive.addfile(root_info)
        for name, entry_type in entries:
            info = tarfile.TarInfo(name)
            info.type = entry_type
            info.mode = modes.get(name, 0o644)
            info.mtime = 1
            if entry_type == tarfile.REGTYPE:
                info.size = 1
                archive.addfile(info, io.BytesIO(b"x"))
            else:
                if entry_type in (tarfile.SYMTYPE, tarfile.LNKTYPE):
                    info.linkname = "target"
                archive.addfile(info)


def elf_header(rid: str, program_headers: int) -> bytearray:
    machine = {"linux-x64": 62, "linux-arm64": 183}[rid]
    value = bytearray(64)
    value[:6] = b"\x7fELF\x02\x01"
    struct.pack_into("<HH", value, 0x10, 3, machine)
    struct.pack_into("<Q", value, 0x20, 64)
    struct.pack_into("<HH", value, 0x36, 56, program_headers)
    return value


def fake_elf(rid: str) -> bytes:
    return bytes(elf_header(rid, 1) + bytes(56))


def fake_elf_needing(rid: str, needed: tuple[str, ...]) -> bytes:
    strings = bytearray(b"\0")
    offsets = []
    for name in needed:
        offsets.append(len(strings))
        strings += name.encode("ascii") + b"\0"
    strings_offset = 64 + 2 * 56
    dynamic_offset = strings_offset + (len(strings) + 7) // 8 * 8
    virtual = 0x10000
    dynamic = b"".join(struct.pack("<qQ", 1, offset) for offset in offsets)
    dynamic += struct.pack("<qQqQqQ", 5, virtual + strings_offset, 10, len(strings), 0, 0)
    size = dynamic_offset + len(dynamic)
    value = elf_header(rid, 2)
    value += struct.pack("<IIQQQQQQ", 1, 5, 0, virtual, virtual, size, size, 0x1000)
    value += struct.pack("<IIQQQQQQ", 2, 6, dynamic_offset, virtual + dynamic_offset, 0, len(dynamic), len(dynamic), 8)
    value += bytes(strings).ljust(dynamic_offset - strings_offset, b"\0") + dynamic
    return bytes(value)


def codeview_record(path: str, magic: bytes = b"RSDS") -> bytes:
    record = magic + bytes(16) + struct.pack("<I", 1) + path.encode("utf-8") + b"\0"
    return record.ljust(284, b"\0") if magic == b"R2RM" else record


def fake_pe(records: tuple[tuple[int, bytes], ...] = ((16, b""),)) -> bytes:
    header, optional_size, raw = 0x40, 240, 0x200
    optional = header + 24
    directory_size = 28 * len(records)
    entries = bytearray()
    payload = bytearray()
    for kind, record in records:
        pointer = raw + directory_size + len(payload) if record else 0
        address = 0x1000 + pointer - raw if record else 0
        entries += struct.pack("<IIHHIIII", 0, 0, 0, 0, kind, len(record), address, pointer)
        payload += record
    image = bytearray(raw)
    image[:2] = b"MZ"
    struct.pack_into("<I", image, 0x3C, header)
    image[header:header + 4] = b"PE\0\0"
    struct.pack_into("<HHIIIHH", image, header + 4, 0x8664, 1, 0, 0, 0, optional_size, 0x2022)
    struct.pack_into("<H", image, optional, 0x20B)
    struct.pack_into("<I", image, optional + 108, 16)
    struct.pack_into("<II", image, optional + 112 + 48, 0x1000 if records else 0, directory_size)
    section_size = max(directory_size + len(payload), 1)
    struct.pack_into("<8sIIII", image, optional + optional_size, b".text", section_size, 0x1000, section_size, raw)
    return bytes(image + entries + payload)


def gzip_bytes(payload: bytes, epoch: int, filename: str = "") -> bytes:
    stream = io.BytesIO()
    with gzip.GzipFile(filename=filename, mode="wb", fileobj=stream, mtime=epoch, compresslevel=6) as compressed:
        compressed.write(payload)
    return stream.getvalue()


def write(path: pathlib.Path, data: bytes, executable: bool = False) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)
    path.chmod(0o755 if executable else 0o644)


REPOSITORY_ARCHIVE_SETTINGS = (
    ("core.autocrlf", "true"),
    ("core.eol", "crlf"),
    ("core.bigFileThreshold", "1"),
    ("tar.umask", "0022"),
)
MISSING_GIT_HOME = "/nonexistent/nx-infinity-test-git-home"


def fixture_environment() -> dict[str, str]:
    environment = {name: value for name, value in os.environ.items() if not name.startswith("GIT_")}
    environment.update(
        {
            "GIT_ATTR_NOSYSTEM": "1",
            "GIT_CONFIG_GLOBAL": "/dev/null",
            "GIT_CONFIG_NOSYSTEM": "1",
            "HOME": MISSING_GIT_HOME,
            "XDG_CONFIG_HOME": MISSING_GIT_HOME,
        }
    )
    return environment


def fixture_git(
    repository: pathlib.Path,
    *arguments: str,
    stdin: bytes | None = None,
    environment: dict[str, str] | None = None,
) -> bytes:
    return subprocess.run(
        ["git", "-C", str(repository), *arguments],
        input=stdin,
        env=environment or fixture_environment(),
        stdout=subprocess.PIPE,
        check=True,
    ).stdout


def git_returncode(repository: pathlib.Path, arguments: list[str], environment: dict[str, str]) -> int:
    return subprocess.run(
        ["git", "-C", str(repository), *arguments],
        env=environment,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    ).returncode


def git_fixture_repository(repository: pathlib.Path) -> tuple[pathlib.Path, str]:
    repository.mkdir()
    fixture_git(repository, "init", "-q", "--template=")
    write(repository / ".gitattributes", b"crlf.txt eol=crlf\ntext.txt text\n")
    write(repository / "configure", b"#!/bin/sh\n")
    write(repository / "crlf.txt", b"first\nsecond\n")
    write(repository / "text.txt", b"plain\n")
    fixture_git(repository, "add", ".")
    fixture_git(
        repository,
        "-c", "user.name=Fixture",
        "-c", "user.email=fixture@example.invalid",
        "-c", "commit.gpgsign=false",
        "commit", "-q", "-m", "fixture",
    )
    return repository, fixture_git(repository, "rev-parse", "HEAD").decode().strip()


def fixture_archive_arguments(revision: str) -> list[str]:
    return ["archive", "--format=tar", f"--prefix=ffmpeg-{revision[:10]}/", revision]


def fixture_sources(revision: str, stream: bytes) -> dict[str, object]:
    configuration = f"revision={revision}\nconfigure=--fixture\n".encode()
    source = source_bundle.FfmpegSource(
        revision,
        "ffmpeg-fixture-source.tar",
        hashlib.sha256(stream).hexdigest(),
        "--fixture",
        hashlib.sha256(configuration).hexdigest(),
    )
    return {"linux-x64": source}


def replace_fixture_blob(repository: pathlib.Path, revision: str) -> None:
    replacement = fixture_git(repository, "hash-object", "-w", "--stdin", stdin=b"replaced\n")
    original = fixture_git(repository, "rev-parse", f"{revision}:crlf.txt").strip()
    fixture_git(repository, "replace", original.decode(), replacement.strip().decode())


def real_git() -> str:
    return f'exec {shlex.quote(shutil.which("git") or "git")} "$@"'


def git_wrapper(directory: pathlib.Path, body: str) -> str:
    write(directory / "git", f"#!/bin/sh\n{body}\n".encode(), executable=True)
    return f"{directory}{os.pathsep}{os.environ.get('PATH', os.defpath)}"


def hostile_git_environment(scratch: pathlib.Path) -> dict[str, str]:
    home = scratch / "hostile-home"
    write(home / ".gitconfig", b"[core\nbroken\n")
    xdg = scratch / "hostile-xdg"
    write(xdg / "git/attributes", b"* export-ignore\n")
    other = scratch / "other"
    other.mkdir()
    fixture_git(other, "init", "-q", "--template=")
    return {
        "HOME": str(home),
        "XDG_CONFIG_HOME": str(xdg),
        "GIT_DIR": str(other / ".git"),
        "GIT_CONFIG_PARAMETERS": "broken",
        "GIT_CONFIG_COUNT": "1",
    }


if __name__ == "__main__":
    unittest.main(verbosity=2)
