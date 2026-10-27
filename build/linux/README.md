# Native Linux archives

This directory builds the desktop launcher and NX Infinity Core as .NET
self-contained application hosts for `linux-x64` and `linux-arm64`. The output
is a portable, deterministic `.tar.gz`; it is not an AppImage or Flatpak.

## Inputs

- The source baseline restored by
  [`../source-baseline`](../source-baseline/README.md).
- .NET SDK 10.0.402.
- The core projects built with .NET SDK 9.0.100.
  `package.py` refuses a `--dotnet` that resolves any SDK other than the one
  named by `src/global.json`, and a `--core-dotnet` that resolves any SDK
  other than the one named by the restored core's `global.json`. The core's
  pin (`rollForward: latestFeature`) lets dotnet pick any newer 9.0 SDK, a
  later patch or a later feature band, so use a dotnet root that holds no 9.0
  SDK newer than 9.0.100.
- Git 2.45 or later, for both packaging and `test_tools.py`.
- A Git checkout containing the FFmpeg commits embedded by each native package:
  `39cac587c4f78f7c26e0f0e5da664943d4e9fade` for x64 and
  `e16ff06adb9acd647c0c33aa3c27438e1009b822` for ARM64.
- Immutable notice inputs fetched through `fetch_notice_sources.py`.
- A writable work and output directory outside the repository.

The package remains `unqualified` until native gameplay, audio, controller and
save/load checks are recorded. The build and protocol gates do not make those
hardware claims.

## Build

From the repository root, after restoring and building the core:

```sh
export NUGET_PACKAGES=/path/to/nuget-cache
epoch=$(git show -s --format=%ct HEAD)
python3 -B build/linux/fetch_notice_sources.py \
  --output /path/to/empty-notice-scratch

python3 -B build/linux/package.py \
  --rid linux-x64 \
  --core-source /path/to/restored-core \
  --core-dotnet /path/to/dotnet-9 \
  --dotnet /path/to/dotnet \
  --work /path/to/empty-linux-x64-work \
  --output /path/to/output \
  --notice-sources /path/to/empty-notice-scratch \
  --ffmpeg-source /path/to/pinned-ffmpeg-repository \
  --epoch "$epoch"

python3 -B build/linux/verify.py \
  --archive /path/to/output/NX-Infinity-1.0.0-linux-x64.tar.gz \
  --rid linux-x64
```

Repeat with `linux-arm64`. The work directory must not exist. The output
directory may contain the other architecture.

## Package contract

- `NxInfinity` is the launcher apphost. `nx-infinity` supplies its immutable
  install root.
- `cores/nxi/NxInfinity.Core` is the native worker apphost.
- `cores/nxi/backend.json` hashes every worker file and declares
  `managedAssembly: false`.
- `NX-LINUX-BUILD.json` hashes every other archive file and records the RID,
  source baseline, source epoch, qualification and system dependencies.
- The archive includes the desktop entry, icon, licenses, dependency
  provenance, available upstream notices, and complete modified source for the
  embedded GPL timing module. The pinned `NATIVE-DEPENDENCIES.json` binds each
  native archive path, both complete RID-specific `.deps.json` graphs and
  runtime targets, all native notice bytes, the complete managed NuGet package
  notice tree, and the 211-file GPL source tree.
- Package notices are read directly from SHA-256-pinned `.nupkg` archives; a
  mutable extracted NuGet-cache copy is not trusted as their source.
