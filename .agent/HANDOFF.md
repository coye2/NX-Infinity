# PR 1 handoff — reproducible source baseline

## Scope and baseline

- Branch: `feat/reproducible-source-baseline`
- Base: `974459caca0ef6daf9151f417dc9bc76ffaec46a`
- Invariant: restore a reviewable, buildable v1 launcher/core-host/core source baseline without vendoring the unchanged Kenji tree or claiming decompiled source is byte-identical original source.
- Out of scope: native Linux release packaging, Android, gameplay qualification, device testing, deployment and release publication.

## Verified observations and decisions

- The release source manifest SHA-256 is `27dc579656ce0ae36fa1dce95b0406e04a93ff0f81d8f815818a60b27dabeee3`.
- Core coverage: 3,930 files exact at Kenji `7d1fd99d3dc1c230ef7f823e96f187b3de619e4a`; 75 exact Kenji-history sources bound to 11 commit/path/blob tuples; 13 exact files at later Ryujinx `20888e6d1f96bcf1a59f415c745c684e38529503`; 62 managed-IL reconstructions; one unavailable test-only source; five decompiler-split helper files.
- The launcher and core host are checked-in managed-IL source reconstructions. Their observed release informational versions are preserved.
- Hello Kitty artwork is not redistributed. The existing URL catalog is used on demand. The zero-byte brand placeholder is omitted. The public NX logo and GPL-covered timing module are retained with their existing provenance/source.
- The core host uses non-building project references to recover the transitive dependency graph, then copies the already built core assemblies into build/publish output.

## Changed paths

- Recovered products: `src/app/NxInfinity/`, `src/core/NxInfinity.Core/`
- Core reconstruction overlays: `src/core/overlays/`
- Deterministic restoration and smoke checks: `build/source-baseline/`, including the host publish inventory
- Build/CI: `src/NxInfinity.slnx`, `.github/workflows/checks.yml`, `.gitattributes`, `.gitignore`
- Documentation/bookkeeping: `README.md`, `src/README.md`, `CONTRIBUTING.md`, `CHANGELOG.md`, `specs/security/EXCEPTIONS.md`, `specs/verifications/AUDIT-pr1-source-baseline.md`
- Licensing: `LICENSING.md`, `THIRD-PARTY-NOTICES.md`, `licenses/Ryujinx-Team-MIT.txt`, `third_party/totk-dfps/NX-NOTICE.md`

## Verification evidence

All scratch/build logs are under `/home/garyb/Mounts/NVME/OpenChamber/NX-Infinity/`; no device or adb was used.

- Restorer, Python 3: PASS. Fresh round-2 output `review2-core-20261026/` and post-review provenance output `review3-core-20261026/`; both receipts report 3,930 + 75 + 13 + 62, one unavailable test source and five helpers. The latter used a fresh shallow repository containing only the explicitly fetched base plus 11 history commits.
- Restored `Ryujinx.HLE`, .NET SDK 9.0.100: PASS, inherited NuGet audit warnings, 0 errors.
- Restored `Ryujinx.Graphics.Vulkan`, .NET SDK 9.0.100: PASS, 100 inherited warnings, 0 errors.
- Restored OpenAL backend, .NET SDK 9.0.100: PASS, inherited NuGet audit warnings, 0 errors.
- Launcher/runtime/test solution, .NET SDK 10.0.402: PASS, 0 warnings, 0 errors.
- Core-host framework-dependent publish, .NET SDK 10.0.402: PASS (`review2-host-20261026/`).
- `smoke_worker.py`: PASS; exact 107-library and 135-asset inventory, every declared asset present, protocol hello, exact backend revision and clean disposed event.
- Runtime contract executable: 63 passed, 0 failed.
- Runtime services executable: 73 passed, 1 explicitly unavailable Windows journal integration.
- Source-baseline tool tests: 15 passed, including commit/path/blob binding. The original round-2 first invocation named a nonexistent scratch directory and failed its explicit precondition; its rerun used the existing mounted NVMe project directory.
- `actionlint`: PASS.
- Python/JSON/XML syntax parsing: PASS.
- Rebuilt vs shipped non-compiler-generated type inventories: launcher 122/122 exact; core host 37/37 exact (ILSpy 11.1.0).
- Launcher package inventory: shipped package set retained; Linux/macOS Skia/HarfBuzz native assets are the only build-host additions.
- Core-host package inventory: portable package graph matches v1; the released Windows FFmpeg package is replaced only by build-host Skia native assets in the no-RID Linux publish. RID-native packaging remains PR 2.

