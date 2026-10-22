# التمام اليومي — النواة البرمجية

This repository contains the production foundation only: `Domain`, `Contracts`, `Application`, and `Infrastructure` for an offline Windows personnel/attendance system.

It contains the **original local HTML/CSS screen layouts** in `src/AlTamam.WebUI`, restored from the earlier complete repository, while retaining the four-layer core. There is **no WPF/WebView2 executable host yet, no implemented business-action bridge, no generated employee data and no automated test project**. Existing pages preserve their original visual baseline; their data operations must be completed in the designated implementation stages. Successful compilation of the foundation does **not** mean that personnel management, authentication, attendance, transfer, or backup features are implemented or verified.

Build on Windows using .NET SDK 10:

```powershell
dotnet restore AlTamam.slnx
dotnet build AlTamam.slnx -c Release --no-restore
```

`AlTamam.Infrastructure` contains the SQLCipher connection layer, embedded relational schema, and password hashing. It must not be initialized with real records or distributed with embedded credentials. Generate encryption keys at installation/onboarding time and protect them using the Windows key store in the subsequent security stage.

The SQL schema contains only fixed application catalog entries including «غير محدد», «موجود», «إجازة», «غياب», and «إذن». It does not create an active privileged user or a known password. First-run provisioning is intentionally part of the authentication stage.

Source code is not shipped with any implementation plan, progress journals, sample data or test artifacts. Execution instructions and acceptance specifications are delivered separately.

## Existing screen assets

The canonical UI paths are `src/AlTamam.WebUI/index.html`, `assets/css/altamam.css`, `assets/js/altamam-app.js`, `screen-manifest.json` and `screens/**/index.html` (69 existing nested route pages). The original CSS and screen layout have been preserved. The main JavaScript now offers navigation only; it does not store personnel in localStorage, impersonate user roles or claim transactions have succeeded. No UI is allowed to claim secure attendance/authentication operations before Phase 04+ C# integration. Tests and execution steps live in the separate planning ZIP.

## UI routes and virtual host

The bundled WebUI retains its existing Arabic text, RTL layout, and color system.
Its 69 screen identifiers and nested folder names use unique ASCII PascalCase identifiers
(such as `Dashboard`, `InternalTransfer`, and `DismissedPersonnelArchive`).
All inline script blocks, inline event handlers, and inline styles were moved to local JS/CSS; stale links to external prompt archives were removed from the runtime screen manifest.
The canonical offline virtual host is `https://manger.adwas.example/`, declared in
`src/AlTamam.Contracts/Bridge/LocalWebUiOrigin.cs` and `action-manifest.json`.
The `.example` suffix is reserved; use `CoreWebView2HostResourceAccessKind.Deny`
when the future WPF/WebView2 host maps this origin to `src/AlTamam.WebUI`.
**The current repository contains no Windows executable host or functional C# UI bridge yet.**
