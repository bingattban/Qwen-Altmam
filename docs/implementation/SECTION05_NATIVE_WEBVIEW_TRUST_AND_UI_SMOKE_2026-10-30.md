# Section 05.06–05.15 follow-up — native exact origin + packaged WebView2 UI probe

**Phase ownership:** Phase 04 WebView2/C# boundary, Phase 01 Windows CI; Phases 13–15 verification. No new phase or irreversible domain policy. The baseline for this stacked branch is PR #144 head \`8c0cd8dd6976948f440418e11d57ba21f57f4708\`; results from that ancestor are NOT proof for the current branch.

## Specific compatible defect found (05.08)

The already-tested C# bridge \`WebView2BridgeDispatcher.IsAllowedOrigin\` rejects credentials and non-default HTTPS ports; the WPF host's private \`IsLocalVirtualHost\` previously compared only scheme and host. This allowed unusual URLs with matching host but different port/credentials through the *native navigation and resource filter* despite the stricter bridge authentication. The host now **delegates to the same canonical, already-tested predicate** for navigation, frames, popups and WebResourceRequested, so the layers cannot drift. This is a denial-only security hardening; no new hosts or remote fetches allowed.

## Executed implementation and validation (05.06 / 05.08 / 05.14)

The old \`--webview-selfcheck\` only checked HTML navigation and the page title. It now executes \`assets/js/webview-runtime-probe.js\`, an actual local bundled, read-only JavaScript file inside real Windows WebView2, and requires its exact PASS verdict after checking:
- pinned HTTPS virtual origin and genuine computed RTL;
- all four real navigation elements and the real app viewport;
- loaded \`AltamamBridge.invoke\` client with protocol version 1;
- real CSS stylesheet from the local pinned host;
- absence of inline \`<script>\` and external scripts or stylesheet links.

The same probe is exercised by \`build/test-webview-runtime-probe.js\`: nine VM executions including eight negative fixtures (bad port, non-RTL, absent CSS/bridge/nav/viewport, inline script, external asset). Its source contract also checks the native host exact-origin delegation, all canonical HTML route files for local JS/CSS and strict CSP, and verifies \`RestoreDatabaseAtomically\` remains **unwired** while read-only \`PreviewRestore\` stays live. BOTH independent Windows workflows execute this test before native build; the previous package acceptance suite (PR #144) executes \`--webview-selfcheck\` from each actual x86/x64 Portable and Installer plus reinstall. On any failure Release blocks package approval; it does not conceal missing tests.

**Scope and contracts:** No business DTO, SQLCipher migration, scheduler, domain permission, historical identity, attendance, audit operation or restore activation changed. The only runtime error surfaced is a nonzero selfcheck exit when an asset or trust boundary is invalid; test mode never invokes the business bridge or writes records. On failure nothing is promoted or merged; database transaction and rollback semantics remain unchanged.

## Mandatory outstanding gates

- **05.06:** Native end-to-end interactive flows (user input, unauthorized roles, conflicting concurrent edits, storage failures and interrupted writes) still require a real Windows UI driver and physical acceptance where applicable; prior unit/integration tests cannot be substituted for these.
- **05.07 / 05.12:** Merge only after BOTH exact-head Windows Core and Windows Release runs succeeded, two architecture matrix jobs generated four distinct Portable/Installer outputs plus verified SHA-256 manifests and metadata; recheck the merge SHA in main. Never recycle ancestor hashes as final evidence. Branch protection is the owner's separate repository configuration.
- **05.10 — explicit conflict requiring owner decision before modification:** The latest Section 05.10 directive mandates search inputs **170 × 35 CSS pixels**, but the simultaneously open UI PR #143 explicitly asserts search fields **must not be capped at 170 px** and implements wider controls. Alternative A: enforce 170×35 in every canonical search box (contract-compliant but reduces space for long names and breaks PR #143's present design test). Alternative B: retain wider search fields (more readable text but violates the literal 05.10 dimensions). Neither alternative changes the historical database, permissions or security policy; potential UI readability and regression-test effects differ. This follow-up does NOT silently choose either. Owner approval is required before modifying conflicting CSS/design tests.
- **05.14:** Actual disconnected clean PCs (real x86 and x64), operator interactions, AppData continuity across physical upgrades, inaccessible/offline dependency simulation and native cross-PC recovery are still OPEN. CI WebView2 smoke is explicitly NOT physical UAT.
- **05.15:** Destructive \`RestoreDatabaseAtomically\` remains disabled. The read-only preview and staging prerequisites are not a completed cutover, and cannot be enabled until privileged reauthentication, native confirmation, scheduler quiescence, rollback and interruption/cross-PC acceptance are proven.

**Note:** This document is implementation scope and gap evidence; do not claim final acceptance or exact-head results until the new PR CI is green.
