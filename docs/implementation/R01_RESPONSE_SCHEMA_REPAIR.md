# R01 response schema repair — verified defect

Base: `667a44effa691390155520bc4d837b9b81c5d3a0` (merged #249).
Scope: original phases 04/13, R01 4.8. No business policy, database migration,
production protocol implementation or security permission changes.

## Reproduction before correction

`BridgeResponse` in `src/AlTamam.Contracts/Bridge/BridgeMessages.cs` serializes
`code`, `messageAr`, validation/conflict metadata, audit ID and expected/actual
versions. `$defs.response` in `bridge-v1.schema.json` used
`additionalProperties: false` but omitted those properties. A consumer actually
validating the published schema therefore rejects legitimate native replies.

Local test-first commit: `9cbdcdfc95e61f93e2b6ed52685f31cd280d9243`.
Equivalent test-only source is retained remotely at
`4141c10ebc758fbe70614ad46c8f1154b7c4fa6f`; the observed RED run was local. No schema changes in that
commit. The test uses real provision/login, `BridgeHandlerCatalog.Create`, the
actual dispatcher and temporary encrypted SQLCipher. Every response from the
existing three authenticated boundary tests is serialized using the same
`JsonSerializerDefaults.Web` as DesktopBridgeGateway and checked against the
published response schema.

Local RED: 3 failed, 0 passed, 0 skipped. Exact failure:
`$: undocumented serialized field code`. This is a real contract discrepancy,
not a missing dependency. Initial parallel MSBuild was unsupported by the local
socket policy; the executable command below uses a single MSBuild node and
successfully executes native tests. No escalation or security bypass was used.

## Minimal correction and extended coverage

Declare all existing envelope properties and structured invalidation fields;
retain `additionalProperties: false`. Require the always-serialized canonical
`code` and Arabic message. Add typed data projections for both status actions
and both search actions, with stable identity, items/count and nullable envelope
metadata preserved. Do not rename legacy aliases or alter runtime serialization.

The test validator implements only the schema vocabulary used by these response
projections and fails on unsupported keywords. It is a scoped test validator,
not a general JSON Schema engine. Native negative controls deliberately corrupt
an already-validated real reply to prove array/object drift, string counts and
missing PersonId cannot silently pass. Mutation receipt/replay, stale conflicts,
Supervisor denial and expired sessions remain real native tests. Active/empty/
deactivated status and disabled-restore suites also validate their actual wire
responses. Real-login read checks cover server role/navigation metadata,
dashboard strength, settings, 08:00/20:00/16:00 policy separation and search;
reads must preserve business evidence. The second role is authenticated only
after logout; no forged role is used.

Local GREEN after the envelope correction: same 3 tests passed (0 failed/skipped).
Expanded intermediate group: 4 passed (0 failed/skipped). Broader final R01/envelope
run: 22 passed, 0 failed, 0 skipped; TRX r01-consolidated-final.trx. A final fail-closed
schema lookup adjustment also passed the affected five-test group (5 passed, 0 failed,
0 skipped; TRX r01-schema-final.trx). Windows
Core/Release results remain separate; local tests do not prove all manifest actions.

Executable local command (SDK 10.0.403):

```bash
dotnet test tests/AlTamam.Tests/AlTamam.Tests.csproj -c Release --no-restore \
  --filter 'FullyQualifiedName~R01|FullyQualifiedName~Section08ResponseEnvelope' \
  --verbosity minimal -m:1 /p:BuildInParallel=false /p:UseSharedCompilation=false \
  /nodeReuse:false --logger 'trx;LogFileName=r01-consolidated.trx'
```

The Windows workflows run the full normal suite, then real WebView2 consumers;
Release packages only after tests. Four x86/x64 Portable/Installer outputs and
independent SHA256 provenance on the exact final head remain merge prerequisites.
Independent manual Windows acceptance remains owner-deferred, NOT EXECUTED.
No performance optimization or measured speedup is claimed.

## Completion boundary

This closes the reproduced envelope-schema discrepancy after successful final
validation; it does not by itself close R01. An exhaustive meaningful native
scenario for every enabled UI-used action, all response data schemas/snapshots,
remaining consumer failure isolation/correction/dashboard flows and full
filtered/paged/historical Excel parity are still required. Keep destructive
restore explicitly disabled and do not count a static action inventory or a
mocked role matrix as those missing executions.
