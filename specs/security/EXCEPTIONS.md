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

## Mutable reconstructed network content

Status: accepted for source reconstruction only; blocks an unreviewed release.

The reconstructed v1 launcher retains two mutable download boundaries:

- OCR language models come from `tesseract-ocr/tessdata_fast` branch `main`. The launcher limits each response to 32 MiB but does not pin a commit or verify a per-language SHA-256 before native Tesseract parses it.
- The optional avatar catalog reads `henry-debruin/nso-icons` branch `main`, and the omitted Hello Kitty artwork is fetched from recorded publisher URLs. Downloads are size-bounded and decoded through the existing image normalizer, but their content identities are not pinned.

These behaviors are evidence of the shipped launcher, not approval for a new artifact. Before native Linux, Android or any later release, pin immutable upstream revisions and expected size/SHA-256 identities, reject unexpected redirects, verify cached content on every use and requalify the parser/decoder paths.
