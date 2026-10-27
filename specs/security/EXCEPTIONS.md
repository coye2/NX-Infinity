---
type: security-exceptions
context: reproducible-source-baseline
---

# Security exceptions

## Pinned v1 core dependencies

Status: accepted for source reconstruction only; blocks an unreviewed release.

The exact v1 core baseline retains these NuGet audit findings:

- `System.Private.Uri` 4.3.0: GHSA-5f2m-466j-3848, GHSA-x5qj-9vmx-7g6g and GHSA-xhfc-gr8f-ffwc.
- `System.Text.Json` 8.0.0: GHSA-8g4q-xg66-9fp4 and GHSA-hh2w-p6rv-4g7w.

These versions are preserved only to prove that the public source reconstruction builds against the released dependency graph. Audit warnings remain enabled in CI and local builds. Native Linux, Android or any later release must upgrade or otherwise resolve these findings and requalify behavior before publication; this exception does not approve distributing a new artifact with them.

Native Linux resolution: the self-contained core host uses the pinned .NET
10.0.13 runtime pack from SDK 10.0.402 instead of redistributing the old
standalone packages. The archive verifier requires that runtime-pack identity
and rejects the vulnerable v1 identities in the final
launcher and core dependency manifests. Warnings from compiling the unchanged
restored v1 source graph remain reconstruction evidence; those vulnerable
package identities are not accepted in a native Linux archive.

## Mutable reconstructed network content

Status: accepted for source reconstruction only; blocks an unreviewed release.

The reconstructed v1 launcher retains two mutable download boundaries:

- OCR language models come from `tesseract-ocr/tessdata_fast` branch `main`. The launcher limits each response to 32 MiB but does not pin a commit or verify a per-language SHA-256 before native Tesseract parses it.
- The optional avatar catalog reads `henry-debruin/nso-icons` branch `main`, and the omitted Hello Kitty artwork is fetched from recorded publisher URLs. Downloads are size-bounded and decoded through the existing image normalizer, but their content identities are not pinned.

These behaviors are evidence of the shipped launcher, not approval for a new artifact. Before native Linux, Android or any later release, pin immutable upstream revisions and expected size/SHA-256 identities, reject unexpected redirects, verify cached content on every use and requalify the parser/decoder paths.

Native Linux resolution: RID builds define `NXI_NATIVE_LINUX`. That build
removes the remote avatar catalog downloads and loader, the catalog row,
grid, paging and caption from the avatar dialog, the translation settings and
timer, and the OCR download implementation at compile time, while retaining
local avatar import and profiles. The archive verifier scans the launcher
assembly and rejects the mutable branch and publisher URL markers, the
translation-settings strings, and the catalog-only avatar dialog strings.

Inert residue remains in the Linux launcher assembly and is never shown or
reached: the dialog still constructs its category, search, paging and
favorite controls but never attaches them; the catalog caption and reload
label remain as keys in the shared `UiText` localization table; the
`AvatarCatalog.Repository`, `IslandSource` (the game's website URL, never
fetched) and `IslandAttribution` constants and the island category label
remain; and the unreachable `TranslateGameFrameAsync`, `GameTextCapture` and
`LocalTranslator` code still compiles. The never-assigned `_translator` field
makes each native Linux publish emit warning `CS0649`; both of its readers,
`StopTranslation` and the unreachable `TranslateGameFrameAsync`, check for
null.
This does not resolve or approve those network paths for Windows or Android.

## Third-party producer CodeView paths

Status: accepted package provenance; fail closed on any drift.

Twelve managed assemblies obtained from pinned NuGet packages retain their
upstream producer's absolute PDB path in PE CodeView metadata. Rewriting those
package bytes would break their exact package identity and may invalidate
strong-name evidence. Native Linux builds therefore preserve those known
strings, bind each one to its assembly path and SHA-256 in
`NATIVE-DEPENDENCIES.json`, and reject every additional or changed producer
path. NX-owned and reconstructed-core builds still map local source roots and
the archive rejects PDB files and any unreviewed Unix, drive-qualified, or UNC
CodeView path.

The verifier reads these paths from each portable executable's debug
directory, decodes the complete NUL-terminated CodeView (`RSDS`) and
ReadyToRun perf-map (`R2RM`) paths as strict UTF-8, and rejects malformed
portable executables, executables without a debug directory, unknown debug
record types, and non-UTF-8 paths. A CodeView record must end at its path's
terminating NUL; a perf-map record must be exactly 284 bytes; any byte after the
NUL must be zero. Only paths under the observed source-mapped roots `/_/src/`,
`/_/packages/`, `/_/binding/` and `/_/artifacts/` (with no empty, `.` or `..`
component, colon or backslash), and bare file names without separators,
whitespace padding or control characters, are accepted
without an exact policy entry. Two
pinned package assemblies (`Humanizer` and
`Microsoft.IO.RecyclableMemoryStream`) embed portable PDBs whose source
documents are mapped to `/_/`; each is bound to its SHA-256, and any other
embedded PDB is rejected.

Separately, `cores/nxi/LibHac.dll` from the pinned `Ryujinx.LibHac` 0.20.0
package (SHA-256
`b68c441a59974b58f0941d068813fc94df0c3a47dcc38552ea1036838c7cb628` on both
RIDs) stores 127 distinct caller-file paths from its upstream author's Windows
checkout (`C:\Users\gr33m\devprojects\C#\LibHac\src\...`) as UTF-16 string
literals in its user-string heap. They are upstream assembly bytes, not NX
build paths, and are retained unchanged. A scan of every portable executable in
the round-8 packages found no other such path outside debug records.

## Prebuilt native dependency provenance

Status: exact artifacts and available notices are pinned; maintainer
redistribution review is still required before release publication.

Native Linux uses prebuilt assets from the pinned NuGet packages for
Skia/HarfBuzz, GLFW, SDL3, Shaderc and FFmpeg. The archive inventory records
each package SHA-256, each per-RID native file SHA-256, its declared license and
the strongest source revision observable from package or binary metadata. Every
ELF path and hash is independently pinned. Managed package and native dependency
notices are included and verifier-required.

The binaries were not independently reproduced from those source revisions.
FFmpeg x64 identifies source `39cac587c4f78f7c26e0f0e5da664943d4e9fade`;
ARM64 identifies `e16ff06adb9acd647c0c33aa3c27438e1009b822`.
Each archive includes that RID's complete source and binary-recorded build
configuration. The NuGet metadata says `LGPL-2.1-only` while both binaries say
`LGPL-2.1-or-later`; both facts and the LGPL-2.1 text are retained. SDL x64
embeds revision `0ccb76ae876e3e893d179828f16f0933326bf084`, while the
ARM64 binary reports `NOTFOUND`; its source revision remains explicitly unknown.
These are provenance limitations, not permission to omit a maintainer license
review when publishing a release.
