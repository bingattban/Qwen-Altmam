# Windows CI and test-first merge gate (Phase 01)

**PR only:** make changes on a feature branch. Do not push ordinary updates directly to `main`.

1. A PR triggers `.github/workflows/windows-build.yml` (**Windows Core Build**) and `.github/workflows/windows-release.yml` (**Windows Release Packages**) independently and concurrently.
2. Core: route/action/CSP guards + Node UI checks → .NET Release build → actual Windows xUnit tests.
3. Release: independent preflight → .NET Release build + full test suite (reject 0 tests) → *only after tests succeed*, publish **both win-x86 and win-x64** WPF self-contained apps and package a **Portable ZIP and Installer EXE** for each, recording SHA-256 and publish metadata.
4. Require **success for both workflows on the exact current PR head SHA**, real uploaded Portable and Installer artifacts, and reviewed changes before merge. Failed/skipped/cancelled/missing artifacts block merge; fix on branch and retest.
5. Merge the verified PR, then inspect Core and Release checks on the new `main` merge SHA. PR builds are *candidates*, not signed or user-accepted final production releases.

**Non-automated gate:** the owner must configure repository rulesets/branch protection for `main` to enforce the required checks for other collaborators. The active connector cannot enforce branch protection. Actual clean offline Windows install/uninstall, DPAPI portability and recovery checks require manual Windows evidence before production handoff.

**Test records:** use GitHub Actions run IDs, test-results-windows, core-test-results-windows, actual artifact names and checksum files from the exact tested SHA; never cite stale results as completion.


**Owner-approved Portable semantics (2026-10-28):** `Portable` means the extracted WPF application can run without installation. Both x86/x64 Portable and Installer distributions share the existing `%LOCALAPPDATA%\\AlTamam` data root for that Windows account; CI must exercise `AppDataLayoutTests` in the full Windows test suite. The build must not migrate, move or delete existing databases. Clean-machine GUI acceptance and preservation of original DPAPI key files remain separate gates.
