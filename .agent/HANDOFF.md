# PR 2 handoff — native Linux archives

## Scope and baseline

- Branch: `feat/native-linux-builds`
- Base: `b26ba273dc83fb757b2548519f1ee44e3d4d6df2` (the exact reviewed PR 1 source-baseline commit)
- Invariant: build deterministic, integrity-checked native `linux-x64` and `linux-arm64` archives with self-contained launcher/core apphosts, correct native assets, desktop metadata, notices and a real launcher/worker smoke path.
- Out of scope: Android, Flatpak/AppImage, game/firmware/key distribution, gameplay qualification, deployment and release publication.

## Decisions and implementation

- `build/linux/package.py` builds the restored .NET 9 core for both framework-only and RID-specific references, publishes the .NET 10 launcher/core apphosts, stages the archive, writes exhaustive manifests and creates a deterministic gzip/tar stream.
- `cores/nxi/backend.json` declares a native executable (`managedAssembly: false`) and hashes every core-host file. `NX-LINUX-BUILD.json` hashes every other package file and records RID, epoch, source baseline, qualification and system dependencies.
- Both packages remain `unqualified`. CI/Xvfb does not make gameplay, Vulkan, audio, controller, save or physical ARM64 claims.
- The package uses system `libopenal.so.1`. Bundled FFmpeg libraries are shared, and the launcher exports the core directory through `LD_LIBRARY_PATH` so `libavcodec.so.60` resolves its adjacent `libavutil.so.58`.
- Linux app builds define `NXI_NATIVE_LINUX`, which removes mutable online avatar/OCR download paths at compile time. Local avatar import remains available; game-window OCR is not offered as a Linux capability.
- Final core publications use the SDK 10.0.402 runtime pack `runtimepack.Microsoft.NETCore.App.Runtime.<rid>/10.0.13`. The verifier rejects the vulnerable v1 standalone package identities even though compiling the unchanged restored v1 source still emits its documented NuGet advisories.
- Restored-core builds disable debug records, map their source root to
  `/_/nx-core`, and force non-incremental compilation. Product publishes map
  both repository and work roots. PDB files are removed before publish and
  verifier-rejected.
- Archives include project/Ryujinx notices, exact package/native hashes, a
  generated notice inventory covering every package in both `.deps.json`
  graphs, the .NET license/third-party notices, and complete modified
  corresponding source for the embedded GPL timing module. Package notices are
  read from verified `.nupkg` bytes; immutable Shaderc dependency notices are
  fetched through `NOTICE-SOURCES.json`.
- Bundled FFmpeg libraries are accompanied by deterministic per-RID source
  archives and binary-recorded build configurations: x64 uses
  `39cac587c4f78f7c26e0f0e5da664943d4e9fade`; ARM64 uses
  `e16ff06adb9acd647c0c33aa3c27438e1009b822`. The verifier pins the selected
  source files, every package path, and every ELF path/hash.
- Twelve immutable third-party package assemblies retain producer CodeView
  paths. Each path is bound to the exact assembly path and SHA-256; every
  additional or changed Unix, drive-qualified, or UNC producer path is rejected.
- `install-desktop.sh` writes an absolute, quoted per-user desktop entry only after the user moves the extracted archive to its permanent location. User data remains outside the archive at the platform local-data root.

## Changed paths

- Build and verification: `build/linux/`, `.github/workflows/checks.yml`
- RID/product behavior: `src/app/NxInfinity/{AvatarCatalog.cs,AvatarDialog.cs,BackendChoice.cs,MainWindow.cs,NxInfinity.csproj}`, `src/core/NxInfinity.Core/NxInfinity.Core.csproj`, `src/Directory.Build.props`
- Documentation/security/licensing: `README.md`, `docs/linux.md`, `docs/testing.md`, `src/README.md`, `CHANGELOG.md`, `LICENSING.md`, `THIRD-PARTY-NOTICES.md`, `specs/security/EXCEPTIONS.md`, `specs/verifications/AUDIT-pr2-native-linux.md`

## Verification evidence

All generated files and logs are under `/home/garyb/Mounts/NVME/OpenChamber/NX-Infinity/`.
No device, adb, real GPU or network was used during round-6, round-7, round-8
or round-10 verification. Priority evidence is stated per payload below; no
blanket uninterrupted-priority claim is made.

- PR 1 fork CI run `40438778445`: PASS at base commit; both `checks` and `source-baseline` jobs green.
- Source-baseline Python tests: 15 passed.
- Native Linux Python tests: 44 passed (paths/collisions/aliases, malicious
  archives, raw modes, trailing gzip data and canonical tar streams, strict
  JSON, ELF architecture/format and `DT_NEEDED` closure, exact
  policy/manifests, complete package/source notice coverage, vulnerable
  identities, publish symlinks, path-map/debug controls, structured UTF-8
  CodeView paths, packaged link resolution, durable fresh default-data
  evidence, hardened desktop integration and exact no-game smoke reports).
