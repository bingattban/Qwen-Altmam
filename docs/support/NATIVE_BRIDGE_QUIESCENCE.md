# Native bridge stop-and-drain — offline restore prerequisite

## Added

The `NativeOfflineOperationDrain` admission barrier in
`src/AlTamam.Application/Bridge/` implements a **linearizable** transition:
`live → stopping → drained`. Each incoming browser message and native idle
activity must obtain a lease before performing any asynchronous SQLCipher
operation. Calling `StopAndDrainAsync` immediately denies all subsequent
leases and returns a task which completes only after every earlier lease is
disposed. Concurrent stops share the same completion, and duplicate lease
disposal cannot underflow the in-flight count.

The actual `DesktopBridgeGateway` now owns this barrier. It offers the
native-only `StopReceivingAndDrainAsync` which first stops admissions, waits
for all existing browser messages/idle updates to finish, then detaches the
WebView2 message handler. Once stopped, reattachment is rejected. Unsolicited
events are no longer emitted after stopping. The existing bridge protocol,
session validation and request handling remain otherwise unchanged.

Tests in `tests/AlTamam.Tests/Bridge/NativeOfflineOperationDrainTests.cs`
exercise queued asynchronous work, multiple concurrent requests, duplicate
disposal, stop-idempotence and post-stop refusal on the native .NET runtime.
No synthetic scenario triggers real database restoration.

## Fail-closed boundaries not yet satisfied

**This work is a prerequisite, not an activated restore workflow.**

1. No Windows native restore UI or shutdown button calls this method yet.
2. The bridge drain **does not cancel** running transactional operations:
   it *waits* for completion rather than interrupting an in-progress write.
   A future native coordinator must provide user cancellation before beginning
   shutdown, and fail closed on hung/failed drains rather than proceeding.
3. The daily attendance and automatic backup schedulers still require awaited
   `DisposeAsync` and lifetime coordination, followed by secure disposal of
   all DB contexts, connection pools, and confirmation of no SQLite sidecars.
4. The original app single-instance mutex must be retained throughout the
   cutover; native GeneralAdministration reauthentication, user confirmation,
   verified stage + rescue + rollback, power-loss/disk-full and cross-PC tests
   must all pass before enabling `RestoreDatabaseAtomically`.
5. The manual clean-machine acceptance matrix remains separately required.
   Automated package-preservation tests are NOT proof of live restore or
   independent customer-machine acceptance.

No changes to attendance, personnel identity/history, SQLCipher schema, old
data files or Portable/Installer AppData location are made in this batch.
