---
type: code-audit
context: native-linux-pr2
---

# PR 2 code audit

Verdict: self-audit PASS for the round-23 changes. Commit and push stay
blocked until both independent reviewers pass the new frozen snapshot.

## Supply chain and security

- ✓ No new unpinned package version. Linux-native references use the already
  reconstructed graph's exact Concentus, SkiaSharp, HarfBuzzSharp, SDL3,
  FFmpeg and Shaderc versions.
- ✓ Package/license provenance is recorded in
  `build/linux/NATIVE-DEPENDENCIES.json`; exact NuGet and per-RID native hashes,
  a graph-complete managed-package notice tree, pinned Shaderc dependency
  notices, the .NET license/third-party notices, FFmpeg corresponding source,
  and GPL timing source are verifier-required archive files.
- ✓ No credential, token, private-key or environment-value material appears in
  the diff.
- ✓ Archive extraction rejects traversal, links, special files, unsafe raw
  modes, oversized/member-heavy archives, duplicate and case/Unicode-colliding
  names, Windows-reserved names, bidi controls and non-normalized/control
  characters, and non-canonical aliases. The archive must be one flag-free gzip
  member with no trailing bytes, and its tar stream must equal the canonical
  serialization of the verified tree. Direct malicious-tar and gzip tests cover
  these branches.
- ✓ Manifests use duplicate-key rejection and exact file-set/hash checks. The
  verifier independently pins the source baseline, system dependencies,
  dependency-policy hash, backend identity/source URL, native locations/hashes,
  every package path and ELF identity per RID, both `.deps.json` graphs, every
  package notice, the per-RID corresponding-source records, all known
  third-party producer CodeView paths, and both embedded-portable-PDB
  assemblies by assembly hash. CodeView and perf-map paths are parsed from the
  PE debug directory as strict UTF-8; unknown debug types fail.
- ✓ Every ELF `DT_NEEDED` entry is bundled or declared in the package's system
  dependencies.
- ✓ The desktop installer rejects control characters and escapes `%`,
  backslash, quotes, backticks and dollar signs where the Desktop Entry format
  requires them.
- ✓ Native Linux compilation removes mutable avatar/OCR downloads and the
  translation settings. The archive scan rejects their ASCII and UTF-16 markers
  and any bundled translation runtime.
- ✓ Final launcher and core dependency manifests require .NET runtime pack
  10.0.13 and reject the known vulnerable v1 standalone package identities.
- N/A `slopcheck` is not installed in this repository/environment. No new
  package family was introduced; package identities, native assets, license
  metadata and final dependency manifests were inspected directly.

## Correctness, scope and tests

- ✓ Scope is limited to native Linux x64/ARM64 publishing, CI, security
  boundaries, docs, notices and bookkeeping. Android and package formats other
  than `.tar.gz` remain untouched.
- ✓ New Python functions are typed. No suppression, unsafe cast, shell
  interpolation, `eval`, or `shell=True` was introduced.
- ✓ Forty-four focused tests cover unsafe/prohibited/non-canonical paths, raw
  archive modes and hardlinks, gzip trailing data/extra members/header
  fields/truncation and data after the tar end marker, JSON duplicates/types,
  ELF format/architecture and `DT_NEEDED`, exact
  package/native/backend/graph/notice/source policy, dependency blockers,
  publish/checksum symlinks, structural PE CodeView/perf-map/embedded-PDB
  records including non-ASCII paths, packaged document links, unsupported
  Linux UI markers, durable fresh default-data evidence, desktop paths and
  exact no-game smoke-report schemas.
- ✓ Existing solution build is warning/error-free; contract and service suites
  pass with only the documented Windows-only unavailable integration.
- ✓ Two clean x64 builds from different absolute restored-source/work roots on
  the same host produce identical product payloads and byte-identical 813-file
  archives after final assembly. The 811-file ARM64 package passes one clean
  static build. The x64 package additionally passes dynamic dependency and Xvfb
  launcher/worker checks.
- ✓ Normal launcher startup before and after replacing the extracted
  application directory writes to the production default XDG data root,
  preserves an external marker byte-for-byte, and emits strict reports binding
  fresh timestamped evidence and the packaged launcher hash.