- Round-10 window (coordinator GO, 13:41:08–13:42:03 EDT, driver
  `tools/round10_window.py` with the same 10 ms per-thread watcher,
  `NXI_WINDOW_ROOT=pr2-round10`, Python only, zero network URLs in the log
  `round10-driver.log` SHA-256
  `3cf35e51f5ba62799e54a2a1e4757837ff65e512ac43eecea5399013e1caa628`). It
  reassembled four archives from the unchanged round-8 app publishes and
  round-2 core publishes, so the archives reflect the current tree: no C#
  change since round 8, plus the round-10 uncompressed FFmpeg source bundles.
  These supersede the round-8 archive identities.
  - x64-A and x64-B (host zlib-ng 1.3.1) are byte-identical, SHA-256
    `839efa187f8173c1f067529ed807f36d67b38a8514d45b6547fadb22d36c710b`,
    117,608,135 bytes, 813 files, 40 x86-64 ELF files.
  - x64-classic used the same inputs as x64-A, with the assembler and verifier
    loading classic zlib 1.3.1 (`classic-zlib/libz.so.1`, SHA-256
    `1b741cc7e0c9ec4fef8ced7530729aeb1f8ce42f23d0c7f082490d6f6719513c`, a
    read-only copy of the installed Freedesktop 25.08 runtime library). It
    assembled without a source-identity mismatch and verified, SHA-256
    `59b0ce902dad7287d91d59c5f401e4b3e1dabedf4b53dd6bcd8c28379555f2e1`
    (114,385,917 bytes). Its compressed bytes differ from x64-A, but its tar
    stream is identical: all three x64 archives carry tar stream
    `94118539b9e36d9f9fa274fb8be3406a5feafdb65ec9f5029ad25ee2b66df65a`
    (328,437,760 bytes).
  - ARM64 SHA-256
    `dc655a050549156c4bcafee88f54e07a77fe0f311883a2198530bdf744266d5c`,
    112,079,239 bytes, 811 files, 39 AArch64 ELF files, tar stream
    `1a8c4984afed882e8a740b93f07ec6964fbfbddd9408d1b4a4ef53b21b01eb8b`
    (336,752,640 bytes). Not executed on ARM64 hardware.
  - Bundled FFmpeg sources: every x64 archive carries
    `ffmpeg-39cac587c4-source.tar` (`7cdc5c54…d9d4`) and ARM64 carries
    `ffmpeg-e16ff06adb-source.tar` (`42cad98c…e9e66`), with their pinned
    build configurations.
  - x64-A smoke: Xvfb probe report
    `3d223f83383c10ce714eb80d97878f8d0812033d84c1d6c04867f2eccbeed1a4`;
    default-data reports
    `fae41785e84429a0b7fd99fb3c1c5f6ce5b07afada218b35413220c12a6ff971` and
    `5347eccd438b5efc0af8c59310fd5b3daeee8a39207f9723ad9abc6d290cf013`;
    marker `0762b626…f0f1` unchanged across the install replacement.
  - Priority: three exposures, each re-pinned to nice 10. In each of the three
    smoke starts one launcher thread lowered itself to nice 19 about 3.2 s in
    (pids/tids 255825/255878, 256640/256699, 257491/257598). No other exposure
    was observed, including the driver.
- Round-8 window (coordinator GO, 13:01:26–13:02:49 EDT, same driver with
  `NXI_WINDOW_ROOT=pr2-round8`, `RestoreSources` pinned to the local NuGet
  folder, zero network URLs in the log). It covers the current tree, including
  the round-7 review fixes. Solution build (no RID) 0 warnings/0 errors;
  contract tests 63/63; service tests 73 plus one Windows-only unavailable;
  three fresh app-only publishes, x64 A/B app trees identical; core publishes
  reused unchanged from round 2. Each native Linux app publish emits one
  warning, `CS0649` for the never-assigned `_translator` field; it is harmless
  because both readers, `StopTranslation` and the unreachable
  `TranslateGameFrameAsync`, check for null.
- Round-8 archives (static verifier PASS, sidecars exact):
  - x64-A and x64-B byte-identical, SHA-256
    `f4eef89646dd7ed8c6931b0d00b6f3c43860cd54a164ca9d3da8177dcfe806fa`,
    813 files, 40 x86-64 ELF files. App trees `pr2-round8/x64-a-work/app-publish`
    and `pr2-round8/alternate-root/x64-b/work/app-publish`; core trees
    `pr2-round2-x64-a3-work/core-publish` and
    `pr2-round2-x64-b3-work/core-publish`; FFmpeg source
    `39cac587c4f78f7c26e0f0e5da664943d4e9fade`.
  - ARM64 SHA-256
    `02bc64d825d5d2176e3bb13e50de697062e28c6bb0fe32eef5f0d1077740fecc`,
    811 files, 39 AArch64 ELF files. App tree `pr2-round8/arm64-work/app-publish`;
    core tree `pr2-round2-arm64-work/core-publish`; FFmpeg source
    `e16ff06adb9acd647c0c33aa3c27438e1009b822`. Not executed on ARM64 hardware.
  - The packaged launcher no longer contains "Hello Kitty & Friends" or the
    Nintendo Switch Online attribution. Inert, never-shown residue remains and
    is listed in `specs/security/EXCEPTIONS.md`: unattached dialog controls,
    `UiText` keys, the `Repository`, `IslandSource` and `IslandAttribution`
    constants, and
    unreachable translation/OCR code.
- Round-8 x64 smoke: one CPU-only Xvfb probe (report SHA-256
  `966f4be43ab339c2addd80e120d8330979cdf2be4e3d7044af4a85cb0c3b83c2`) and two
  default-data starts around an install replacement (reports
  `e4fca44847d1fac9625ce267bdd49a3b022476c32853d1847044034ffd0a7d34` and
  `3246ab4609efb081442947a75b602ed5a2dc6d6a296c0e864349bcea055dc125`); marker
  `0762b626650be8ec77af17481ca4a83caa452cf7086c1933ea832d677142f0f1`
  unchanged.
- Round-8 priority: the 10 ms per-thread watcher recorded four exposures, each
  re-pinned to absolute nice 10: the driver itself at nice -4 at 0.378 s into
  the build, and the launcher thread that lowers itself to nice 19 about 3.2 s
  into each of the three smoke starts. No other exposure was observed.

Round-7 evidence, superseded for the archive identities by round 8:
- Round-7 window (coordinator GO, 12:34:16–12:35:39 EDT, one watched driver
  `tools/round7_window.py`, scratch `pr2-round7/`, `RestoreSources` pinned to
  the local NuGet folder, zero network URLs in the log): solution build 0
  warnings/0 errors; contract tests 63/63; service tests 73 plus one
  Windows-only unavailable; three fresh app-only publishes; x64 app publishes
  from two absolute roots tree-identical; core publishes reused unchanged from
  round 2.
- Round-7 archives (static verifier PASS, sidecars exact):
  - x64-A and x64-B byte-identical, SHA-256
    `e25c097cb6f464d57c0fbc5bc1beb45bd2b9272ad22035aaa0ceda3398c93db3`,
    813 files, 40 x86-64 ELF files. App trees `pr2-round7/x64-a-work/app-publish`
    and `pr2-round7/alternate-root/x64-b/work/app-publish`; core trees
    `pr2-round2-x64-a3-work/core-publish` and
    `pr2-round2-x64-b3-work/core-publish`; FFmpeg source
    `39cac587c4f78f7c26e0f0e5da664943d4e9fade`.
  - ARM64 SHA-256
    `6629791482e20208d04fd9268356f5337ac9de6c392f31515364f0daf3322dc9`,
    811 files, 39 AArch64 ELF files. App tree `pr2-round7/arm64-work/app-publish`;
    core tree `pr2-round2-arm64-work/core-publish`; FFmpeg source
    `e16ff06adb9acd647c0c33aa3c27438e1009b822`. Not executed on ARM64 hardware.
