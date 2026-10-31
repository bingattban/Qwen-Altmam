# Windows CI and test-first merge gate (Phase 01)

**PR only:** make changes on a feature branch. Do not push ordinary updates directly to `main`.

1. A PR triggers `.github/workflows/windows-ci.yml` (**Windows Core Build**) and `.github/workflows/windows-release.yml` (**Windows Release Packages**) independently and concurrently.
2. Core: route/action/CSP guards + Node UI checks → .NET Release build → actual Windows xUnit tests.
3. Release: independent preflight → .NET Release build + full test suite (reject 0 tests) → *only after tests succeed*, publish **both win-x86 and win-x64** WPF self-contained apps and package a **Portable ZIP and Installer EXE** for each, recording SHA-256 and publish metadata.
4. Require **success for both workflows on the exact current PR head SHA**, real uploaded Portable and Installer artifacts, and reviewed changes before merge. Failed/skipped/cancelled/missing artifacts block merge; fix on branch and retest.
5. Merge the verified PR, then inspect Core and Release checks on the new `main` merge SHA. PR builds are *candidates*, not signed or user-accepted final production releases.

**Non-automated gate:** the owner must configure repository rulesets/branch protection for `main` to enforce the required checks for other collaborators. The active connector cannot enforce branch protection. Actual clean offline Windows install/uninstall, DPAPI portability and recovery checks require manual Windows evidence before production handoff.

**Test records:** use GitHub Actions run IDs, test-results-windows, core-test-results-windows, actual artifact names and checksum files from the exact tested SHA; never cite stale results as completion.


**Owner-approved Portable semantics (2026-10-28):** `Portable` means the extracted WPF application can run without installation. Both x86/x64 Portable and Installer distributions share the existing `%LOCALAPPDATA%\\AlTamam` data root for that Windows account; CI must exercise `AppDataLayoutTests` in the full Windows test suite. The build must not migrate, move or delete existing databases. Clean-machine GUI acceptance and preservation of original DPAPI key files remain separate gates.


## Phase 01: repeatable Windows development and packaging

The repository intentionally has **exactly two** Windows workflows. The canonical
Core workflow file is `.github/workflows/windows-ci.yml` (display name and
required job names are unchanged); Release stays `.github/workflows/windows-release.yml`.
Both are independently triggered on pull requests and pushes to `main`.
The Release `package` matrix has a hard `needs: build-test` dependency;
packaging a failed or unexecuted test suite is forbidden. A PR checkout may
be GitHub's temporary merge commit; the release metadata **also** records the
exact source PR head and compares real package bytes against SHA-256 hashes.

Run on a Windows development machine with .NET 10 SDK, PowerShell 7,
Node.js and Python 3.12:

```powershell
dotnet restore AlTamam.slnx
dotnet build AlTamam.slnx -c Debug --no-restore
dotnet build AlTamam.slnx -c Release --no-restore
dotnet test AlTamam.slnx -c Release --no-build --no-restore
python build/manifest-guard.py
python build/validate-ui-shell.py
python build/test-ci-exit-guards.py
python build/test-ci-performance-contract.py
pwsh -NoProfile -File ./build/test-webview-ui-e2e.ps1
```

Direct `dotnet publish` builds the executable but **not** the final offline
package. The approved Release workflow additionally verifies and bundles the
full Microsoft WebView2 offline standalone installer, Arabic fonts and OFL
notices, and native SQLCipher dependencies. It performs offline UI self-checks,
builds the customer-facing Portable ZIP and Inno Setup Installer EXE for
**each** `win-x86` and `win-x64`, exercises the packaged binaries and records
`SHA256SUMS.txt` and `BUILD_METADATA.json`. Download the four final named
artifacts and two checksum artifacts from a **successful** Release run; do not
redistribute a raw publish-directory artifact as a complete offline package.