- ✓ Round-10 archives, reassembled from the round-8 app publishes of this
  tree's C# and the unchanged round-2 core publishes, pass the hardened static
  verifier: x64 contains 813 files and 40 x86-64 ELF files at SHA-256
  `839efa187f8173c1f067529ed807f36d67b38a8514d45b6547fadb22d36c710b`;
  ARM64 contains 811 files and 39 AArch64 ELF files at SHA-256
  `dc655a050549156c4bcafee88f54e07a77fe0f311883a2198530bdf744266d5c`.
  x64-A and x64-B, from app publishes made under different absolute roots, are
  byte-identical. The packaged launcher contains no catalog-only avatar
  strings.
- ✓ An x64 archive assembled and verified under classic zlib 1.3.1 completes
  without a source-identity mismatch. Its compressed bytes differ from the
  zlib-ng archive, but its tar stream is identical
  (`94118539b9e36d9f9fa274fb8be3406a5feafdb65ec9f5029ad25ee2b66df65a`),
  so the round-9 classic-zlib blocker is resolved.
- ✓ The round-10 Xvfb probe and both default-data starts passed (reports
  `3d223f83…eed1a4`, `fae41785…f971`, `5347eccd…f013`), and marker SHA-256
  `0762b626650be8ec77af17481ca4a83caa452cf7086c1933ea832d677142f0f1`
  was unchanged across the install replacement. The per-thread watcher
  recorded three re-pinned exposures, listed in `.agent/HANDOFF.md`.
- ✓ The code is split into focused packaging, distribution, common validation,
  dependency-policy verification, archive verification, and explicit/default
  smoke modules.
- ✓ No Fowler Mysterious Name, Feature Envy, Data Clumps, Primitive Obsession,
  Message Chains or Middle Man smell was found in new code. Repeated manifest
  identity values are intentional independent trust assertions, not shared
  mutable configuration.

## Repository conventions and limits

- The repository has no `CONVENTIONS.md`, churn script, or slopcheck command.
  Existing root documentation locations were preserved.
- Generic size guidance is exceeded by the packaging orchestrator, archive and
  dependency verifiers, package-notice generator, smoke verifier, and regression
  suite. These modules group one build transaction or one fail-closed policy;
  splitting their trust constants from validation would weaken reviewability.
- Reconstructed `MainWindow.cs` remains a pre-existing long source-recovery
  artifact; this PR adds only compile-time Linux guards.
- No GPU, game, firmware, key, physical audio/controller, save/load, or ARM64
  device execution was performed. Packages therefore remain `unqualified`.

## Red-flag review

No checklist item was silently skipped. The unavailable generic audit tools and
the justified size deviations are stated above. Round-1 reviewers found
clean-build nondeterminism, a missing CI environment input, incomplete
notice/provenance enforcement, symlink dereferencing and weak negative tests.
Round-2 reviewers showed that regenerated self-inventories could still hide
complete dependency-graph drift, altered notice bytes, incomplete GPL source,
and prohibited user content. Round-3 reviewers found missing translation and
Concentus policy, mutable backend provenance, unsafe raw archive modes,
contradictory probe fields and stale default-data evidence. Round-4 reviewers
found incomplete graph-wide/Shaderc notices, missing FFmpeg corresponding
source, additive report schemas, non-durable default-data evidence, and
unreviewed producer CodeView paths. Round-5 reviewers found incomplete package
path/native-payload coverage, ARM64 FFmpeg source/configuration incorrectly
reused from x64, unsupported ARM64 SDL source attribution, UNC CodeView and
Unicode-control path gaps. Rounds 6 to 8 are recorded in their own sections
below. Each finding now has a code change and direct regression or integration
evidence, or an explicit disclosure; no prior snapshot or verdict is reused.

The initial round-6 resource record is not reused for x64-B or the first
default-data validator: the coordinator observed both at nice -4 after their
one-time assertions. RUN LOG 133 repeated exactly those functional steps with a
10 ms live descendant re-pin/watch. The x64-B payload and its observed Git child
had no detected reset across 581 cycles; the watcher itself had one explicitly
recorded nice -4 reset and immediate correction. The default-data process tree
had no detected reset across 332 cycles. These are sampled observations, not an
unqualified claim of continuous scheduler state.

The resource coordinator independently re-hashed fresh x64-B as
`33dc6f36dfe45a1cced3119bfa1bda967111e421851e3ee69b331f33252790dc`,
verified smoke report
`b86bd335a9c9a1f88375eda26ad7a9fb3c36d55f70c3fe7316e2037c43d36519`,
and confirmed the preservation marker remained unchanged. The round-6
resource-accounting caveat is closed.