- Round-7 x64 smoke: one CPU-only Xvfb probe (launcher and core apphosts
  observed, report SHA-256
  `f24bb0b03f5ea3a95cc17da6c049a5dac63cb681e2528c957e94ce2ba4a9671e`) and
  two default-data starts around an install replacement (reports
  `19f611f67b13b994d6e943cb924a86194d9225dff4fe55106e1d56d19c2731f8` and
  `f52d268aae2796b6571b317457b46c9f19a5e402f08254df2cc81a0a4b95dd15`);
  marker `0762b626650be8ec77af17481ca4a83caa452cf7086c1933ea832d677142f0f1`
  unchanged.
- Round-7 priority: a 10 ms watcher checked every thread of the descendant tree
  and recorded four exposures, each re-pinned to absolute nice 10: the driver
  itself at nice -4 for at most 84 ms at build start, and one launcher thread
  that lowers itself to nice 19 about 3.2 s into each of the three smoke
  starts. No other exposure was observed.

Earlier evidence, superseded for the archive identities by round 7:
- `actionlint .github/workflows/checks.yml`: PASS.
- `git diff --check b26ba273...`: PASS.
- Code/security self-audit: PASS; generic unavailable-tool and function-length limits are explicit in `specs/verifications/AUDIT-pr2-native-linux.md`.
- .NET SDK 10.0.402 solution build to NVMe artifacts: PASS, 0 warnings, 0 errors.
- Runtime contract executable: 63 passed, 0 failed.
- Runtime services executable: 73 passed, one explicitly unavailable Windows-only integration.
- Restored core builds used .NET SDK 9.0.100. HLE/OpenAL/Vulkan succeeded for no RID, x64 and ARM64. Inherited NuGet advisory and obsolete-Silk warnings remain in source-build logs; final archive dependency manifests exclude the vulnerable standalone package identities.
- Final round-6 x64 archive: SHA-256
  `33dc6f36dfe45a1cced3119bfa1bda967111e421851e3ee69b331f33252790dc`;
  813 total files, 40 x86-64 ELF files; hardened static
  policy/manifest/checksum PASS. The unchanged ELF payload retains the prior
  dynamic audit of 38 files with `unresolved=0`.
- Clean-build reproducibility: two complete x64 builds from different absolute
  restored-core and work roots have identical payloads. The compliant x64-A
  archive and fresh RUN LOG 133 x64-B reassembly from the unchanged second
  publish tree are byte-identical at the x64 SHA-256 above.
- Final round-6 ARM64 archive: SHA-256
  `8b6aefc05691b3144d49aadf80488985ccb0a57f3134fc1ee61935edbee3d137`;
  811 total files, 39 AArch64 ELF files; hardened static
  policy/manifest/checksum PASS. It was not executed on ARM64 hardware.
- Final round-6 x64 Xvfb probe: PASS. The CPU-only display launched the native
  Avalonia apphost and native core apphost at absolute nice 10, verified the
  exact backend revision and unchanged manifest, rejected gameplay claims, and
  recorded graceful cleanup with exit 0.
- Production-default data check: replacement-separated starts established the
  update-preservation behavior, but the first RUN LOG 130 validator was directly
  observed at nice -4 and is not resource-compliance evidence. RUN LOG 133
  repeated one functionally identical normal start under a 10 ms descendant
  watcher: 332 cycles, no detected validator/Xvfb/launcher priority exposure,
  strict report SHA-256
  `b86bd335a9c9a1f88375eda26ad7a9fb3c36d55f70c3fe7316e2037c43d36519`,
  and unchanged marker SHA-256
  `0762b626650be8ec77af17481ca4a83caa452cf7086c1933ea832d677142f0f1`.

## Failed probes and corrections

- Early host publishes redirected the restored project references into an empty global artifacts tree. Output redirection is now scoped to the NX host, and the core is built in its NVMe source tree for the required reference shapes.
- RID publishing then exposed missing mixed framework/RID references and three duplicate direct outputs. The pipeline now builds both reference shapes and excludes only the three outputs MSBuild already publishes.
- One checksum command ran from the repository instead of the archive directory and correctly failed to find the relative archive; rerunning from the checksum file's directory passed.
- The first Xvfb report was valid, but the smoke checker compared hexadecimal revisions case-sensitively. It now accepts canonical hex case and additionally observes/renices the actual launcher and core payloads, not only the Xvfb wrapper.
- Round-1 cross-root x64 builds exposed two nondeterministic runtime DLLs. Their
  generated type/MVID bytes depended on the publish work path. Product
  `PathMap` now maps repository and work roots; escaped `%2C` is required so
  MSBuild receives the comma inside one property value. The corrected pair is
  byte-identical.
- The first hardened verifier expected notices at the archive root instead of
  `licenses/`; that negative check failed, the paths were corrected, and both
  architecture archives then passed.
- Validation mode intentionally requires an explicit data root, so it cannot
  prove the normal default. A separate bounded normal-startup smoke now tests
  production default-path behavior without weakening validation safeguards.
- The first bounded normal-startup helper also waited for optional HOME-view
  state and timed out after the required profile store was already present.
  Its readiness condition now uses the required profile store only; both final
  before/after replacement runs passed.
- One rejected normal-smoke launch inherited nice 19 after applying a relative
  `nice` increment. Only its NX process group was stopped; both smoke scripts
  now set and print their own absolute priority, and final runs proved nice 10.
- The first archive-input comparison used `path` as a zsh loop variable, which
  replaced zsh's command-search path and made the loop invalid. The rerun used
  a neutral variable plus absolute `cmp`/`basename` commands and proved every
  repository-owned package input matches the final staged x64 archive.
- Round-3 reviewers demonstrated that translation payloads, mutable backend
  provenance, unsafe raw tar modes, contradictory gameplay evidence and stale
  default-data files could pass. They also found the redistributed Concentus
  notice missing. The verifier, report schema, startup evidence and notice
  policy now fail closed, with direct regressions for each case.