## Limits and known issues

- No Windows execution, launcher GUI smoke, game, firmware, key, audio, Vulkan/gameplay or physical-device test was run.
- `System.Private.Uri` 4.3.0 and `System.Text.Json` 8.0.0 retain known NuGet advisories from the pinned baseline. The source-baseline README records them; dependency upgrades require separate qualification.
- Reconstructed OCR models and avatar content are mutable, size-bounded network inputs without pinned hashes. `specs/security/EXCEPTIONS.md` blocks release until they are pinned and verified.
- Vulkan warnings are inherited obsolete Silk.NET enum names plus CS0675/CS9193 in reconstructed source; builds have zero errors.
- The canonical Kenji host is an external restoration input; CI availability depends on that public service.
- `src/Ryujinx.Graphics.Gpu/NXI/TestAccess.cs` is unavailable, not present in shipped IL and not required by production builds.

## Review, commit and push

- Review round 1: both blind reviewers returned `NEEDS CHANGES` at 90/100 for frozen snapshot `cae90807cd5124fe5fc989f7d4b707468c4f8edcf85a80b414d23a783ca57c0a` (`hashes.sha256`). Reviewer sessions: `ses_e84f2974cffeRMz5dV7RTFZpDL` and `ses_e84f29711ffeWFrWqDNVH3uVm7`.
- Round 1 findings resolved before this record: external exact sources now come from pinned Git objects; local hash-sensitive text is LF-normalized; Windows/path/symlink escapes are rejected; the host gate checks the exact 107-library/135-asset inventory and every file; protocol output is schema-strict; inherited mutable network content is release-blocking; the stale timing-module path is corrected.
- Review round 2: one blind reviewer returned `PASS` at 97/100 for snapshot `e07e44b1c487c9674bd8899e5a5e3e913cfebae43f76fb15a63a97948dec1f07`. The replacement for an interrupted second review returned `NEEDS CHANGES` at 96/100: only 2/75 history blobs were reachable from the declared base, while the other objects relied on mutable branch refs.
- Round 2 finding resolved: all 75 history entries now identify their source commit, path and blob; restoration verifies that tuple before SHA-256/byte count; `--print-kenji-revisions` emits the immutable 12-commit closure; CI explicitly shallow-fetches it. All 12 commits were fetched directly by SHA from the canonical Kenji remote, and a fresh restoration passed from that closure. The first local workflow reproduction used zsh for the Bash-only `mapfile` command and failed before setup; the Bash rerun fetched all commits, then a mistyped later-source scratch path failed before output creation; the corrected restore passed.
- Pre-round-3 snapshot audit found that the prior packet's path/hash list covered 211 files but omitted two in-scope files present in the tree: `build/source-baseline/host-publish-inventory.json` and `third_party/totk-dfps/NX-NOTICE.md`. Round 3 must enumerate and hash the complete 213-path status-derived set; the prior packet cannot approve those two paths.
- Review round 3: freeze the post-provenance-fix tree and obtain a focused delta re-review. Any resulting content change requires another snapshot.
- Commit: none.
- Push: not performed.
- Next action: obtain `PASS` for the exact round-3 snapshot, then explicitly stage, commit and push only that reviewed snapshot.