## Exceptional round-6 review

Snapshot
`1b256b4272ca1c89e7c6766a44af0d55a43627a4eafb10306e922cc44e6663a9`
received `NEEDS CHANGES` at 90/100 and 91/100 from two independent reviewers.
Both reproduced a Unicode CodeView-policy bypass: scanning printable ASCII runs
can split a non-ASCII absolute RSDS producer path and omit it from the exact
allowlist. One reviewer additionally proved that bytes appended after the gzip
member are outside all manifests yet accepted by the archive verifier.

Should-fix findings cover missing `libstdc++.so.6` and `libgcc_s.so.1`
prerequisites, broken relative links in relocated package documentation, and
native-Linux UI actions for unavailable OCR/translation behavior. One reviewer
also suggested rejecting non-canonical archive path aliases.

The user removed the automated review-round cap and asked for every finding to
be fixed, verified and dual-reviewed until both reviewers pass. Each finding
above, including the consideration, now has a code change and a direct
regression test. The real round-6 artifacts confirm the trailing-bytes and
Unicode CodeView repros are now rejected, while the canonical tar writer
reproduces the existing x64 stream exactly.

## Round-7 review

Snapshot
`5676fe471660e81338d3266deb0197ede6781da9ae3c24011726f7fff7051bbf`
received `PASS` at 97/100 and `NEEDS CHANGES` at 96/100. Both reviewers
confirmed every round-6 fix with mutated copies of the real archives. The
blocking should-fix was the native-Linux avatar dialog still offering the
online catalog row, Hello Kitty button and catalog caption. The other
should-fix restored Windows translation/OCR to the test notes.

Every round-7 consideration is also addressed: gzip XFL/OS bytes are bound;
CodeView and perf-map records may not carry bytes after their path, every PE
needs a debug directory, and only `/_/` or bare-file-name paths avoid the exact
allowlist; `DT_NEEDED` is read through `PT_DYNAMIC`; unsupported Markdown link
forms fail; the Linux translation timer is compiled out; `LibHac.dll` caller
paths are disclosed; and the test suite no longer writes outside its temporary
directories. On the round-7 trees, the new rules accept the real CodeView set
and match `readelf -d` on all 79 ELF files, and the full verifier rejects the
round-7 launcher for the catalog strings the C# change removes.

## Round-8 review

Snapshot
`73083d32a11881f29c342b570f2af99431fc1b7fb62e204e7c600b66e70f48f1`
received `PASS` from both reviewers, at 96.2/100 and 96.7/100, with no must-fix
finding. Both confirmed every round-7 fix with mutated copies of the round-8
archives. Their shared should-fix was the LibHac disclosure: the pinned
`LibHac.dll` holds 127 distinct UTF-16 caller-file paths, not 122.

The round-9 tree corrects that count and addresses the considerations without
changing C#, so the round-8 archives remain the product artifacts:

- Markdown reference definitions inside blockquotes, list items or indented
  blocks now fail unless the strict start-of-line pattern accepts them.
- `/_//…` debug paths are no longer exempt from the exact allowlist.
- Corrupt deflate data or a bad gzip CRC raises `ValueError`.
- The build README states that archive bytes depend on the host's zlib, so the
  canonical tar stream is the cross-host reproducible identity.
- The `CS0649` warning from native publishes and the full inert
  avatar/translation residue are disclosed in `.agent/HANDOFF.md` and
  `specs/security/EXCEPTIONS.md`.
- `BackendChoice` now maps win-arm64 and throws on unsupported platforms; this
  is outside the Linux scope and is recorded rather than changed.

The updated verifier accepts both round-8 archives unchanged, and new
regression cases cover each verifier change.

## Round-9 review

Snapshot
`a4f692c0ef7b38040f011c39ddbc2b9917dbf3c6faf38c3951dfcb5734533911`
received `PASS` at 100/100 and `NEEDS CHANGES` at 95.5/100. The must-fix,
missed by every earlier round, was that the FFmpeg corresponding-source bundles
were compressed with the host's zlib-ng and pinned by compressed hash, so
packaging on a classic-zlib host such as the CI runner could not reproduce
them. The bundles are now the uncompressed `git archive` tar streams, whose
hashes no longer depend on the host's zlib; a classic zlib 1.3.1 run
reproduces both pins. The considerations (escaped-label reference definitions,
HTML attributes other than `href`/`src`, `/_/` paths outside the mapped roots,
the `IslandSource` residue and the `CS0649` wording) are each addressed, with
regression cases for the verifier changes. The round-10 reassembly, including
one x64 archive built under classic zlib 1.3.1, confirms the fix: both x64
builds carry the same tar stream and pass the verifier.