- The bundled FFmpeg libraries are accompanied by the uncompressed `git archive`
  tar of the exact per-RID source revision and the per-RID build configuration
  recorded in each binary. The tar is not compressed separately, so its pinned
  SHA-256 does not depend on the host's zlib. Packaging requires git 2.45 or
  later, the first release with the `--no-lazy-fetch` option (`git.c` at tag
  `v2.45.0`), and refuses older git. `GIT_NO_LAZY_FETCH` alone is older: the
  2.39.4, 2.40.2, 2.41.1, 2.42.2, 2.43.4 and 2.44.1 maintenance releases honour
  it in `promisor-remote.c`, but releases before those, including 2.44.0, do
  not. The packager runs git without any inherited `GIT_*` variables, without
  user or system configuration and system attributes, without replace refs
  (`--no-replace-objects` and `core.useReplaceRefs=false`) and without lazy
  fetches (`--no-lazy-fetch` and `GIT_NO_LAZY_FETCH`). It pins
  `core.autocrlf`, `core.eol`, `core.attributesFile`, `core.bigFileThreshold`
  and `tar.umask` over the repository's own settings. It refuses a repository
  whose `info/attributes` is a symlink, is not a
  regular file, or is not empty. Both pinned commits' own `.gitattributes` use
  no `filter`, `ident` or `export-subst` attribute, so for these commits the
  tar depends only on the commit and on git's tar writer. The pins were
  produced with git 2.56.0; another git version's tar output is not yet
  confirmed. A tar or build configuration that does not match its pin is
  deleted and packaging stops with an identity mismatch. Because the global
  configuration is ignored, a global `safe.directory` entry does not apply:
  the FFmpeg repository must be owned by the user running the packager.
- User data is outside the archive. Replacing an extracted application folder
  cannot overwrite the default data root.
- Restored-core debug records are disabled and local source paths are mapped to
  `/_/nx-core`; PDB files are rejected. Immutable producer CodeView paths
  already embedded in third-party NuGet assemblies are read from each PE debug
  directory as UTF-8 and independently allowlisted by assembly hash, so no new
  producer path can enter silently. Every PE must have a debug directory, and
  its path records may not carry bytes after the path. The two package
  assemblies that embed portable PDBs are likewise pinned by hash.
- The independently hash-pinned policy also fixes the complete package path set
  and every ELF path/hash per RID. Regenerating the package's self-inventory
  cannot authorize an added game archive or native binary.
- The archive must be one gzip member with no header name, comment or extra
  field, the default-level XFL and unknown-OS header bytes, and no bytes after
  it. The `.sha256` file pins the compressed bytes themselves. Its decompressed
  tar stream must equal the canonical serialization of the verified package
  files, so nothing can be carried in tar headers, padding or after the
  end-of-archive marker.
- The canonical tar stream is the reproducible identity. No file inside the
  package is compressed by the build, so every packaged file and that tar
  stream are independent of the host's zlib. Only the outer gzip bytes depend
  on it (classic zlib and zlib-ng can produce different deflate output at the
  same level), so archives built on different hosts can have different
  `.sha256` values while carrying byte-identical package files.
- Every `DT_NEEDED` library is either bundled or one of the declared system
  libraries in `NX-LINUX-BUILD.json`.
- Relative links in the packaged Markdown files resolve inside the archive.
  Links to repository files that are not packaged become plain source-repository
  paths. Upstream package README files under `licenses/native/` keep their
  hash-pinned bytes, including their own repository-relative links.

The verifier rejects unsafe, non-canonical or colliding paths, links,
wrong-architecture ELF files, misplaced or altered native assets,
dependency-graph or runtime-target drift, altered notices, incomplete GPL
source, games/keys/firmware/user data, build-path-bearing PDBs, mutable online
avatar/OCR code, avatar-catalog controls or translation settings in the Linux
launcher, and the
vulnerable v1 package identities blocked by
[`specs/security/EXCEPTIONS.md`](../../specs/security/EXCEPTIONS.md).

## Native smoke

On x64, exercise the actual Avalonia event loop and worker handshake without
game content:

```sh
xvfb-run -a /path/to/extracted/nx-infinity \
  --validate --backend nxi --probe \
  --data-root /absolute/scratch/data \
  --report /absolute/scratch/report.json \
  --seconds 1
```

This CPU-only Xvfb check proves launcher startup and protocol setup. It does not
exercise Vulkan rendering, audio, controllers, games, firmware, keys, or saves.