- The first round-4 archives exposed that manifests written after mode
  normalization inherited the process's restrictive `0600` umask. Manifest
  writers now explicitly set `0644`; a restrictive-umask regression passes,
  and all three final archives pass raw-member-mode verification.
- Round-4 reviewers found incomplete managed/Shaderc notices, missing FFmpeg
  corresponding-source access, additive smoke schemas, non-durable default-data
  evidence, and undisclosed absolute paths embedded by third-party package
  producers. Round 5 now generates a complete graph-derived package notice
  inventory, pins six external Shaderc dependency notices, bundles exact FFmpeg
  source/build inputs, uses exact report schemas, emits two durable default-data
  reports, and rejects any producer CodeView path not independently bound to an
  exact assembly.
- During initial round-6 verification, ananicy reset the x64-B assembler and
  first default-data validator to nice -4 after one-time nice-10 assertions.
  Those executions remain functional observations only and are not
  resource-compliance evidence. The coordinator authorized RUN LOG 133 to
  repeat exactly those two steps with a continuous descendant watcher.
- RUN LOG 133 re-pinned and sampled every live process every 10 ms, with
  independent `/proc` observations each cycle and `ps` output about every
  200 ms. The actual x64-B assembler and its observed Git child had no detected
  priority reset across 581 cycles; the watcher itself was observed once at
  nice -4 at 0.402 seconds and immediately restored to nice 10. The default-data
  validator, Xvfb wrappers and native launcher had no detected reset across
  332 cycles. This is the complete priority account, not a claim that
  unobserved intervals cannot exist.
- The resource coordinator independently re-hashed the fresh x64-B archive as
  `33dc6f36dfe45a1cced3119bfa1bda967111e421851e3ee69b331f33252790dc`,
  verified smoke report hash
  `b86bd335a9c9a1f88375eda26ad7a9fb3c36d55f70c3fe7316e2037c43d36519`,
  confirmed the preservation marker was unchanged, and closed the round-6
  priority caveat.

## Limits and next action

- No game, Vulkan renderer, physical audio/controller, save/load, firmware, keys, native ARM64 execution, production deployment or release publication was tested.
- Linux packages intentionally retain `unqualified` status until those manual checks exist.
- Review round 1 snapshot
  `cd24a25104ad4786695ad5e2f55b7bce205e47a7fba1548f684da36c81bf2f5e`
  received `NEEDS CHANGES` at 70/100 and 82/100 from
  `ses_e84964324ffe7czVGwdl6JiNIM` and
  `ses_e849642b6ffeyw9E7PqgJntRBj`. All must-fix and should-fix findings are
  addressed; that snapshot is invalid and cannot authorize commit.
- Round-2 snapshot
  `f32969b1b1a9a5af806e535f1e5a258dc3ab35813d4337eadee2a6c58c9f224c`
  received `NEEDS CHANGES` at 91/100 and 90/100 from
  `ses_e84713c9affe5x3glbuFb3S27W` and
  `ses_e84713c58ffeX3WSuCcWjvYbhy`. Both reviewers reproduced that regenerated
  self-inventories could hide dependency-graph drift, altered notices, or an
  incomplete GPL source tree. Reviewer B also found missing explicit
  games/keys/firmware exclusions. That snapshot is invalid.
- Round-3 remediation pins both complete RID-specific dependency-graph bytes
  and runtime targets, every notice digest, and a 211-file corresponding-source
  tree identity in the independently hash-pinned policy. NuGet notices are read
  directly from verified package archives. Prohibited-content and hardlink
  regressions plus a narrow trace-provider exception are now tested.
- Round-3 snapshot
  `77069c23c6ec382cbf45db0004b9a03b76787565832f521eb228613ac479c3d0`
  received `NEEDS CHANGES` at 91/100 and 82/100 from
  `ses_e845e9747ffeHOT5l4v247Ve5o` and
  `ses_e845e9705ffejERXhev08uuuV0`. That snapshot is invalid.
- Round-4 remediation rejects translation payloads and unsafe archive modes,
  pins backend provenance, includes the Concentus notice from its verified
  package, rejects contradictory probe fields, and requires freshly created
  default-data evidence. Python-only verification ran 32 tooling tests,
  Shellcheck, actionlint, source-baseline tests, both architecture verifiers,
  one explicit Xvfb probe and two replacement-separated normal starts.
- Round-4 snapshot
  `ed5d1dd02f4e4c7b0d684a1fdc81193d37858da1a4d78216f58fd12b3c55f739`
  received `NEEDS CHANGES` at 82/100 and 88/100 from
  `ses_e82476074ffeNvYUR507IUsyLQ` and
  `ses_e8247606effei3j1PKs6ibInFS`. That snapshot is invalid.
- Round-5 Python-only verification ran 36 tooling tests, 15 source-baseline
  tests, both architecture verifiers, one explicit Xvfb probe and two
  replacement-separated normal starts. All payloads ran at verified absolute
  nice 10; no .NET build, real GPU, device, adb or network was used.
- Round-5 snapshot
  `6ed4c0ebba9b05c6bff1f291314a124332399585c5fb2e97b0b70ba46269c865`
  received `NEEDS CHANGES` at 82/100 and 86/100 from
  `ses_e822a7b70ffeobk7NpRmnT4L5i` and
  `ses_e822a7a8affelpMa8OpMppX3ps`. That snapshot is invalid.
- Post-round-5 remediation independently pins every package path and ELF
  path/hash per RID, uses the ARM64 FFmpeg commit/configuration actually
  embedded in that binary, records ARM64 SDL source revision as unknown,
  rejects every Unicode control/line-separator path, and covers UNC plus
  case-insensitive DLL CodeView paths.
- The user explicitly authorized one exceptional sixth dual-agent review after
  the five-round cap, with no seventh automated review.
- Exceptional round-6 snapshot manifest
  `1b256b4272ca1c89e7c6766a44af0d55a43627a4eafb10306e922cc44e6663a9`
  covered 40 paths. Reviewer A (`ses_e81ea9d42ffejvJMHii7UZCoWE`) returned
  `NEEDS CHANGES`, 90/100. Reviewer B
  (`ses_e81ea9cdcffevsrr321CTBP3Ak`) returned `NEEDS CHANGES`, 91/100.