## Round-10 review

Snapshot
`24051ca6d6971be19d7393aa6ece153d2fe8fa8cb83cc7f201b5f49f0e2ea3b2`
received `PASS` from both reviewers, at 96/100 and 96.7/100, with no must-fix
finding. Both reproduced the round-9 fix under classic zlib. Their shared
should-fix was that the docs said the source-tar hash depends only on the
commit, while `git archive` also read host settings: `core.autocrlf=true` or
`tar.umask=0022` changed the bytes. Both also found that a slash before an
HTML attribute (`<img/src=…>`) passed the link check.

The round-11 tree runs `git archive` with user and system git configuration
and system attributes ignored, pins `core.autocrlf`, `core.attributesFile` and
`tar.umask`, and refuses a repository with a local `info/attributes` file. Both
real pins still reproduce under a hostile global config, so the round-10
archives remain the product. The HTML check now treats `/` like whitespace
after a tag name. The README and this record name git's tar writer as the
remaining unconfirmed input. A new test runs the bundle against a repository
with hostile local settings and a hostile `GIT_CONFIG_PARAMETERS`, and checks
that `info/attributes` is refused; two slash-attribute cases join the link
test. Not changed: `docs/linux.md` line width, because it ships as
`README-LINUX.md` and editing it would require a new reassembly.

## Round-11 review

Snapshot
`941d75a1bd8bf0b69f12ed1aa145c9ce79a97546bbdd6e68e5f57dc41ed503d2`
received `PASS` from both reviewers, at 95.5/100 and 100/100, with no must-fix
finding. The one should-fix was that the source tar still had repository-local
inputs: a local `core.bigFileThreshold=1` streams large blobs without the
in-tree `eol=crlf` conversion, and a replace ref substitutes a blob. Both fail
closed with an identity mismatch, but the docs named git's tar writer as the
only remaining input. The considerations were that the test did not exercise
the environment isolation, that the link check skipped any `scheme:` target
(so `[x](missing.md:frag)` passed), and that it ignored the backslash escapes
and entities CommonMark resolves in destinations.

The round-12 tree runs git with every inherited `GIT_*` variable removed,
replace refs and lazy fetches off, and `core.eol` and `core.bigFileThreshold`
pinned as well; a symlinked `info/attributes` is now refused even when
dangling. The link checks in the verifier and the packager share one pattern
that only treats `http`, `https` and `mailto` as external, and the verifier
rejects any destination containing `\` or `&`. The FFmpeg test was meant to
show that each hostile input changes or breaks raw `git archive` and that the
bundle is unaffected by all of them at once; round 12 found that this record
overstated what it proved (see below). Both real pins reproduce from fresh
repositories carrying every hostile setting and a replace ref. No packaged file
changed: all 231 regenerated distribution files match both round-10 archives,
which pass the updated verifier.

## Round-12 review

Snapshot
`f3be038eef19815b350f8a4bc4af6e582a1ba5110e34f19701904501cacc26bb`
received `PASS` from both reviewers, at 96.2/100 and 95.8/100, with no must-fix
finding. Both raised the same should-fix: this record said removing any single
git protection made a test fail, but removing `GIT_CONFIG_NOSYSTEM`,
`GIT_ATTR_NOSYSTEM` or `GIT_NO_LAZY_FETCH` left every test passing. One
reviewer also showed that the global-attributes positive control could not
fail, because it ran after the hostile repository settings were already
applied. The considerations were that CommonMark autolinks (`<file:///…>`,
`<missing.md:x>`) and scheme-only targets (`https:missing.md`) bypassed the
link check, that a trailing `/` on a file link passed, and that the test
fixture still read system git attributes.