Never attach database files, personnel spreadsheets, exported PDFs, DPAPI keys,
credential material or `.altrecovery` files to a PR, a test artifact or a
release. `.gitignore` plus the tracked-file check in
`build/validate-ui-shell.py` guard these known extensions; the check runs
in **both** Windows preflights. It is not a replacement for human secret review.

## Dependency and licensing checks (Phase 01.07–01.08)

- Exact direct NuGet versions live in `Directory.Packages.props`; .NET 10 SDK
  is selected on Windows runners. Restore and vulnerability checks run on CI;
  a full transitive `packages.lock.json` has **not** been established, so an
  immutable complete dependency graph must not be claimed.
- Microsoft WebView2: the **standalone offline installer**, not its network
  bootstrapper, is downloaded on the **CI runner only**, Authenticode-checked
  as Microsoft-signed and included in both architectures' packages. Microsoft
  redistribution documentation: https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution
- Microsoft.Data.Sqlite.Core is MIT-licensed; the SQLCipher native provider is
  the unofficial SQLitePCLRaw bundle. Microsoft documents the combination:
  https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/custom-versions
- The pinned `SQLitePCLRaw.bundle_e_sqlcipher` **2.1.10** provider has been
  deprecated by its maintainer (2026). It is retained for now to preserve
  existing encrypted databases; the owner conditionally approved a supported
  alternative, **only after** x86/x64 cross-provider compatibility, recovery
  and redistribution-license checks have actually passed.
  See https://github.com/ericsink/SQLitePCL.raw/wiki/SQLite-encryption-options-for-use-with-SQLitePCLRaw
- Excel import/export uses built-in `System.Xml` and `System.IO.Compression`
  for OOXML, not a third-party Excel runtime. Cairo and Tajawal fonts and their
  `OFL` notices are staged only during packaging from a pinned source; do not
  omit licenses in final distributions.
- Code signing and the full owner EULA are **not** inferred from a green CI
  run. Unsigned CI candidate artifacts and pending clean-machine manual UAT
  must never be represented as signed/final production releases.

## Owner-reconciled Phase 01 architecture decision (2026-10-31)

The owner explicitly approved preserving `AlTamam.DesktopHost` as the actual
WPF executable/project identity and `src/AlTamam.WebUI` as a separately managed
static HTML/CSS/JavaScript asset module. The six logical responsibilities
remain partitioned; introducing duplicate C# wrappers or renaming the
executable would risk packaging, installation and existing user data with no
approved benefit. No other routes, users, screens or features change here.

The owner also authorized **investigating** a supported SQLCipher upgrade and
implementing it **only after** complete compatibility with existing encrypted
databases and backups is proven. The current native SQLCipher bundle is
therefore deliberately unchanged in this Phase 01 PR: no alternative provider
and licensed native binaries are provisioned, and no cross-provider read,
write, restore and on-disk compatibility suite has run. See
[`SQLCIPHER_UPGRADE_EVALUATION.md`](SQLCIPHER_UPGRADE_EVALUATION.md) for
authoritative sources, compatibility requirements and outstanding evidence.
A green build with the *old* provider cannot be counted as proof that a
*different* provider would open existing files.

This approval reconciles the naming conflict; it is **not** permission to
silently change the database encryption provider in later phases.


## Owner decision for Phase 02 (2026-10-31): keep SQLCipher unchanged

No SQLCipher provider replacement, native-version change, schema rekey or database
migration to a different encryption engine is authorized. The current
\`SQLitePCLRaw.bundle_e_sqlcipher 2.1.10\` is intentionally preserved.
The historical evaluation document is **not** a pending implementation action.
Phase 02 adds an independent \`tests/AlTamam.IntegrationTests\` project to
verify the existing provider against encrypted-reopen, wrong-key/unencrypted
rejection, temporal snapshots, half-open assignment intervals, concurrent
writes, immutable audit and transactional rollback on actual Windows runners.
Both Windows CI workflows execute it after one solution build and before any
Release package is generated, failing if the dedicated TRX reports fewer than
eight real tests. No personnel data or production secrets are used.
