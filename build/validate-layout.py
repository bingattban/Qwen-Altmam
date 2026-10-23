#!/usr/bin/env python3
"""Phase 15 CI gate (15.02): validate the published WPF layout BEFORE packaging.

Usage: python3 build/validate-layout.py <publish-dir> [--portable]
Fails (exit 1) unless the publish directory contains a genuine PE executable
and every required runtime asset. --portable additionally requires the bundled
webui content and forbids stray build junk (.pdb allowed only if owner opts in;
default: reject .pdb, .xml docs, obj/, *.FileList.txt).
Exit codes: 0 = pass, 1 = layout violation, 2 = directory missing.
"""
import os, struct, sys

def is_pe(path: str) -> bool:
    """Genuine Windows PE check: MZ header + e_lfanew -> 'PE\\0\\0' signature."""
    try:
        with open(path, "rb") as f:
            if f.read(2) != b"MZ":
                return False
            f.seek(0)
            head = f.read(0x40)
            if len(head) < 0x40:
                return False
            off = struct.unpack("<I", head[0x3C:0x40])[0]
            f.seek(off)
            return f.read(4) == b"PE\x00\x00"
    except OSError:
        return False

def main() -> int:
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    portable = "--portable" in sys.argv
    if not args:
        print("usage: validate-layout.py <publish-dir> [--portable]")
        return 2
    root = args[0]
    if not os.path.isdir(root):
        print(f"LAYOUT: directory missing: {root}")
        return 2

    errors = []
    exe = os.path.join(root, "AlTamam.DesktopHost.exe")
    if not os.path.isfile(exe):
        errors.append("missing real executable AlTamam.DesktopHost.exe")
    elif not is_pe(exe):
        errors.append("AlTamam.DesktopHost.exe is NOT a valid Windows PE binary (fake exe?)")

    for name in ("AlTamam.DesktopHost.dll", "AlTamam.DesktopHost.runtimeconfig.json",
                 "AlTamam.DesktopHost.deps.json"):
        if not os.path.isfile(os.path.join(root, name)):
            errors.append(f"missing {name}")

    # WebView2 loader + native SQLCipher must ship in the layout (15.02/15.05)
    webview2 = os.path.join(root, "WebView2Loader.dll")
    if not os.path.isfile(webview2):
        errors.append("missing WebView2Loader.dll (WebView2 runtime dependency)")
    natives = [d for d in os.listdir(root) if d.lower().startswith("runtimes")]
    cipher = []
    for base in ([os.path.join(root, d) for d in natives] + [root]):
        for dirpath, _, files in os.walk(base):
            cipher += [f for f in files if "sqlcipher" in f.lower() or f.lower() == "e_sqlcipher.dll"]
    if not cipher:
        errors.append("no native SQLCipher library found under runtimes/ (encrypted DB would fail at startup)")

    if portable:
        webui = os.path.join(root, "webui", "index.html")
        if not os.path.isfile(webui):
            errors.append("portable ZIP must bundle webui/index.html content")
        # Arabic typography is part of the offline contract: font files and OFL
        # licenses MUST be embedded into the packaged WebUI (never a CDN).
        font_dir = os.path.join(root, "webui", "assets", "fonts")
        for font in ("Cairo-Variable.ttf", "Tajawal-Regular.ttf", "Tajawal-Bold.ttf"):
            candidate = os.path.join(font_dir, font)
            if not os.path.isfile(candidate) or os.path.getsize(candidate) < 10000:
                errors.append(f"offline Arabic font missing or truncated: {font}")
        for license_file in ("OFL-cairo.txt", "OFL-tajawal.txt"):
            if not os.path.isfile(os.path.join(font_dir, license_file)):
                errors.append(f"offline Arabic font license missing: {license_file}")
        screens = os.path.join(root, "webui", "screens")
        if os.path.isdir(screens):
            # Screens are nested route directories (screens/**/index.html),
            # not 69 HTML files immediately inside screens/.
            n = sum(
                1 for directory, _, files in os.walk(screens)
                for name in files
                if name.lower() == "index.html"
            )
            if n < 69:
                errors.append(f"webui/screens has {n} route index.html files, expected >=69")
        else:
            errors.append("portable ZIP missing webui/screens")
        for junk in ("*.pdb", "*.xml", "obj", "*.binlog"):
            hits = [f for f in os.listdir(root)
                    if (junk.startswith("*.") and f.endswith(junk[1:])) or f == junk]
            if hits:
                errors.append(f"clean-directory rule violated: {hits}")

    if errors:
        print("LAYOUT VALIDATION FAIL:")
        for e in errors:
            print("  -", e)
        return 1
    mode = "portable" if portable else "publish"
    print(f"LAYOUT VALIDATION PASS ({mode}): real PE exe, WebView2 loader, native SQLCipher, config present.")
    return 0

if __name__ == "__main__":
    sys.exit(main())
