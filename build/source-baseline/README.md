# v1 source baseline

The v1 release source manifest names 4,081 core files, but the public repository
did not contain the launcher, core host, or core fork. This directory restores a
buildable baseline without vendoring the unchanged Kenji tree.

`sources.json` separates the inputs:

- 3,930 files are exact at the pinned Kenji revision.
- 75 files are exact Git blobs from Kenji history.
- 13 files are exact at the pinned later Ryujinx revision.
- 62 files are source reconstructions from the shipped managed assemblies.
- One test-only source file was neither shipped as IL nor needed by production.
- Five helper files are decompiler-split types needed to compile the
  reconstruction.

Every exact source is checked against `NX-BUILD-SOURCES.json`. Reconstructed
files have separate released-source and checked-in-overlay hashes; they are not
misrepresented as byte-identical original source.

## Restore

From the repository root, use Bash and scratch storage outside the repository:

```text
mapfile -t kenji_revisions < <(
  python3 build/source-baseline/restore.py --print-kenji-revisions
)
git init <scratch>/kenji
git -C <scratch>/kenji remote add origin https://starforgejo.dev/projects/Kenji-NX.git
git -C <scratch>/kenji fetch --no-tags --depth=1 origin "${kenji_revisions[@]}"
git -C <scratch>/kenji checkout --detach "${kenji_revisions[0]}"

git clone --filter=blob:none https://github.com/ChanseyIsTheBest/ryujinx.git <scratch>/later-ryujinx
git -C <scratch>/later-ryujinx checkout --detach 20888e6d1f96bcf1a59f415c745c684e38529503

NXI_TEST_SCRATCH=<scratch> python3 -B build/source-baseline/test_tools.py

python3 build/source-baseline/restore.py \
  --kenji-repo <scratch>/kenji \
  --later-repo <scratch>/later-ryujinx \
  --output <scratch>/nx-v1-source
```

Each of the 75 Kenji-history sources is bound to a commit, path and blob in
`sources.json`. `--print-kenji-revisions` emits the base revision followed by
the 11 history commits so callers can fetch the complete immutable object set
without relying on mutable branch refs. The restorer reads each exact external
source from its pinned commit and verifies its blob, byte count and SHA-256, so
checkout line-ending conversion cannot change restored bytes. Checked-in
overlays and linked runtime files are forced to LF by `.gitattributes`. The
command refuses to overwrite an existing output directory. A successful run
writes `SOURCE-RECOVERY-RECEIPT.json`.

## Build

The restored core uses .NET SDK 9.0.100. The launcher and core host use SDK
10.0.402.

```text
cd <scratch>/nx-v1-source
dotnet build src/Ryujinx.HLE/Ryujinx.HLE.csproj -c Release
dotnet build src/Ryujinx.Graphics.Vulkan/Ryujinx.Graphics.Vulkan.csproj -c Release
dotnet build src/Ryujinx.Audio.Backends.OpenAL/Ryujinx.Audio.Backends.OpenAL.csproj -c Release

cd <repo>/src
dotnet publish core/NxInfinity.Core/NxInfinity.Core.csproj -c Release \
  -p:CoreSourceRoot=<scratch>/nx-v1-source \
  -o <scratch>/nx-core-host
dotnet build NxInfinity.slnx -c Release

cd <repo>
python3 build/source-baseline/smoke_worker.py \
  --dotnet <path-to-dotnet-10> \
  --worker <scratch>/nx-core-host/NxInfinity.Core.dll
```

The commands must resolve the SDK versions pinned by the corresponding
`global.json` files. The smoke check compares the publication with
`host-publish-inventory.json`: all 107 dependency libraries and the exact
135-entry runtime/resource/native asset inventory must match, and every declared
asset must be present as a regular file.

The pinned baseline currently reports NuGet advisories for `System.Private.Uri`
4.3.0 and `System.Text.Json` 8.0.0. They are preserved here as observed release
inputs, not treated as acceptable dependencies for a new release. Updating and
qualifying them is separate from reconstructing the v1 source baseline; the
release-blocking scope is recorded in the [security exceptions](../../specs/security/EXCEPTIONS.md).

## Provenance boundary

The launcher, core host, and 62 core files are source reconstructions from the
managed assemblies identified in `sources.json`. They are buildable source, not
a claim that decompilation recovered the authors' original formatting or local
names. Exact source files remain bound to the release manifest's byte counts and
SHA-256 values.

The 13 later source files retain the Ryujinx Team MIT notice; the pinned Kenji
base retains the Kenji-NX MIT notice. Both notices are copied into the restored
tree.

The reconstructed launcher intentionally does not redistribute the bundled
Hello Kitty avatar artwork. It fetches those images from the already recorded
publisher URLs when selected. The zero-byte `nx-brand-reference.png` placeholder
is omitted; the launcher logo is the byte-identical public asset already stored
under `assets/`. The bundled TOTK timing module remains accompanied by its
GPL-2.0 corresponding source under `third_party/totk-dfps`.

The reconstructed v1 launcher also retains mutable network inputs for OCR
language models and avatar catalogs. Those inputs are bounded but not pinned by
content hash. They are documented as release-blocking inherited behavior in the
[security exceptions](../../specs/security/EXCEPTIONS.md); a later release must
pin and verify them before native parser or image-decoder use.
