# Phase 13 — Windows security, performance and Arabic UI QA

**Starting baseline:** `bingattban/Qwen-Altmam`, `main` `d1d6a7768d1e746e256c6d2af8845007763a5245`.
**Evidence of final phase completion:** **NOT ESTABLISHED**. This increment adds
new real-Windows checks and machine-readable evidence; it does not change
applicable acceptance criteria. The **tested HEAD** and workflow-run ID must
come from the downloadable `phase13-*-windows` artifacts, **not** this file.
For PRs, `actualCheckoutSha` is typically GitHub's synthetic merge SHA,
while `sourceHeadSha` records the PR head separately.

## Execution matrix — never infer PASS from source presence

| 13.x | Existing and added verification | Status before new CI completes | Remaining evidence |
|---|---|---|---|
| 13.01 | Windows compiled WPF + WebView2 opens all 69 manifest HTML paths; new synthetic SQLCipher native bridge exercises GA, Admin and Supervisor. screen_020 absent, screen_025/report and screen_002 present | NOT EXECUTED at new HEAD | Every active screen's actual workflows for each role; no inert control audit |
| 13.02 | RTL/modal/focus/Escape real-WebView2; new Arabic/Western/Persian-digit normalization and local CSP check | NOT EXECUTED at new HEAD | Screenshots/assistive technology, 1024x768, 1366x768, high DPI; **170x35 conflicts with present 200–360px responsive search**, owner decision pending |
| 13.03–13.04 | Existing monthly grid and table regression suites | NOT EXECUTED at new HEAD | Real Windows 31-day horizontal/frozen-column/day-adjacent viewport tests for site/month/10,000 rows |
| 13.05 | Real Windows synthetic three-role bridge authorization probe; existing SQLCipher account and attendance integration tests | NOT EXECUTED at new HEAD | Full physical sequential first-run/return/replacement/08:00/20:00/movement/report/closed-day journey |
| 13.06 | Existing hostile-payload, origin and replay C# tests; real WebView2 strict-CSP/browser-bridge probe | NOT EXECUTED at new HEAD | Full penetration suite, final policy review on shipped binaries |
| 13.07 | Existing measured 10,000-person search xUnit + runner metadata; tooling/perf runner report | NOT EXECUTED at new HEAD | 10,000 **active** people with multi-year 31-day calendar, cancellation/throughput, historical equivalence benchmark |
| 13.08 | Existing idempotence/competing-write tests | NOT EXECUTED at new HEAD | Real power-loss simulation on separate physical equipment, UI latency |
| 13.09 | Existing 300-second/session isolation bridge tests | NOT EXECUTED at new HEAD | Real WPF inactivity lock while export dialog/modal is in focus |
| 13.10 | .NET vulnerability scan, new CycloneDX SBOM from resolved NuGet dependencies; existing manifest/CSP static guards | NOT EXECUTED at new HEAD | Human license audit and captured **offline** Windows network trace |
| 13.11 | Source SHA, checkout SHA, Windows runner, run ID stored in artifacts; omitted tests explicitly disclosed | NOT EXECUTED at new HEAD | Cross-check artifacts and signed final review |
| 13.12 | Real Windows E2E core/release workflow required before packaging | NOT EXECUTED at new HEAD | Full acceptance NOT EXECUTED; new automated tests alone are insufficient |

### Boundaries
- No production account, database or WebView2 profile is used by the synthetic E2E probe.
- The existing 69-route navigation smoke measures **successful same-origin resource loading**, not success of every business control or every role transition.
- Benchmark times are hardware-specific observations, **not contractual latency guarantees**. Never substitute a single 10k-person search for 10k-person multi-year calendar/export measurement.
- Export integrity, physical disk interruption, cross-PC restoration and clean-machine accessibility acceptance remain independently gated.
- Phase 12 restore activation remains **DISABLED**.
- Until physical/assistive-tech and full role-screen acceptance is documented, **PHASE 13 = PARTIAL / NO-GO**.

### Reproduce
```powershell
dotnet restore AlTamam.slnx
dotnet build AlTamam.slnx -c Release --no-restore
dotnet test tests/AlTamam.Tests/AlTamam.Tests.csproj -c Release --no-build --no-restore
$env:ALTAMAM_PHASE13_EVIDENCE_PATH = "$PWD/Phase13Evidence/windows-e2e.json"
pwsh -NoProfile -File ./build/test-webview-ui-e2e.ps1
pwsh -NoProfile -File ./tooling/perf/Collect-Phase13WindowsFacts.ps1 -OutputPath Phase13Evidence/runner-facts.json
pwsh -NoProfile -File ./tests/AlTamam.WindowsE2E/Validate-Phase13Evidence.ps1 -EvidenceDir Phase13Evidence
```
