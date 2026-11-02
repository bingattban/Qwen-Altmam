# PHASE 15 — Independent Windows final acceptance checklist

This checklist is an evidence collection form, **not evidence that tests ran**.
Default on every row: **NOT EXECUTED**. The GitHub-hosted Windows E2E,
real package smoke and synthetic SQLCipher suites **do not substitute** for
independent clean OFFLINE Windows operators. Never amend past CI timestamps.

## Freeze and integrity (before powering the test VM)

- Repository: `bingattban/Qwen-Altmam`.
- Immutable tested source HEAD: **TO RECORD**.
- Tested checkout SHA (PR synthetic merge vs main commit): **TO RECORD**.
- Core/Release run IDs and conclusions, exactly matching candidate: **TO RECORD**.
- Four package filenames and SHA-256 checked locally with
  `Get-FileHash -Algorithm SHA256 -LiteralPath <file>`: **TO RECORD**.
- Windows version/build/architecture, CPU/RAM, disk space, DPI,
  offline state proof, UTC+local time, screen recording link: **TO RECORD**.
- Current candidate baseline and known blockers:
  `docs/release/GO_NO_GO.md`.

## Test evidence matrix (fill each independently)

| Case | Test action and pass criterion | Status |
|---|---|---|
| x86 Portable on real 32-bit Windows | Extract full ZIP, run EXE directly offline, first-use WPF + WebView2 + SQLCipher; no normal-use elevation or adjacent database | NOT EXECUTED |
| x86 Installer on real 32-bit Windows | Install/uninstall/reinstall, shortcuts, SQLCipher + first-use, preserving per-user AppData/backups | NOT EXECUTED |
| x64 Portable on clean 64-bit Windows | Direct EXE, move entire folder, same data path and account, local runtime/fonts and real UI | NOT EXECUTED |
| x64 Installer on clean 64-bit Windows | Setup/login/attendance/report+XLSX/backup, uninstall and reinstall with exact data preservation | NOT EXECUTED |
| True older→newer upgrade | Two independently hashed versions; same historical data and key before/after, schedule/retention preserved | NOT EXECUTED |
| Missing WebView2 while offline | Start without Evergreen preinstalled; bundled signed local standalone runtime works; no CDN/request | NOT EXECUTED |
| Main four screens/SECTION 07 | Walk every 69 current route/embedded modal, report `screen_025`, Add Person `screen_070`, auto-prepared `screen_002`, no `screen_020` or duplicate report | NOT EXECUTED |
| Roles and immutable actor | Founder optionally named, never linked; admin1/admin2 supervisor denied/allowed exact commands, historical actor username + roster full name after account/person rename | NOT EXECUTED |
| Archived same-person vs different-person | SAME person return keeps original `PersonId`, DIFFERENT approved human reusing dismissed number gets new `PersonId`; no mixed attendance/report/audit/Excel | NOT EXECUTED |
| Attendance and monthly | 08:00 automatic, independent 20:00 lock, site and status totals, fallback, transfer after committed attendance, current vs archive, 31-day frozen/scroll | NOT EXECUTED |
| E: missing/low space/wrong key | Arabic error, explicitly reselect folder, no fallback or lost backup, tampered replays rejected | NOT EXECUTED |
| Real power interruption | Safe, recorded rescue and restoration rollback; no live data loss; physically distinct recovery environment | NOT EXECUTED |
| Other Windows account/PC recovery | Independent encrypted portable key material, distinct Windows login and verified cipher/history equality | NOT EXECUTED |
| ≥10,000 active people multi-year | Real GUI 31-day grid, all-status counters, 2+ years attendance, export cancel/memory/latency/throughput, historical equality | NOT EXECUTED |
| Keyboard/Arabic/accessibility | RTL, Arabic/Western/Persian digits, Esc/Tab/readable tables, 1024×768, 1366×768, high-DPI screen reader | NOT EXECUTED |
| P0/P1 triage/owner approval | No unresolved critical/high impact issue, no misleading enabled restore; signed owner decision & verifier identity | NOT EXECUTED |

## Procedure and evidence capture

- Use **synthetic** personnel only. Keep internet interfaces disabled on
  clean VMs when proving offline. Keep the 32-bit and 64-bit test results
  **separate**, not one generic screenshot.
- For each scenario append: **test timestamp**, device+Windows build,
  source SHA, downloaded package name+measured SHA-256,
  precise UI/DB steps, **expected vs actual**, SQLCipher/Report/Excel
  captured evidence, logs, result (`PASS/FAIL/NOT EXECUTED`),
  independent tester and owner notes.
- Release failure severity P0/P1/P2/P3; if any P0/P1 remains open, final
  signoff is **NO-GO**. An unresolved owner conflict remains pending,
  never silently assumed.
- Do not change or invoke the disabled destructive restore on a real
  personnel database to manufacture a passing recovery case.
- A signed release requires a **new exact tested main SHA**, all required
  execution evidence, four matching package hashes, independent reviewer
  confirmation and the owner's explicit final approval.

**Independent verifier:** PENDING.

**Owner final GO decision and date:** PENDING — **NO-GO**.
