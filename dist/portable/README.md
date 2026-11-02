# Portable output — Phase 14
The actual x86 and x64 Portable ZIPs are **built on Windows by GitHub Actions**,
after all required tests pass. They are staged here at runtime, not committed as
unverified executable files. Download the four independent ZIP/EXE workflow
artifacts and their architecture-specific SHA-256 manifests.

Portable is extracted and launched using `AlTamam.DesktopHost.exe`. It
shares exactly `%LOCALAPPDATA%\\AlTamam` with the Installer; moving this
folder or extracting it elsewhere does not move user data or a DPAPI key.
