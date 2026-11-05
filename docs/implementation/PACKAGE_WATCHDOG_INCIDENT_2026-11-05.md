# Windows Package x64 watchdog and failure evidence (2026-11-05)

Baseline: main 59382f615b60795a234a23c65d9088e2c065401c. Failed postmerge run: https://github.com/bingattban/Qwen-Altmam/actions/runs/42048029792

The 64-bit package acceptance step was entered at 15:05:21 UTC and did not report completion. Release packaging ended with failure at 15:49:45 UTC; both Core and Release full tests and x86 packaging were successful, independent four-artifact SHA-256 was skipped due to absent x64 outputs.

Observed source defect: after a child process exceeded a bounded WaitForExit timeout, the old script used Kill(true) then an unbounded parameterless WaitForExit(), which can stall cleanup indefinitely; the packaging workflow had no explicit job timeout. Exact failed subprocess could not be verified because GitHub returned BlobNotFound for the failed job logs. Treat this as a proven watchdog flaw, not a proven unique root cause.

Test-first: native Windows process tests in build/test-package-process-watchdog.ps1 exercise real subprocess exit 0, nonzero exit, 30-second hang with a one-second process timeout, and missing files. They run as new mandatory preflight checks in both Windows Core and Windows Release. The tests were authored before changing the acceptance driver; no local Windows runtime was available for execution before push, so no pre-fix red run is claimed.

Fix: route the real package acceptance subprocesses through a helper with bounded 10-second post-kill reap, diagnostics and failure on every timeout or nonzero exit; cap each package job at 30 minutes. Do not disable or shorten existing functional WebView2/SQLCipher, GUI, route, Portable/Installer, install/uninstall/reinstall, or preservation acceptance tests. No application/runtime, database, migrations, identity, history, roles, owner policy or release publishing changes.

Gate: merge only after both parallel Windows workflows on identical current PR SHA succeed, including real four x86/x64 Portable/Installer outputs and independently verified SHA256. On repeat failure, inspect native trace and subprocess START diagnostics; never claim timeout as PASS. Physical independent Windows UAT remains deferred until improvements are complete.
