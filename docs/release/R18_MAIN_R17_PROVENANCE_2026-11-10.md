# R18 — Frozen R17 main candidate provenance and open field-acceptance gates
Record date: 2026-11-10. Scope: release evidence and safe CI inventory validation, **not** changing or expanding any production feature.

## Exactly verified GitHub CI candidate

- Repository: `bingattban/Qwen-Altmam`; R17 merged PR #268, immutable main source `859cc9c7e889d73f0f23debd0235c3c912f397c8`.
- [Core 42698618350](https://github.com/bingattban/Qwen-Altmam/actions/runs/42698618350) — **SUCCESS**, including actual Windows native integration and WebView2 smoke.
- [Release 42698618694](https://github.com/bingattban/Qwen-Altmam/actions/runs/42698618694) — **SUCCESS**, including full tests before package creation, both x86/x64 packaging jobs and independent four-package file rehash.
- Version `0.0.1135-ci`. Original Release upload: `AlTamam-Phase15-FourPackage-Provenance-0.0.1135-ci`, artifact ID `13712393096`, created 2026-11-09. The exact inventory was retrieved from the archived ZIP's `four-package-inventory.json`. It was preserved with **only an additional Core run ID** in [the frozen JSON snapshot](../../artifacts/R17_MAIN_AUDITED_CANDIDATE_2026-11-09.json). Do not confuse the digest of the enclosing GitHub artifact ZIP with individual customer binary digests.
- The original failing R16 main Release [42670076098](https://github.com/bingattban/Qwen-Altmam/actions/runs/42670076098) retains its historical **failure** (internal GitHub server error at package matrix scheduling). A separate, later R17 main SHA succeeded; that did not retroactively change R16's run outcome.

## Four authentic customer files

| Architecture | Distribution | Exact customer file | Bytes | SHA-256 |
|---|---|---|---:|---|
| Windows 32-bit | Portable | `AlTamamDaily_0.0.1135-ci_Portable_win-x86.zip` | 253746971 | `aaca9136c1181ab40d491b3b193931c28bcc7607bc8e2bfe415112cd4c5a3f15` |
| Windows 32-bit | Installer | `AlTamamDaily_0.0.1135-ci_Setup_win-x86.exe` | 235043511 | `9d5e3a9ac380237a42540daf7815ec5ac213e186ff32fced1e095b2865ec77a5` |
| Windows 64-bit | Portable | `AlTamamDaily_0.0.1135-ci_Portable_win-x64.zip` | 282676889 | `ede4296d5e96a347063dcdb317be4f5c47f58b5a076c29767d1d737e99cbc324` |
| Windows 64-bit | Installer | `AlTamamDaily_0.0.1135-ci_Setup_win-x64.exe` | 263063937 | `16fe3623685d5011b770d81b7de0f5ab2d7ba4a5765d04348a68cca40a3ad594` |

These are the actual customer-file SHA-256 digests from the successful Release audit, NOT hashes of Github's artifact ZIP wrappers. Rehash files separately after downloading them onto independent Windows machines.

## Guard and limitations

- R18 adds `build/validate-frozen-candidate.py` and negative-path tests that reject absent/mismatched source SHA, duplicate or missing customer files, bad architecture, name, length or SHA format, and falsely claimed production signoff or physical UAT. A schema test of a frozen audit **cannot independently rehash 250 MB binaries**: the original Release audit is the source of the binary hashes.
- The test is executed in both Windows Core and Windows Release preflight; tests must run before producing four new packages, then all four must pass a same-run independent SHA-256 audit before any PR is merged. The R18 implementation SHA will be DIFFERENT from the frozen R17 SHA, so keep historical evidence labeled as R17 rather than attributing 0.0.1135 to R18.
- No database, encryption, WPF, authentication, scheduler, role or attendance behavior changes.

## Mandatory field cases, still NOT EXECUTED

WIN-01..WIN-10 in [the acceptance matrix](PHASE15_ACCEPTANCE_MATRIX.md) remain open, including both Portable and Installer on truly disconnected clean x86/x64 systems, cold WebView2, live RTL/keyboard/user roles/attendance/Excel, real two-version upgrade, physical power-loss and disk-full recovery, cross-PC and cross-account portable encrypted key recovery, E: remount/error path, and measured 10,000-person GUI/Excel multi-year workload.

To acquire evidence safely, use `build/verify-clean-offline-candidate.ps1` on an **independent disposable Windows user/VM** with network adapters disabled, with authentic package binaries, exact frozen source SHA and Release run ID. This is an automated subset ONLY. Never feed it an existing production profile or real personnel records. Actual physical disruption and recovery need an authorized independent operator and recorded logs, not CI fault-injection surrogates.

**Decision: NO-GO.** No final production tag, migration policy change, bypass of disabled WebView2 restore command, pre-login key-import ceremony or GeneralAdmin ownership assumption without explicit owner decisions and independent real Windows acceptance. Owner signoff: **PENDING**.