The round-13 tree splits the FFmpeg test into four tests. Each positive
control now starts from a clean repository, so none can pass vacuously. A new
test clones a local promisor repository without blobs and requires the bundle
to fail rather than fetch them, while plain `git archive` fetches and
succeeds. Replace refs are disabled with git's `--no-replace-objects` option,
which outranks repository configuration on every git version. External links
must now be `http://`, `https://` or `mailto:`; autolinks must be one of these;
and a link ending in `/` or `.` must resolve to a directory. The test fixture
ignores system attributes. Removing any of the 17 testable protections makes
one of the four FFmpeg tests or the link test fail. `GIT_CONFIG_NOSYSTEM` and
`GIT_ATTR_NOSYSTEM` remain untested, because exercising them needs root to
write `/etc/gitconfig` or `/etc/gitattributes`. Both real pins still
reproduce, and no packaged file changed: all 231 regenerated distribution
files match the round-10 archives, which pass the stricter verifier. Round 13
found that the "every git version", "17 testable protections" and "needs
root" claims in this paragraph overstated the evidence (see below).

## Round-13 review

Snapshot
`0c0dc168b71b1726483cacd7e6ee749f55c5bd9cd748c8886e4b32a533a6077b`
received `PASS` at 97.1/100 and 100/100, with no must-fix finding. The
should-fix was that several fail-closed guards had no test: deleting the
archive or build-configuration identity check, the pinned-revision check,
the symlinked-repository refusal, the existing-destination guard or the
verifier's archive-root containment check left every test passing, while
this record implied complete coverage. The considerations were:

- `--no-replace-objects` was only shown to outrank repository configuration
  on git 2.56;
- `GIT_CONFIG_NOSYSTEM` can be tested without root by pointing
  `GIT_CONFIG_SYSTEM` at a malformed file;
- `GIT_NO_LAZY_FETCH` only exists from git 2.44 (corrected in the round-15
  section below);
- a tar that failed its identity check stayed on disk, and the git pipe was
  never closed;
- the refused-attributes cases depended on their order;
- the lazy-fetch test did not confirm that `git archive` was the failing
  command;
- an uppercase autolink scheme and a `..` that ends a file path were not
  covered;
- the mutation count included one test-side check.

The round-14 tree also pins `core.useReplaceRefs=false` on the command line,
which outranks repository configuration on any version. It refuses git older
than 2.44, closes the git pipe with a context manager, and deletes a tar or
build configuration that fails its identity check. The redundant
existing-destination guard is removed: the exclusive `open("xb")` already
refuses an existing file or a dangling symlink, and a test now covers that.
New tests cover each identity check, an abbreviated revision, a symlinked or
non-directory repository, an unsupported RID, an existing destination, git
older than 2.44 and a malformed system configuration. The refused-attributes
cases clean up after themselves, the lazy-fetch test asserts that
`git archive` failed, and the link test adds `[up](../)`,
`licenses/LICENSE/x/..` and `<FILE:///etc/passwd>`.

A scratch mutation run against the whole Linux suite removed each of 34
production protections one at a time, and a test failed every time. A
test-side mutation that disables the global-attributes positive control is
also caught. `--no-replace-objects` and `core.useReplaceRefs=false` back each
other up on git 2.56, so removing either alone is not caught; removing both
is. `GIT_ATTR_NOSYSTEM` is still untested: git has no variable that names the
system attributes file, so testing it needs root or a private mount
namespace. Both real pins still reproduce, and no packaged file changed: all
231 regenerated distribution files match the round-10 archives, which pass
the verifier.

## Round-14 review

Snapshot
`ae83e16e28c7060fb8fc393658e2fbc4d1ecc5ec54bee68070118896c5e3ccab`
received `PASS` from both reviewers at 100/100, with no must-fix or
should-fix finding. Both questioned the git 2.44 minimum, which neither could
confirm offline. Git's source shows that 2.44.0 does not honour
`GIT_NO_LAZY_FETCH`, so the round-14 gate would have accepted a git that
ignores it. The variable is honoured in `promisor-remote.c` from the 2.39.4,
2.40.2, 2.41.1, 2.42.2, 2.43.4 and 2.44.1 maintenance releases; the
`--no-lazy-fetch` option first appears in `git.c` at tag `v2.45.0`. (The
round-15 version of this paragraph said the variable was also absent from
those maintenance releases; that was wrong.) The other considerations were
that no test accepted the minimum version or refused unparseable
`git --version` output, that two tests
each covered two behaviours, that the dangling-symlink case did not check the
symlink survived, that the build configuration was not written exclusively,
that the identity check was duplicated, and that the README's input list did
not name git.

