#!/usr/bin/env python3
"""Phase 15 CI gate (15.02): validate the published WPF layout BEFORE packaging.

Usage: python3 build/validate-layout.py <publish-dir> [--portable]
Fails (exit 1) unless the publish directory contains a genuine PE executable
and every required runtime asset. --portable additionally requires the bundled
webui content and forbids stray build junk (.pdb allowed only if owner opts in;
default: reject .pdb, .xml docs, obj/, *.FileList.txt).
Exit codes: 0 = pass, 1 = layout violation, 2 = directory missing.
"""
import json, os, struct, sys

def pe_machine(path: str) -> int | None:
    """Genuine Windows PE check: MZ header + e_lfanew -> 'PE\\0\\0' signature."""
    try:
        with open(path, "rb") as f:
            if f.read(2) != b"MZ":
                return None
            f.seek(0)
            head = f.read(0x40)
            if len(head) < 0x40:
                return None
            off = struct.unpack("<I", head[0x3C:0x40])[0]
            f.seek(off)
            if f.read(4) != b"PE\x00\x00":
                return None
            return struct.unpack("<H",f.read(2))[0]
    except (OSError, struct.error):
        return None

def main() -> int:
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    if "--arch" in sys.argv and len(args)>1:
        args.remove(sys.argv[sys.argv.index("--arch")+1])
    portable = "--portable" in sys.argv
    arch = None
    if "--arch" in sys.argv:
        at = sys.argv.index("--arch")
        if at+1 >= len(sys.argv) or sys.argv[at+1] not in ("win-x64","win-x86"):
            print("Expected --arch win-x64 or --arch win-x86"); return 2
        arch = sys.argv[at+1]
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
    else:
        machine=pe_machine(exe)
        if machine is None:
            errors.append("AlTamam.DesktopHost.exe is NOT a valid Windows PE binary")
        elif arch is not None and machine != {"win-x64":0x8664,"win-x86":0x14c}[arch]:
            errors.append(f"Wrong Windows PE architecture: expected {arch}, machine=0x{machine:04x}")

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
        runtime_installer=os.path.join(root,"Prerequisites","MicrosoftEdgeWebView2RuntimeInstaller.exe")
        if not os.path.isfile(runtime_installer) or os.path.getsize(runtime_installer)<50000000:
            errors.append("missing Microsoft WebView2 OFFLINE standalone runtime (portable and installer)")
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
        routes_json = os.path.join(root, "webui", "screen-manifest.json")
        if not os.path.isdir(screens) or not os.path.isfile(routes_json):
            errors.append("portable ZIP missing webui/screens or canonical screen-manifest.json")
        else:
            # 69 immutable screen/flow IDs do NOT imply 69 distinct HTML files:
            # screen_070 AddPerson is embedded in screen_010 Edit Names.
            # Derive the expected physical asset count from the exact manifest,
            # so obsolete HTML cannot make a missing route look present.
            with open(routes_json, encoding="utf-8") as file:
                screen_manifest = json.load(file)
            routes = screen_manifest["screens"]
            ids = [route.get("screenId") for route in routes]
            expected_ids = [f"screen_{i:03d}" for i in range(1, 71) if i != 20]
            if ids != expected_ids or screen_manifest.get("screenCount") != 69:
                errors.append("publish manifest must have exactly the approved 69 route IDs")
            unique_assets = {route["localAssetPath"] for route in routes}
            missing_routes = [
                path for path in unique_assets
                if not os.path.isfile(os.path.join(root, "webui", path))
            ]
            if missing_routes:
                errors.append(f"packaged route assets missing: {sorted(missing_routes)[:5]}")
            actual = {
                os.path.relpath(os.path.join(directory, name), os.path.join(root, "webui")).replace("\\", "/")
                for directory, _, files in os.walk(screens)
                for name in files if name.lower() == "index.html"
            }
            extra_routes = actual - unique_assets
            if extra_routes:
                errors.append(f"unexpected obsolete/unregistered route assets: {sorted(extra_routes)[:5]}")
            if len(actual) != len(unique_assets):
                errors.append(f"route HTML count drift: {len(actual)} files vs {len(unique_assets)} manifest assets")
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
    print(f"LAYOUT VALIDATION PASS ({mode}/{arch or 'unspecified'}): real PE exe, WebView2 loader, native SQLCipher, config present.")
    return 0

if __name__ == "__main__":
    sys.exit(main())
