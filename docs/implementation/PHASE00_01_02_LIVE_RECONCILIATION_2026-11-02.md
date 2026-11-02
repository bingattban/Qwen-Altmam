# PHASE 00–02 — CURRENT live reconciliation and guarded evidence

**Report scope:** repository `bingattban/Qwen-Altmam`. Baseline reviewed on
2026-11-02: merged `main` at `ff91ad4d77f15861dced2f50fbbe5b6e737c68e4`
(PR #199). All inventory values below refer to source actually fetched from
that repository. This report records current implementation; it does **not**
certify a customer release or overwrite historical owner decisions.

**Authority:** latest explicit owner decisions > approved reconciled plan >
compatible historical drafts. In particular, keep `DesktopHost` and
`WebUI` as currently shipped, preserve the existing SQLCipher provider,
and do not activate destructive `RestoreDatabaseAtomically`.

## 00 — Current baseline and gap reconciliation

| Invariant | Verified source | Meaning |
|---|---|---|
| Solution topology | `AlTamam.slnx`: **5 C# projects** (Domain, Contracts, Application, Infrastructure, DesktopHost) plus **3 test projects** (Tests, UnitTests, IntegrationTests). WebUI is shipped as local HTML/CSS/JS content, not a new independent assembly. | Preserve owner's approved file-level split; don't invent an unneeded project. |
| Routes | `screen-manifest.json`: **69** declared and listed routes | Structural route count only, not proof that all 69 business screens work end-to-end. |
| Actions | `action-manifest.json`: **103** canonical actions; 11 service owners | Exact immutable bridge contract; `RestoreDatabaseAtomically` still disabled. |
| Active repo / merge | `bingattban/Qwen-Altmam`, current working base `main` | Old `bingattban2` and early 90-action / no-test reports are historical snapshots. |
| Source-of-truth check | `build/verify-phase00-02-gates.py` and regression `build/test-phase00-02-gates.py` | Both Windows workflow preflights fail if baseline/permission/encryption/build-shape invariants drift. |

The original `PHASE00_EVIDENCE.md`, `AS_IS_ARCHITECTURE.md`,
`REQUIREMENTS_TRACEABILITY.md` and dated project ledger retain **historical**
2026-10-23 observations for traceability. Their figures (four projects,
90 actions, no compiled tests) are **not** the present repository state.
Do not re-label their historic results as live measurements.

## 01 — Solution, tests-first Windows CI, four deliverables

- Actual CI files are **only** `.github/workflows/windows-ci.yml`
  (Windows Core Build) and `.github/workflows/windows-release.yml`
  (Windows Release Packages); both trigger on PR and push to `main`.
- Core: manifests, role/route/CSP and UI regressions, one Release solution
  build, then real independent SQLCipher integration + authorization/unit
  + full C# suite + Windows WebView2 automated checks.
- Release: independent preflight, real solution build + tests, then
  `needs: build-test` gates both `win-x86` and `win-x64` packaging jobs;
  each produces **Portable ZIP + Installer EXE** with actual SHA-256 evidence.
  A separate four-customer-package digest/provenance check must pass.
- NuGet dependency caching is allowed, but compiled outputs/test results
  cannot be treated as cached evidence. PR head must match both successful
  workflows and complete artifacts before merge. Repeat both on merged main.
- The live 00–02 guard and its negative-path regression are now required
  in **both** Windows preflights.

**External acceptance still missing:** branch protection/ruleset enforcement
must be confirmed by the repository owner; green hosted CI does not replace
owner approval of physical Windows clean-install/offline usability or signing.

## 02 — Encrypted persistence and temporal integrity

- `AlTamamDbContext` is pinned to **schema V012**, shipped migration
  resources V001–V012, and the existing owner-approved
  `SQLitePCLRaw.bundle_e_sqlcipher 2.1.10`. SQLCipher provider replacement
  and rekeying are **not** authorized.
- The database layer enables SQLCipher, WAL and foreign keys; migration
  execution uses ordered transactions, SHA-256-pinned migrations and
  verified encrypted pre-upgrade rescue snapshots.
- New test-first regression: `SchemaMigrationRunnerTests` requires V002+
  to carry their true recorded SHA pins, rejects a missing/renamed historic
  row and prevents new migrations when prior history is incomplete. V001
  remains deliberately unpinned because it bootstraps before SHA storage.
- New independent SQLCipher integration scenario checks V012 historic
  automatic-attendance outcome counts stay **NULL/unknown**, not made-up
  zeros, and refuses negative outcome counts. It uses synthetic data only.
- Both Core and Release must execute the **actual** Phase 02 independent
  SQLCipher integration suite; zero-tests-executed is an explicit failure.

**Independent blockers (unchanged):** a second clean offline Windows
computer/account, genuine multi-machine encrypted recovery, power-loss and
disk-full interruption, complete physical install/upgrade test and native
atomic restore approval. `RestoreDatabaseAtomically` remains **DISABLED**.
This work does **not** alter permissions, migration SQL files, data history,
the DPAPI key format or the owner's database provider choice.

## Evidence before marking DONE

This batch is a **candidate** until both Windows workflows on the FINAL PR
HEAD and the exact four packages/checksums/provenance succeed. Record final PR
head and merged-main SHA with run URLs after completion; do not copy old pass
figures or assert physical UAT. This record is a live scope/implementation map,
not a signed Phase 15 GO decision.