The round-15 tree refuses git older than 2.45 and also passes
`--no-lazy-fetch`, which older git rejects as an unknown option. One helper
checks both identities and deletes a mismatched file. The build configuration
is opened exclusively, like the tar. The version test reports 2.44.9 and
unparseable output (both refused) and 2.45.0 and 3.0.0 (both accepted). The
system-configuration test and the existing-destination test are separate; the
latter covers an existing file and a dangling symlink at both the tar and the
configuration path, and checks that each symlink survives. The README lists
git 2.45 as an input and cites the source, and CI prints `git --version`
before running the tests.

A scratch mutation run against the whole Linux suite removed each of 39
production protections one at a time, and a test failed every time. These
include each version-gate edge (`<=`, a minimum of 2.44 or 2.46, and accepting
unparseable output) and the exclusive configuration write. The two
replace-ref protections and the two lazy-fetch protections each back each
other up, so removing one of a pair alone is not caught; removing both is.
`GIT_ATTR_NOSYSTEM` remains untested. Both real pins still reproduce, and no
packaged file changed: all 231 regenerated distribution files match the
round-10 archives, which pass the verifier.

## Round-15 review

Snapshot
`9a5b267740a262cc9cfed3bf82e1eee7a4baaa48b1a8bda018ec183d70463bb8`
received `PASS` at 100/100 and 95.7/100, with no must-fix finding. The
should-fix was that this record, the README and the handoff said no git
release before 2.45 has `GIT_NO_LAZY_FETCH`. `promisor-remote.c` at the
2.39.4, 2.40.2, 2.41.1, 2.42.2, 2.43.4 and 2.44.1 maintenance tags honours it;
2.39.3, 2.40.1, 2.41.0, 2.42.1, 2.43.0, 2.43.3 and 2.44.0 do not. Only the
`--no-lazy-fetch` option starts at 2.45.0, and that option is why packaging
needs 2.45. The considerations were that two looped tests asserted outside
their per-case block, that the existing-destination test did not check which
files were left behind, that the version test derived its fake output from
its key, and that the CI step name no longer matched what it runs.

The round-16 tree corrects the version history above, in the README and in
the handoff. Every assertion in the version and existing-destination tests
now runs inside its case; the existing-destination test checks the regular
files left for each existing or dangling tar and configuration path; and the
version test is keyed by the exact `git --version` output. The CI step is now
"Test build tools" and refuses git older than 2.45 before running the tests.
`source_bundle.py` is unchanged. The round-15 mutation harness, re-run against
this tree, catches all 39 production mutations, with only the documented
redundant pairs and `GIT_ATTR_NOSYSTEM` surviving. No packaged file changed:
all 231 regenerated distribution files match the round-10 archives, which
pass the verifier.

## Round-16 review

Snapshot
`eea22e3af5ecaf3f60f1bd249160f424f84ffd64d3a33ee5e4bf31aee94fdaec`
received `PASS` from both reviewers at 100/100, with no must-fix or
should-fix finding. Both independently confirmed the corrected version
history against git's source. The considerations were that the
`require_supported_git` docstring could still be read as saying
`GIT_NO_LAZY_FETCH` needs 2.45, that the README's "earlier releases of each
series" did not clearly cover series older than 2.39, that the CI version
gate failed without a message, and that this record's round-13 list quoted
the 2.44 claim without marking it as corrected.

The round-17 tree rewords the docstring to tie the 2.45 minimum to
`--no-lazy-fetch` alone, rewords the README to "releases before those,
including 2.44.0", gives the CI gate an explicit error message, and marks
the round-13 quote as corrected. The only code change is the docstring.

## Round-17 review and fork CI

Snapshot
`e0398d581fdb63b21378ab120097142642f13d662d184c3cba812fdbd876fb6a`
received `PASS` from both reviewers at 100/100, with cosmetic considerations
only. (The sentence above understates the round-17 change set: the CI gate's
error message also changed.) It was committed as `7229e5e` and opened as
coye2/NX-Infinity#2.

The first fork CI run built and verified everything up to the native archives.
The FFmpeg source tars reproduced their pins on the runner's git. The
verifier then rejected `dotnet-THIRD-PARTY-NOTICES.txt`: the packager copied
the .NET license and notices from the root of the dotnet install, and on the
runner that root is shared by several SDKs, so its bytes depend on install
history. The pin had been taken from a standalone 10.0.402 SDK.