- Both reviewers reproduced a fail-open Unicode CodeView path check: printable
  ASCII scanning can split a non-ASCII absolute RSDS producer path and omit it
  from the exact allowlist. Reviewer A also proved that appending bytes after
  the gzip member leaves the archive verifier green. These are must-fix
  integrity-policy defects.
- Should-fix findings were incomplete `libstdc++.so.6`/`libgcc_s.so.1` runtime
  prerequisites, broken relative links in relocated packaged documentation,
  and native-Linux UI actions for unavailable OCR/translation behavior.
- Reviewer A also noted, as a consideration, that `root/./file`, `root//file`
  and trailing-slash archive aliases were accepted.
- The user then removed the automated review-round cap: fix every finding,
  verify, and repeat dual independent review until both reviewers pass.
- Post-round-6 remediation:
  - The archive must be exactly one gzip member with flags 0, an mtime equal to
    the manifest epoch, and no following bytes. Its decompressed tar stream must
    equal the canonical serialization of the verified tree, which rejects data
    in tar headers, padding, or after the end-of-archive marker. Only regular
    file and directory entries are accepted. The tar writer moved to
    `common.write_canonical_tar`, and it reproduces the round-6 x64 stream
    byte-for-byte.
  - CodeView (`RSDS`) and ReadyToRun perf-map (`R2RM`) paths are read from each
    PE debug directory as complete strict UTF-8 strings. Malformed portable
    executables, unknown debug types, non-UTF-8 paths, single-backslash roots,
    and `/_/..` escapes fail. On the real x64 tree the parser reproduces exactly
    the 12 allowlisted paths and rejects the reviewers' `café` mutation of
    `Avalonia.Base.dll`. Two package assemblies with embedded portable PDBs
    (`Humanizer`, `Microsoft.IO.RecyclableMemoryStream`; identical across
    RIDs) are hash-pinned in schema-6 `NATIVE-DEPENDENCIES.json`.
  - Package and verifier system dependencies now declare glibc and the GCC
    runtime libraries. Every ELF `DT_NEEDED` entry must be bundled, declared,
    or the LTTng library needed only by the optional CoreCLR trace provider.
  - Packaged Markdown links are retargeted to the archive layout. Links to
    unpackaged repository files become plain source-repository paths, and the
    verifier rejects any unresolved or escaping relative link outside the
    hash-pinned upstream READMEs in `licenses/native/`.
  - Native Linux builds compile out the translation settings card and dialog
    and the OCR download implementation; the verifier rejects their UI strings
    in the launcher assembly.
  - `common.safe_relative` rejects non-canonical aliases.
- The round-7 window completed and its evidence is recorded above.
- Round-7 snapshot
  `5676fe471660e81338d3266deb0197ede6781da9ae3c24011726f7fff7051bbf`
  (40 paths): Reviewer B (`ses_e81c4e8edffeZM1zz1fVdMYFaf`) returned `PASS`,
  97/100; Reviewer A (`ses_e81c53ffaffevdWhsD0GGdYhOO`) returned
  `NEEDS CHANGES`, 96/100. Both confirmed all six round-6 fixes against
  mutated copies of the real archives. The round fails the dual-pass gate and
  that snapshot is invalid.
- Reviewer A should-fix: the native-Linux avatar dialog still showed the
  online catalog row, Hello Kitty button and catalog caption; the Hello Kitty
  button threw on the empty catalog. Reviewer B should-fix: `docs/testing.md`
  dropped Windows game-window translation/OCR from "still to test".
- Considerations raised by one or both reviewers: unbound gzip XFL/OS bytes;
  CodeView records with bytes after the NUL, padded or non-mapped relative
  paths, or no debug directory; section-header-only `DT_NEEDED` parsing;
  unparsed Markdown link forms; the Linux translation timer; undisclosed
  UTF-16 producer paths in `LibHac.dll`; a test writing outside its temporary
  directory.
- Post-round-7 remediation:
  - Native Linux compiles out the avatar catalog row, tile grid, paging,
    caption, attribution and catalog loader, keeping profiles, local import,
    removal and "Use avatar". It also compiles out translation initialization
    and its timer. The verifier rejects the catalog-only dialog strings.
  - `docs/testing.md` restores Windows translation/OCR to "still to test".
  - The gzip header must also carry XFL 0 and OS 255.
  - Every PE needs a debug directory. CodeView records must end at their NUL,
    perf-map records must be exactly 284 bytes, and all bytes after the NUL
    must be zero. Only `/_/` paths without `..` or backslashes and bare file
    names are exempt from the exact allowlist. Both real trees still produce
    exactly the 12 allowlisted paths.
  - `common.elf_needed` reads `PT_DYNAMIC` through the program headers and
    rejects non-loadable ELF objects; it matches `readelf -d` on all 79 ELF
    files in the round-7 x64 and ARM64 trees.
  - Packaged Markdown may use only `[text](target)`, `[text](target "title")`
    and `[label]: target`; any other `](`, reference definition or HTML
    `href`/`src` fails, and single-letter schemes such as `C:` are not URIs.
  - `specs/security/EXCEPTIONS.md` discloses the UTF-16 caller-file paths in
    pinned `LibHac.dll` (corrected to 127 after round 8).
  - Tests: 43 Linux tooling tests (with new mutations for each item) and 15
    source-baseline tests pass; the Linux suite leaves its scratch directory
    empty.
- The round-8 window completed and its evidence is recorded above.
- Round-8 snapshot
  `73083d32a11881f29c342b570f2af99431fc1b7fb62e204e7c600b66e70f48f1`
  (41 paths) received `PASS` from both reviewers: A
  (`ses_e81acee34ffeIImOMFzp9b0slr`) 96.2/100 and B
  (`ses_e81aca1edffeV4JEQyD3QTRumz`) 96.7/100, no must-fix. Both raised the
  same should-fix: `LibHac.dll` holds 127 distinct caller-file paths, not 122.
