# SECTION 05 — Mandatory implementation rules: evidence and remaining gaps (2026-10-30)

This is an **audit of the owner's approved SECTION 05 (05.01–05.05)**, not a new phase, a new section, or permission to alter an established business policy.

## Examined baseline (05.03)

- Repository: `bingattban2/Qwen-Altmam`; main before this patch: `ee18d46f5b05b7fd67e883b0228afe8a93afe194`.
- Both Windows workflows on that **baseline** were completed successfully; full Release C# test log reported **922 passed / 0 failed / 0 skipped**. This is not proof of physical offline-machine acceptance.
- Inspected: the two GitHub Windows workflows and their previous results; canonical 69-route `screen-manifest.json`; 101-action `action-manifest.json`; shared UI JavaScript and independently loaded route HTML; existing UI regression tests; SQLCipher migrations `V001`–`V011` and existing service boundaries. This patch does not change database state or schema.

## 05.01 — One approved plan, test gates

Keep approved **Phases 00–15** and **Sections 01–11**. Requirements remain owned by Phase 00, CI by Phase 01, C#/WebView2 by Phase 04, Windows tests/build/release by Phases 13–15, and individual behavior by its existing owning phase. Historical documents mentioning an earlier "Phase 16" are **not** adopted as new governance and have not been silently rewritten.

**Confirmed CI defect:** in BOTH Windows Core and Windows Release preflight, three JavaScript regressions were executed successively but the PowerShell exit code was inspected only after the third. A passing third test could conceal a failing security/bridge test. This patch adds immediate failure checks after every regression and `build/test-ci-exit-guards.py`, which detects missing or duplicate scripts and missing per-command exit-code guards. New JS tests must be enrolled in **both** Windows paths. The Release build/test-before-both-packaging dependency is unchanged.

**Unresolved organizational enforcement:** main branch protection was reported disabled on the examined baseline. Green CI is an operational requirement; the repository owner must additionally configure required branch checks to make it a GitHub-enforced restriction. No repository administration settings are changed here.

## 05.02 — Conflicts and owner's policy authority

No new business, history, security, authorization, or retention policy is chosen or changed. The pre-existing unrelated PR #114 concerning mandatory operation IDs is **not merged, rebased, or resolved** by this patch. If further review discovers a new material conflict, record both alternatives and their historical/security/permission effects and obtain owner choice *before* changing the conflicting behavior.

## 05.04 — Exact impact contract for this patch

| Topic | Contract |
| --- | --- |
| Owning layer | `.github/workflows/windows-build.yml`, `windows-release.yml` (Phase 01 CI and Phases 13–15 gates); `build/test-ci-exit-guards.py` (regression verification); `src/AlTamam.WebUI/assets/js/screen-module.js` (shared route presentation) |
| DTO / bridge action | **None added or altered.** Existing `action-manifest.json`, bridge payloads, `screen-manifest.json`, server handlers and transport are unchanged |
| Input validation | CI validator compares all checked-in `build/test-*.js` with both workflows, demands an immediate PowerShell failure guard after every JS test and after the validator itself, and self-tests a masked-exit example |
| RBAC / security | **No permission widening.** All mutation authorization remains in C#; CI explicitly verifies existing bridge-intent and audit-RBAC tests without permitting masked failures |
| Error handling | A failing Node test or Python validator aborts its Windows preflight job. The route script corrects misleading return-link labels and lets Escape navigate from both hash and hashless links to the unchanged main entrypoint; no application error codes are changed |
| Transaction boundary / rollback | No data mutation, SQL transaction, migration, history or archive change. Windows Release still blocks both packages until full test success. UI changes are presentation-only and revertible by reverting the feature commit |
| Tests | New `build/test-ci-exit-guards.py` includes failing and passing fixtures; `build/test-route-return-link-honesty.js` verifies all canonical HTML routes retain the shared module and tests label, href, Escape for hash/hashless destinations, and unrelated links. Both run in the Core and Release preflight |

## 05.05 — Honest UI states / unresolved functional work

- Existing `bridge-client.js` distinguishes Loading, Empty, Validation Error, Conflict, Forbidden, Success for supported read-only WebView2 routes; unsupported routes disable their fake REFRESH action and display an explicit development state.
- This patch replaces **only misleading navigation labels**, such as "تشغيل التفاعل الكامل" on links actually returning to the main UI. The original target `href`, working functional screens and server behavior are preserved; the shared Escape handler now supports both hash and hashless main-entry links.
- The standalone route skeletons are **not automatically complete** because a directory link or disabled REFRESH exists. Current static inventory still contains many placeholders beyond the four integrated standalone read-only routes. Functional completion must be measured against each screen's actual owning-phase contract, without inventing a new phase or claiming all 69 screens finished.

## Acceptance still required

1. Both Windows **Core** and **Release** jobs must succeed on the **same final PR SHA**, including the new Python and Node guard; no failed step may be masked or overridden.
2. Both 32-bit/64-bit genuine Portable and Installer packages plus SHA-256 evidence must be uploaded after tests succeed; no merge for missing artifacts.
3. Verify both workflows again on the merge SHA before treating the code as integrated.
4. Independent clean Windows/WebView2 user acceptance and owner publication approval remain separate gates, not implied by successful CI.
