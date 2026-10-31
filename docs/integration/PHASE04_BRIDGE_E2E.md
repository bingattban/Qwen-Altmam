# Phase 04 — actual offline WebView2/SQLCipher round-trip acceptance

Owner-approved DesktopHost is the WPF host (do NOT rename it). The static
WebUI source, 69 route IDs, action manifest, and SQLCipher provider remain
unchanged. This phase only closes real bridge/route acceptance gaps.

## Real browser SQLCipher test (Windows)

`build/test-webview-ui-e2e.ps1` starts the real built WPF executable using
`--webview-selfcheck --webview-ui-e2e --webview-bridge-e2e`. The additional
bridge flag creates an isolated temporary WebView2 profile and an independently
generated random-key SQLCipher database under the Windows temporary directory.
It never reads DPAPI keys, AppData, production databases, or real personnel.

The actual HTML document invokes `AltamamBridge.invoke` via
`window.chrome.webview.postMessage`, through `DesktopBridgeGateway`,
`WebView2BridgeDispatcher`, application services, and SQLCipher. The test:
1. Reads initial unprovisioned state, creates the optional-name founder once,
   verifies an actual database site returned from SQLCipher.
2. Previews archived identity, creates a new synthetic person, reads it back by
   permanent PersonId and renders the returned Arabic name/number into an
   actual WebView2 HTML `output` element.
3. Rejects a duplicate active number, creates a roster-linked Supervisor,
   logs out, signs in as Supervisor, rejects Users management and closed-day
   override at the C# permission boundary, then sends a malformed protocol
   version and asserts its canonical ValidationError.
4. Reopens SQLCipher in native C# and verifies the exact rendered PersonId,
   account roles and presence of the real `cipher_version` provider.

The CI step fails on timeout, missing HTML output, missing SQLCipher evidence,
malformed success, privilege escalation, or any unsuccessful SQLCipher write.
Both parallel Windows Core and Release execute it after build and before the
four Portable/Installer packages.

## Existing production boundaries retained

- Only 4 main destinations: Dashboard, Monthly Attendance, Administration,
  Settings. Other screen routes hand over to their authenticated owning UI.
- screen_020 retired, screen_025 sole Report, screen_070 unique embedded
  Add Person opener, screen_002 auto-prepared, screen_069 08:00 scheduler.
- MainWindow now rejects nondefault HTTPS ports and credential-bearing URLs,
  consistent with the C# request origin checker; no outbound network allowed.
- Add Person deep links open the ONE existing personnel creation form (no new
  backend, duplicate route, or silent write).
- Backend services and immutable audit remain the authority; the existence of
  a route is not evidence that later phase features are fully implemented.
- Real clean-machine offline UAT, hardware sleep/resume, comprehensive
  accessibility checks and Phase 15 Acceptance category evidence remain
  separate open gates.

## 69-route real browser navigation and versioned request schema

The same isolated Windows WebView2 test also loads each of the 69 canonical
manifest entries by its exact virtual-host URL (including the unique embedded
screen_070 alias), allowing a legitimate same-origin standalone redirect to its
authenticated owner. It rejects missing HTML assets, duplicate/retired IDs,
external origins, or a failed navigation timeout. Node's existing route tests
remain separate from these actual browser navigations.

The machine-readable `src/AlTamam.Contracts/Schemas/bridge-v1.schema.json`
records all 103 canonical action names, required versioned JSON envelope
fields, typed operation IDs for mutating actions, typed request payload
properties for the owning workflows, and the typed native response envelope.
A dependency-free CI schema drift check runs BEFORE build in both Windows
workflows. This documents and tests wire compatibility; it does not substitute
for C# per-action authorization and validation already present in service code.