The fix reads both files from the RID's pinned 10.0.13 runtime-pack `.nupkg`,
the runtime that the self-contained publish actually bundles, through the
same hash-checked reader as the other package notices. The license bytes are
unchanged; the notice is now the runtime pack's (`66f1d4e4…`), and the policy
pin moves to `9ec24efe…`. A new test requires the notices to come from each
RID's pinned pack rather than a tampered extracted copy, and refuses a wrong
pack hash or an unsupported RID. Three members of each archive change: the
notice, the packaged dependency policy, and the build-manifest entries that
bind them. The round-10 archives are therefore superseded.

The round-18 window reassembled them with the unchanged round-10 driver from
the same publish trees. The two x64 builds are byte-identical
(`b376cb28…`), the classic-zlib x64 build carries the same tar stream, and
the ARM64 archive is `2588a630…`. All four pass the verifier and carry the
runtime pack's notice and their RID's pinned FFmpeg source. The Xvfb probe
and both default-data starts around an install replacement pass, with the
user-data marker unchanged.

## Round-18 review and round 19

Snapshot
`fec13fa7c3cb1f20ac7a142cab492ad64bb360ad55d329c8f0d59aefb73e9877`
received `PASS` from both reviewers at 100/100 and was committed as `ae1f590`.
Its fork CI run passed both jobs and published both archives, which pass the
local verifier. They differ from the round-18 archives in 15 members per
RID:

- the three runtime DLLs, which embed the built commit;
- ten restored-core assemblies of the same size that differ through their
  code. Seven name source-generator version `9.0.14.41315` on CI and
  `9.0.11.2809` (the generators bundled with SDK 9.0.100) locally;
  `Ryujinx.Graphics.Gpu`, `.Shader` and `.Texture` carry no generator version,
  and the cause of their difference is not identified;
- the two manifests, mainly because they record those hashes.
  `backend.json` has no time field; `NX-LINUX-BUILD.json` also records CI's
  own `sourceDateEpoch`.