- Round-9 changes (Python and documentation only, so the round-8 archives
  still represent the product; no new .NET build was needed):
  - The LibHac count is corrected to 127; a scan of every PE in both round-8
    packages finds that `LibHac.dll` is the only PE with such paths.
  - A Markdown reference definition anywhere in a line, including inside a
    blockquote, list item or indented block, must be one the strict
    start-of-line pattern accepts.
  - `/_//…` debug paths are no longer treated as source-mapped.
  - A gzip CRC or deflate error now fails as `ValueError` instead of an
    uncaught `zlib.error`.
  - `build/linux/README.md` states that the canonical tar stream, not the
    compressed bytes, is the cross-host reproducible identity, because the
    compressed bytes depend on the host's zlib implementation (this host uses
    `1.3.1.zlib-ng`).
  - The `CS0649` publish warning and the full inert avatar/translation residue
    are disclosed; `AvatarDialog.cs` is listed in the changed paths.
  - The round-8 archives pass the updated verifier unchanged (x64 813 files,
    ARM64 811 files); 43 Linux and 15 source-baseline tests pass.
  - Carried, out of Linux scope: `BackendChoice` now maps win-arm64 and throws
    on unsupported operating systems or architectures.
- Round-9 snapshot
  `a4f692c0ef7b38040f011c39ddbc2b9917dbf3c6faf38c3951dfcb5734533911`
  (41 paths): Reviewer A (`ses_e819c46b9ffe4cCY8Ve8aKPQbu`) `PASS` 100/100;
  Reviewer B (`ses_e819bfd2dffeEXZSjWMyUzUPqb`) `NEEDS CHANGES` 95.5/100.
  B's must-fix: `source_bundle.py` gzip-compressed each FFmpeg source tar with
  the host zlib at level 9 and pinned the compressed SHA-256. Recompressing
  the shipped bundles with classic zlib 1.3 (Ubuntu 24.04) and 1.3.1
  (Freedesktop 25.08) gave different hashes, so packaging on the
  `ubuntu-24.04` CI runner would abort, and the round-9 README sentence about
  other hosts was false. Considerations from both reviewers: an escaped `]`
  in a Markdown reference label and non-`href`/`src` HTML attributes bypassed
  the link check; `/_/home/…`, `/_/./…` and `/_/C:/…` debug paths counted as
  source-mapped; `IslandSource` was missing from the residue list; the
  `CS0649` wording named only one reader.
- Round-10 changes:
  - Each FFmpeg source bundle is now the uncompressed `git archive` stream,
    `ffmpeg-39cac587c4-source.tar` (SHA-256
    `7cdc5c54068202f40f4bdcc344f4912db3e4defc95238d7dd9c30926c17dd9d4`) and
    `ffmpeg-e16ff06adb-source.tar` (SHA-256
    `42cad98c27584ad67d4517d996f4b561e1ed2a0ff9f44e70d2e5be855add9e66`).
    Each equals the decompressed round-8 bundle byte for byte, and running
    `add_ffmpeg_source` for both RIDs with a classic zlib 1.3.1 loaded
    reproduces both pins. The policy's source records and package-inventory
    hashes are updated (pin
    `6a1b469b397265b859701f686af66e70c0a33f1e3fcb3bc82d109602e882128c`);
    the new inventory hashes were computed by a method that first reproduced
    the old pins. A new test proves the bundle is the raw `git archive` stream.
  - No packaged file is compressed by the build any more; only the outer gzip
    bytes depend on the host zlib, as `build/linux/README.md` now states.
  - Every `]:` in a packaged Markdown file must belong to a strict reference
    definition, and any HTML tag with attributes fails (the packaged docs use
    only attribute-free `<details>`/`<summary>`).
  - A `/_/` debug path is exempt only under the observed mapped roots `src`,
    `packages`, `binding` and `artifacts`, with no empty, `.` or `..`
    component, colon or backslash; the real round-8 CodeView set still passes.
  - `IslandSource` is disclosed, and the `CS0649` note names both readers.
  - `docs/linux.md` describes what the Checks workflow does. No CI run of this
    packaging exists yet; fork CI remains a required gate after commit.
  - Residual assumption: `git archive` tar output is identical across the
    host's git 2.56.0 and the CI runner's git; fork CI will confirm it.
  - 44 Linux and 15 source-baseline tests pass.
- The round-10 window completed; its evidence is recorded above. Under
  classic zlib the packager now completes and produces the same tar stream as
  under zlib-ng.
- Round-10 snapshot
  `24051ca6d6971be19d7393aa6ece153d2fe8fa8cb83cc7f201b5f49f0e2ea3b2`
  (41 paths): Reviewer A (`ses_e8188bd84ffeEw2nkBLHErk2h5`) `PASS` 96/100;
  Reviewer B (`ses_e81885223ffeeY0d2mzX2Cu5iY`) `PASS` 96.7/100; no must-fix.
  Shared should-fix: `build/linux/README.md` and the audit said the source-tar
  hash depends only on the commit, but `git archive` read host settings
  (`core.autocrlf=true` gave `c6412578…`, `tar.umask=0022` gave `7020b9aa…`).
  Shared consideration: `<img/src=…>` passed the HTML link check.
- Round-11 changes (Python and non-packaged docs only; `NATIVE-DEPENDENCIES.json`
  and every packaged file are unchanged, so the round-10 archives remain the
  product):
  - `source_bundle.py` runs git with `GIT_CONFIG_GLOBAL=/dev/null`,
    `GIT_CONFIG_NOSYSTEM=1` and `GIT_ATTR_NOSYSTEM=1`, drops inherited
    `GIT_CONFIG_PARAMETERS`/`GIT_CONFIG_COUNT`, pins `core.autocrlf=false`,
    `core.attributesFile=/dev/null` and `tar.umask=0002`, and refuses a
    non-empty or non-regular `info/attributes`. Both real pins reproduce under
    a global config setting `core.autocrlf=true` and `tar.umask=0022`.
  - The HTML link check treats `/` like whitespace after a tag name.
  - The README and audit name git's tar writer as the remaining unconfirmed
    input; `docs/testing.md` says archive bytes match on the same host.
  - The stale `ffmpeg-source.tar.gz` test fixture name is now `.tar`.
  - The round-10 archives pass the updated verifier; 44 Linux and 15
    source-baseline tests pass.
  - Deferred: `docs/linux.md` line width (it ships as `README-LINUX.md`, so
    editing it would change the archives).
