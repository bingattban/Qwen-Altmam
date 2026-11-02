# Installer output — Phase 14
The x86/x64 Inno Setup EXEs are built and verified from the **same tested
checkout** as the Portable ZIPs in the Windows Release architecture matrix.
The generated files are staged here in CI after installation/uninstallation
preservation checks. No EXE is checked into Git.

Uninstall never removes `%LOCALAPPDATA%\\AlTamam` or external backups.
No signature is claimed without an owner-provided certificate and actual
signature verification. Independently verify each SHA-256 manifest.