The hypothesis is that CI's core build rolled forward to a newer 9.0 SDK; a
different compiler would also explain the three DLLs without a generator
string. The next CI run tests it. (This paragraph was corrected in round 20;
round 19 had attributed the difference to all ten DLLs' generator strings and
to the manifests' timestamps.)

Round 19 answers the reviewers' considerations without changing any packaged
file:

- The packager refuses a package cache without a RID before writing, and a
  test proves the RID's .NET notices are written. Removing the `rid=`
  argument in `package.py` was then caught by that run-time refusal, not by a
  unit test; round 20 adds the unit test.
- The NuGet reader opens each package once without following symlinks and
  reads notices from the bytes it hashed. Native notices are written only
  after every pinned package verifies, and an unpinned notice source fails.
- Each dotnet must resolve exactly the SDK its `global.json` names. The
  restored core's upstream pin allows rolling forward to a newer 9.0 feature
  band, which a shared dotnet root could supply. CI now installs its SDKs
  into a private directory and lists them.
- The verifier's independent `10.0.13` literal and the policy's null
  runtime-pack hash stay as they are: drift fails closed, and the pack is
  already bound by its `.nupkg` hash, its ELF hashes and the `.deps.json`
  graph.

55 Linux and 15 source-baseline tests pass. The real package caches give all
18 native notices per RID byte-identical to the round-18 archives.

## Round-19 review and round 20

Snapshot
`cd76332188874e0d99650e092a788c2e37f234f80cfd1f9962012a0cf614546d`
received `PASS` 100 from Reviewer B and `NEEDS CHANGES` 93.75 from
Reviewer A, with two should-fix findings: a FIFO at a package path made
`verified_package` block where round 18 had refused it, and the CI comparison
above misattributed the manifest and three core-DLL differences (now
corrected).

Round 20 fixes both and every consider, again without changing a packaged
file:

- The package is opened with `O_NONBLOCK`, so a FIFO is refused; reads are
  bounded at 256 MiB; missing or linked, non-regular and mismatched packages
  get distinct messages.
- The SDK check reports dotnet's own explanation when it refuses.
- Tests now drive `build_inputs` with fake SDKs (wrong product SDK, swapped
  SDKs and a rolled-forward core SDK are refused before any work directory
  exists), check that `assemble_archive` passes the RID, package cache and
  notices on, check that native notices are written too, and check that
  nothing is written when a later package fails. A 14-mutation sweep over a
  scratch copy catches every mutation.
- The pinned setup-dotnet installs into `DOTNET_INSTALL_DIR` and exports it
  as `DOTNET_ROOT` and on `PATH` (its `dist/setup/index.js`, lines
  99471–99485), so CI's later steps use the private SDK folder.

62 Linux and 15 source-baseline tests pass. Regenerated with the real package
caches and external notices, all 255 distribution files per archive match the
round-18 archives byte for byte.

## Round-20 review, fork CI and round 21

Snapshot
`374351a006cc136bc8890b914c2ee3850bc317ac21d797ecf8bffc1be8065995`
received `PASS` 100 from both reviewers and was committed as `1dfd372`. Its
fork CI run passed both jobs with only 9.0.100 and 10.0.402 in the private
SDK folder. The ten restored-core assemblies and `backend.json` now match the
round-18 archives byte for byte, which confirms that the earlier CI core
build had rolled forward to a newer 9.0 SDK. Four members still differ per
RID, all because of the built commit: the three runtime DLLs embed it (as
ASCII and UTF-16), which also moves their content-derived timestamp and
16-byte identifier, and the build manifest records CI's commit time and those
three hashes.

Round 21 answers the round-20 considerations without changing a packaged
file:

- A failed package open reports the system's error text, and the descriptor
  is closed on every failure path.
- Duplicate destinations among the package notice sources are refused; both
  package-notice copies share one writer. (Round 22 widens this to every
  distribution write.)
- The SDK query times out after 120 seconds and, on refusal, keeps dotnet's
  list of installed SDKs.
- Tests close the reviewers' mutation gaps: the FIFO timeout cannot be
  mistaken for a refusal, a package replaced after hashing is never re-read,
  both size bounds are tested at the limit, and each `global.json` refusal
  asserts its own message. A 23-mutation sweep catches every mutation.

68 Linux and 15 source-baseline tests pass, and the 255 regenerated
distribution files still match the round-18 archives.

## Round-21 review, fork CI and round 22

Snapshot
`e9ac5a62d36fe4d8ba7c8c73df65e76acc6c67134e56cd5745359564e41e381c`
received `PASS` 95 from Reviewer A and `PASS` 100 from Reviewer B and was
committed as `b8191ea`. Its fork CI run passed both jobs; against round 18,
only the four commit-dependent members differ. Reviewer A's should-fix was a
test gap: the round-21 "never re-read" test replaced the package only after
both reads, so hashing a second read while returning the first went unnoticed.

Round 22 answers it and every consider without changing a packaged file:

- The test now replaces the package between the read and the hash, which
  catches hashing a re-read, hashing the path and returning a re-read.
- A test requires the open-descriptor count to be unchanged after every
  refusal that opened the package.
- Every distribution write creates its file exclusively, so no notice or
  other distribution file can silently replace another; a collision fails
  the build. The real inputs have none.
- The open-failure message no longer blames symbolic links for every error,
  and the tests derive expected error text from `errno` rather than glibc's
  wording.
- A timed-out SDK query is reported as a refusal naming the pin.

A 30-mutation sweep catches every mutation. 71 Linux and 15 source-baseline
tests pass, and the 255 regenerated distribution files still match the
round-18 archives.

## Round-22 review, fork CI and round 23

Snapshot
`a408d4a3ff59f2198f63cdd5b967a4e5e2a18fafaa0a3c0d3f3af26b996e5f82`
received `PASS` 95.7 from Reviewer A and `PASS` 100 from Reviewer B and was
committed as `80bcb34`. Its fork CI run, the first with exclusive
distribution writes, passed both jobs; against round 18, only the four
commit-dependent members differ. Reviewer A's should-fix was a test gap: the
collision test accepted any collision, and its second call collided on the
icon first, so `write_text` could have gone back to overwriting unnoticed.

Round 23 answers it and every consider without changing a packaged file:

- Each collision refusal must name the exact file, starting with the
  launcher written by `write_text`; a second test makes a package notice
  clash with a runtime notice.
- The reader must request exactly `MAX_PACKAGE_BYTES + 1` bytes, must close
  every stream it opens, and must close its descriptor even when wrapping it
  raises a non-`ValueError` or a `BaseException`.
- A `dotnet` that cannot be executed is refused with a message naming the
  pin.
- The test alarm guard restores an outer pending timer as well as the
  handler.

A 37-mutation sweep catches every mutation. 75 Linux and 15 source-baseline
tests pass, and the 255 regenerated distribution files still match the
round-18 archives.