- Round-11 snapshot
  `941d75a1bd8bf0b69f12ed1aa145c9ce79a97546bbdd6e68e5f57dc41ed503d2`
  (41 paths): Reviewer A (`ses_e817e63a4ffeHr8583XtCQ2PYE`) `PASS` 95.5/100;
  Reviewer B (`ses_e817e0f0dffeA8odzGDwIrVo32`) `PASS` 100/100; no must-fix.
  A's should-fix: a repository-local `core.bigFileThreshold=1` (x64 tar
  `f53b127a…`) and replace refs still changed the source tar, so the docs
  overclaimed. Considerations: the test did not exercise the environment
  isolation; `[x](missing.md:frag)` passed as a scheme; CommonMark backslash
  escapes and entities in destinations were not rejected; wording nits.
  Suggestions: lazy fetch, inherited `GIT_DIR`, `safe.directory`, test hygiene.
- Round-12 changes (Python and non-packaged docs only; the round-10 archives
  remain the product):
  - `source_bundle.py` drops every inherited `GIT_*` variable, sets
    `GIT_NO_REPLACE_OBJECTS=1` and `GIT_NO_LAZY_FETCH=1`, also pins
    `core.eol=lf` and `core.bigFileThreshold=512m`, and refuses a symlinked
    `info/attributes` even when it dangles.
  - `common.EXTERNAL_LINK` (`http`, `https`, `mailto`) replaces the generic
    scheme pattern in both `verify.py` and `distribution.py`; the verifier
    rejects link destinations containing `\` or `&`.
  - The FFmpeg test uses an isolated fixture environment and
    `unittest.mock.patch.dict`, shows each hostile input changes or breaks raw
    `git archive`, and requires the bundle to be unaffected by all of them.
    A scratch mutation run removing each of the 11 protections one at a time
    made a test fail every time; the unmutated tests pass.
  - Both real pins reproduce from fresh borrowing repositories with
    repository-local `autocrlf`/`eol`/`bigFileThreshold`/`umask`, a replace
    ref on `RELEASE`, and hostile `HOME`, XDG attributes, `GIT_CONFIG_*` and
    `GIT_DIR` (raw hostile archives differ: x64 `e92e9807…`, ARM64
    `6f31d058…`).
  - All 231 regenerated distribution files match both round-10 archives; the
    x64-a, x64-classic and ARM64 archives pass the updated verifier.
  - README documents that a global `safe.directory` no longer applies.
- Round-12 snapshot
  `f3be038eef19815b350f8a4bc4af6e582a1ba5110e34f19701904501cacc26bb`
  (41 paths): Reviewer A (`ses_e816c5675ffe53XlW0j5QjM8ot`) `PASS` 96.2/100;
  Reviewer B (`ses_e816bdc37ffenoT7u9HlLyF8RI`) `PASS` 95.8/100; no must-fix.
  Shared should-fix: the audit said removing any single protection failed a
  test, but removing `GIT_CONFIG_NOSYSTEM`, `GIT_ATTR_NOSYSTEM` or
  `GIT_NO_LAZY_FETCH` did not; B also showed the global-attributes control
  could not fail. Considerations: autolinks, `https:missing.md`, trailing `/`
  on file links, fixture reading system attributes, a 94-column audit line.
- Round-13 changes (Python and non-packaged docs only; the round-10 archives
  remain the product):
  - The FFmpeg test is split into four: raw-git positive controls, each from a
    clean repository; the bundle under all hostile inputs; `info/attributes`
    refusals (table of cases, now including a symlink to an empty file); and
    a blob-less clone of a local promisor that the bundle must refuse to
    fetch from. The fixture sets `GIT_ATTR_NOSYSTEM=1`; `git_status` is now
    `git_returncode`.
  - Replace refs are disabled with `git --no-replace-objects` instead of
    `GIT_NO_REPLACE_OBJECTS`.
  - `EXTERNAL_LINK` requires `http://`, `https://` or `mailto:`; autolinks
    must match it; a link ending in `/` or `.` must resolve to a directory.
  - Scratch mutation run: all 17 testable protections are caught;
    `GIT_CONFIG_NOSYSTEM` and `GIT_ATTR_NOSYSTEM` survive (need root).
  - Both real pins reproduce; all 231 regenerated distribution files match
    the x64-a, x64-classic and ARM64 round-10 archives, which pass the
    stricter verifier. 47 Linux and 15 source-baseline tests pass.
  - README scopes "depends only on the commit" to the pinned commits, whose
    `.gitattributes` use no `filter`, `ident` or `export-subst`.
- Round-13 snapshot
  `0c0dc168b71b1726483cacd7e6ee749f55c5bd9cd748c8886e4b32a533a6077b`
  (41 paths): Reviewer A (`ses_e815d827effeiLb1aVIO5nbMG4`) `PASS` 97.1/100;
  Reviewer B (`ses_e815d2571ffeyr1SZ2YPwt0jEQ`) `PASS` 100/100; no must-fix.
  A's should-fix: the archive and configuration identity checks, the
  pinned-revision check, the symlinked-repository refusal, the
  existing-destination guard and the link root-containment check were
  untested, while the audit implied full coverage. Considerations (both):
  `--no-replace-objects` only shown on git 2.56; `GIT_NO_LAZY_FETCH` needs
  git 2.44; `GIT_CONFIG_NOSYSTEM` is testable without root; unclosed pipe and
  bad tar left on disk; order-dependent attributes table; lazy-fetch test did
  not assert the failing command; uppercase autolink and `LICENSE/x/..` not
  covered; the "17 protections" count included a test-side check.
- The round-13 "17 testable protections" and "needs root" statements above
  overstated the evidence; the round-14 entry below replaces them.
