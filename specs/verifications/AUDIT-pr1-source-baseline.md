---
type: code-audit
context: reproducible-source-baseline
---

# Code audit — PR 1 source baseline

Verdict: READY for independent review.

## Checklist

- ✓ Supply chain and security: every declared app/host package version matches the shipped v1 dependency graph, except the intentional Linux RID counterpart at the same pinned FFmpeg package version. No secret-pattern matches. External repositories are commit-pinned, every Kenji-history source is bound to a commit/path/blob tuple and every restored source is checked by SHA-256 before compilation. Known HIGH dependency advisories and mutable reconstructed network inputs are release-blocking exceptions in `specs/security/EXCEPTIONS.md`; audit warnings remain enabled.
- ✓ Provenance and metadata: source revisions, Git commit/path/blob identities, assembly hashes, released-source hashes and overlay hashes are recorded in `build/source-baseline/sources.json`. New specs have `type` and `context` metadata.
- ✓ Law of Demeter: the authored restoration/smoke pipeline operates on direct inputs. Existing long object chains in recovered product source were not newly designed here.
- ✓ Repository conventions: this repository has no `CONVENTIONS.md`; existing root documentation layout was preserved. CI uses pinned actions and explicit immutable Git fetches; no direct GitHub REST calls or issue creation were added.
- ✓ Scope: changes restore the prerequisite source baseline only. Native Linux packaging, Android, gameplay and device work remain excluded.
- ✓ Boy Scout Rule: compiler-artifact collection classes were reconstructed as collection expressions, fire-and-forget tasks are explicit, trailing whitespace is removed, and dependency publishing now includes the complete core graph.
- ✓ Types and safety: authored Python functions are typed; JSON dynamism is isolated behind `JsonObject`. Recovery paths reject POSIX and Windows escape forms, and hash-sensitive external files are read from pinned commit/path objects. No suppression directives or unsafe type-bypass casts were added.
- ✓ Tests: 15 focused Python tests cover cross-platform path escape rejection, commit/path/blob binding, checkout line-ending independence, byte/hash rejection, duplicate JSON fields, manifest coverage, exact dependency/asset completeness and strict protocol message validation. End-to-end restoration, three core builds, host publishing, a 107-library/135-asset dependency smoke, 63 contract tests and 73 service tests also pass.
- ✓ SOLID and heuristics: restoration validation, materialization, receipt generation and worker smoke concerns are separated. Subprocesses use argument arrays, outputs are non-overwriting, and source/dependency invariants fail closed.
- ✓ Refactoring smells: no new feature envy, middle-man layer or duplicated dependency allowlist remains in the authored pipeline. The complete release-derived host inventory is kept in one data file rather than production branching logic.
- ✓ Code style: authored functions are small or orchestration-only and descend one abstraction level. Recovered launcher/core files retain large methods and decompiler control-flow labels to preserve the behavioral baseline; broad refactoring before parity/review would defeat this PR's evidence goal.

## Red flags and explicit limits

- No `slopcheck` executable or repository script exists. Package trust was checked instead against the independently shipped v1 `.deps.json` inventories and exact versions.
- No Windows execution or launcher GUI smoke was available. This audit does not claim those environments passed.
- Reconstructed product source contains long files, methods and `IL_` labels. Treating that as clean architecture would be a rationalization; it is accepted only as an explicitly documented recovery boundary and should be improved incrementally after this baseline lands.
- Core warnings and NuGet advisories are not called clean. Exact counts and release-blocking status are recorded in `.agent/HANDOFF.md` and `specs/security/EXCEPTIONS.md`.
