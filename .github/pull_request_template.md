# Required evidence for any corrective PR (existing PHASE 00–15)

## Scope / authority / conflicts
- Existing phase and corrective work item:
- Full starting main SHA:
- Approved owner rule(s) versus old wording:
- Conflicts register (ID / old rule / latest approved rule / file & function / data impact / security and performance / owner decision):
- If material policy remains unresolved, STOP this PR (do not infer approval).

## Inventory and real call chain
- Classification: VERIFIED DEFECT / COVERAGE GAP / INTENTIONALLY DISABLED / STALE CANDIDATE / UNDECIDED POLICY
- File/function/line evidence:
- UI + JS → bridge action/DTO → native dispatcher + role → canonical service → SQLCipher persistence → output/audit:
- Actual runtime, test, contract and dynamic callers:

## Test FIRST: baseline RED
- [ ] Executable regression using required real boundary; mocked JS/static text alone NOT sufficient for native defects.
- Failing test-only SHA (before modifying production code):
- Exact command, environment, CI URL, test count and FAIL assertion:
- If not executed: mark **NOT EXECUTED**, explain missing harness and STOP defect acceptance.
- Negative-path matrix: invalid input / forbidden role / revision conflict / replay / cancellation / rollback / audit immutability:

## Minimal implementation and verification
- Owning layer and changed paths:
- Server-side roles; validation and error code:
- Expected revision; operationId; idempotency token:
- SQLCipher transaction/rollback and data-history preservation:
- Audit event, actor snapshot and UI effects:
- [ ] Identical original regression GREEN on corrected SHA; URL, test count, trace:
- [ ] Applicable real SQLCipher, xUnit, JS, Windows WPF/WebView2 E2E, security and manifest/schema checks successful:
- Local Release compile: PASS or NOT EXECUTED LOCALLY (reason):
- Output parity and measured before/after (if applicable):
- [ ] No skipped/faked/xfail/ignored/weakened mandatory checks or mocked substitutes.

## Protected release evidence
- Final PR head SHA / up-to-date main / conflict/review status:
- Core workflow run ID + SHA + success:
- Release workflow run ID + SHA + success; ALL tests before packaging:
- x86 Portable ZIP and Installer EXE artifact names:
- x64 Portable ZIP and Installer EXE artifact names:
- Independent SHA256 of four real customer binaries and metadata:
- [ ] Seven required protected status checks conclusively SUCCESS; no pending failure.
- Merge commit SHA + Core and Release post-merge main run IDs:
- Manual independent offline Windows UAT: OWNER-DEFERRED, not PASS.
- Final production GO/NO-GO: NO-GO unless separate owner authorization.

Process: [TEST_FIRST_REPAIR_PROCESS_2026-11-05.md](../docs/implementation/TEST_FIRST_REPAIR_PROCESS_2026-11-05.md). Docs-only PRs must say N/A for RED/GREEN application tests, without claiming production behavior is fixed.