- Round-14 changes (Python and non-packaged docs only; the round-10 archives
  remain the product):
  - `source_bundle.py` also pins `core.useReplaceRefs=false`, refuses git
    older than 2.44, writes the archive through `with subprocess.Popen` and
    deletes it on any failure, deletes a tar or build configuration that
    fails its identity check, and drops the redundant destination-exists
    guard (`open("xb")` refuses an existing file or dangling symlink).
  - New tests: every fail-closed guard in `add_ffmpeg_source`, an existing
    destination, git 2.43 refused and a malformed `GIT_CONFIG_SYSTEM`
    ignored (both through a `git` wrapper on `PATH`). The attributes cases
    clean up after themselves; the lazy-fetch test asserts `git archive`
    failed; the link test adds `[up](../)`, `licenses/LICENSE/x/..` and
    `<FILE:///etc/passwd>`.
  - Scratch mutation run over the whole Linux suite: all 34 production
    mutations and the test-control mutation are caught. Removing either
    replace-ref protection alone survives (they back each other up on git
    2.56); removing both is caught. `GIT_ATTR_NOSYSTEM` survives: it needs
    root or a mount namespace to test.
  - Both real pins reproduce; all 231 regenerated distribution files match
    the x64-a, x64-classic and ARM64 round-10 archives, which pass the
    verifier. 49 Linux and 15 source-baseline tests pass.
  - Residual: CI (`ubuntu-24.04`) must provide git 2.44 or later; older git
    now stops packaging with a clear error.
- Round-14 snapshot
  `ae83e16e28c7060fb8fc393658e2fbc4d1ecc5ec54bee68070118896c5e3ccab`
  (41 paths): Reviewer A (`ses_e814da590ffemNWEXVJR1BkBaT`) `PASS` 100/100;
  Reviewer B (`ses_e814d7f15ffe8KneZM4jE5dkku`) `PASS` 100/100; no must-fix
  or should-fix. Considerations: the 2.44 minimum was unverified; the
  minimum-version edge and unparseable `git --version` output were untested;
  two tests covered two behaviours each; the dangling-symlink case did not
  check the symlink survived; the configuration write was not exclusive;
  duplicated identity checks; git missing from the README inputs.
- Git's source shows the round-14 "2.44" minimum above was wrong: 2.44.0
  does not honour `GIT_NO_LAZY_FETCH`, and `--no-lazy-fetch` first appears in
  2.45.0. (Corrected in round 16: the variable alone is honoured in
  `promisor-remote.c` from the 2.39.4, 2.40.2, 2.41.1, 2.42.2, 2.43.4 and
  2.44.1 maintenance releases.)
- Round-15 changes (Python and non-packaged docs plus one CI line; the
  round-10 archives remain the product):
  - `source_bundle.py` refuses git older than 2.45, adds `--no-lazy-fetch`,
    checks both identities through one helper, and opens the build
    configuration exclusively.
  - Tests split: system configuration; git version (2.44.9 and unparseable
    refused, 2.45.0 and 3.0.0 accepted); existing tar or configuration file
    and dangling symlinks at both paths, which must survive.
  - README lists git 2.45 as an input and cites the source; CI runs
    `git --version` before the tool tests.
  - Scratch mutation run over the whole Linux suite: all 39 production
    mutations and the test-control mutation are caught; each replace-ref and
    lazy-fetch pair member alone survives (redundant pairs; each pair removed
    together is caught); `GIT_ATTR_NOSYSTEM` survives.
  - Both real pins reproduce; all 231 regenerated distribution files match
    the round-10 archives, which pass the verifier. 51 Linux and 15
    source-baseline tests pass.
  - Residual: CI must provide git 2.45 or later; the new `git --version` step
    records it.
- Round-15 snapshot
  `9a5b267740a262cc9cfed3bf82e1eee7a4baaa48b1a8bda018ec183d70463bb8`
  (41 paths): Reviewer A (`ses_e81402904ffemqvCXPkUbAkQ7P`) `PASS` 100/100;
  Reviewer B (`ses_e814006d0ffeFplbx5ku4n8DIo`) `PASS` 95.7/100; no must-fix.
  B's should-fix: README, audit and handoff said no release before 2.45 has
  `GIT_NO_LAZY_FETCH`, but `promisor-remote.c` honours it from 2.39.4,
  2.40.2, 2.41.1, 2.42.2, 2.43.4 and 2.44.1 (not 2.44.0). Considerations:
  assertions outside `subTest` in two loop tests; existing-destination test
  did not assert files left behind; version test derived output from its
  key; CI step name stale; A also noted v2.43.7 and v2.44.4 lack the option.
- Round-16 changes (non-packaged docs, tests and one CI step; the round-10
  archives remain the product; `source_bundle.py` unchanged):
  - README, audit and the handoff line above state the corrected history:
    only `--no-lazy-fetch` needs 2.45.0, which sets the minimum.
  - Version test keyed by exact `git --version` output; every assertion in it
    and in the existing-destination test runs inside its case; the latter
    asserts the regular files left for existing and dangling tar and
    configuration paths.
  - CI step renamed "Test build tools"; it refuses git older than 2.45 with
    `sort --version-sort --check=quiet` before running the tests (gate logic
    checked locally against 2.56.0, 2.45.0, 2.45.1.windows.1, 2.44.9, 2.43.0,
    3.0.0 and non-version output).
  - The round-15 mutation harness (`500ee420…`) re-run on this tree: all 39
    production mutations caught; only the redundant pairs and
    `GIT_ATTR_NOSYSTEM` survive. 51 Linux (also under `-X dev`) and 15
    source-baseline tests pass; the round-10 archives verify and all 231
    regenerated distribution files match them.
- Round-16 snapshot
  `eea22e3af5ecaf3f60f1bd249160f424f84ffd64d3a33ee5e4bf31aee94fdaec`
  (41 paths): Reviewer A (`ses_e81346bfaffeqg0bZxjAvblUsx`) `PASS` 100/100;
  Reviewer B (`ses_e81345062ffe9sV4aiiR4XjFLk`) `PASS` 100/100; no must-fix
  or should-fix. Both confirmed the corrected history from git's source.
  Considerations: `require_supported_git` docstring still paired the
  variable with the 2.45 minimum; README "earlier releases of each series"
  ambiguous for series before 2.39; CI gate failed silently; audit round-13
  quote of the 2.44 claim not marked as corrected.
- Round-17 changes (docstring, non-packaged docs and one CI line; the
  round-10 archives remain the product):
  - Docstring ties the 2.45 minimum to `--no-lazy-fetch` only.
  - README: "releases before those, including 2.44.0, do not"; paragraph
    reflowed.
  - CI gate prints `::error::git 2.45 or later is required` on failure.
  - Audit marks the round-13 quote "(corrected in the round-15 section)".
- Next action: freeze this snapshot for round-17 dual independent review.
  Commit and push only after both reviewers pass the exact frozen tree.
