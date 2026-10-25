#!/usr/bin/env python3
"""Static UI shell regression guard; functional route completeness is audited separately."""
from __future__ import annotations

import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
UI = ROOT / "src" / "AlTamam.WebUI"
MAIN = UI / "index.html"
ROUTES = UI / "screen-manifest.json"
CONTRACT = UI / "action-manifest.json"

def check(condition: bool, message: str) -> None:
    if not condition:
        raise SystemExit("UI SHELL GUARD FAIL: " + message)

def main() -> None:
    html = MAIN.read_text(encoding="utf-8")
    navigation = (UI / "assets/js/altamam-app.js").read_text(encoding="utf-8")
    bridge = (UI / "assets/js/bridge-client.js").read_text(encoding="utf-8")
    window = (ROOT / "src/AlTamam.DesktopHost/MainWindow.xaml.cs").read_text(encoding="utf-8")
    xaml = (ROOT / "src/AlTamam.DesktopHost/MainWindow.xaml").read_text(encoding="utf-8")
    iss = (ROOT / "build/AlTamam-installer.iss").read_text(encoding="utf-8")
    manifest = json.loads(ROUTES.read_text(encoding="utf-8"))
    actions = json.loads(CONTRACT.read_text(encoding="utf-8"))["actions"]
    sections = {
        "dashboard": "الرئيسية",
        "monthly": "التمام الشهري",
        "admin": "الإدارة",
        "settings": "الإعدادات",
    }
    check(len(manifest["screens"]) == manifest["screenCount"] == 69, "missing route manifest entries")
    # Phase 00: count alone hid a missing AddPerson route and an obsolete Reports route.
    # Compare the ACTUAL navigable JavaScript catalog to the canonical route manifest.
    catalog_match = re.search(r"const SCREEN_CATALOG = (\\[[^\\n]+?\\]);", navigation)
    check(catalog_match is not None, "missing SCREEN_CATALOG in live JavaScript")
    catalog = json.loads(catalog_match.group(1))
    declared = manifest["screens"]
    expected_ids = [f"screen_{i:03d}" for i in range(1, 71) if i != 20]
    check([r.get("screenId") for r in declared] == expected_ids,
          "wrong canonical route IDs/order; screen_020 must never reappear")
    check(len(catalog) == len(declared), "JavaScript screen catalog count differs from manifest")
    check(len({r["routeKey"] for r in declared}) == len(declared),
          "duplicate canonical route keys")
    check(len({r["key"] for r in catalog}) == len(catalog),
          "duplicate live JavaScript route keys")
    for registered, live in zip(declared, catalog):
        check(live["key"] == registered["routeKey"], "route identity/order drift: " + str(live))
        check(live["path"] == registered["localAssetPath"],
              "route path drift for " + registered["routeKey"])
        check(live["title"] == registered["titleAr"],
              "route title drift for " + registered["routeKey"])
        check(live["phase"] == registered["implementationPhase"],
              "phase ownership drift for " + registered["routeKey"])
    keys = {r["key"] for r in catalog}
    check("AddPerson" in keys and "PersonnelReport" in keys and "ExportPersonReport" in keys,
          "approved screen_070 / screen_025 / screen_026 missing")
    check("Reports" not in keys, "retired duplicate Reports route reintroduced")
    check("AddPerson" in [r["key"] for r in catalog if r.get("embedded")],
          "AddPerson must stay inside Edit Names, not become a redundant standalone card")
    check("SCREEN_CATALOG.filter(route => !route.embedded" in navigation,
          "embedded route is displayed as a redundant standalone directory link")
    check(not (UI / "screens/Administration/PersonnelManagement/ReportsAndDismissal/Reports/index.html").exists(),
          "retired duplicate Reports HTML remains bundled")
    # Sensitive records must not be staged accidentally when operators build locally.
    ignored = (ROOT / ".gitignore").read_text(encoding="utf-8")
    for pattern in ("*.db", "*.db-wal", "*.db-shm", "*.sqlite", "*.sqlite3",
                    "*.key", "*.protected", "*.bak", "*.xlsx", "*.pdf"):
        check(pattern in ignored, "missing sensitive file ignore pattern: " + pattern)
    import subprocess
    tracked = subprocess.run(["git", "ls-files", "-z"], cwd=ROOT,
                             capture_output=True, check=True).stdout.decode("utf-8").split("\\0")
    dangerous = re.compile(r"(?i)\\.(?:db|sqlite|sqlite3|db-wal|db-shm|key|protected|bak|xlsx|xlsm|pdf)$")
    leaked = [f for f in tracked if dangerous.search(f) and not f.startswith("tests/fixtures/")]
    check(not leaked, "possible tracked personnel data / key / report: " + str(leaked[:5]))
    check(len(actions) == 96, "action manifest drift")
    for section, title in sections.items():
        tag = re.search(
            r'<button\b[^>]*id="navBtn-' + re.escape(section)
            + r'"[^>]*>\s*<span>(.*?)</span>',
            html, flags=re.DOTALL,
        )
        check(tag is not None and tag.group(1) == title, f"wrong Arabic heading: {section}")
        check(f'title: "{title}"' in navigation, f"missing UI section title: {title}")
    check(len(re.findall(r'id="navBtn-', html)) == 4, "unexpected extra primary navigation")
    check("renderRouteIndex" not in bridge, "duplicate English bridge index reintroduced")
    check("createElement('iframe')" not in bridge, "nested iframe navigation reintroduced")
    check("screen-frame" not in navigation, "nested frame reintroduced into main route nav")
    check("WindowStyle=\"SingleBorderWindow\"" in xaml, "WPF title-bar style missing")
    check("ResizeMode=\"CanResize\"" in xaml, "WPF resize controls missing")
    check("SystemParameters.WorkArea" in window, "screen-sized initial window missing")
    check("AppName=التمام اليومي" in iss, "Arabic installer app name missing")
    check("DefaultGroupName=التمام اليومي" in iss, "Arabic Start Menu name missing")
    check('Name: "{autodesktop}\\التمام اليومي"' in iss,
          "Arabic desktop shortcut missing")
    check("التمام الشهري" not in iss, "obsolete monthly app name remains in installer")
    check("Task.Run(PrepareServicesAsync)" in window,
          "database startup no longer overlaps WebView2 initialization")
    check("startupPanel.Visibility = Visibility.Collapsed" in window,
          "loading placeholder must close after verified UI navigation")
    check('x:Name="startupMessage"' in xaml, "Arabic native startup feedback missing")
    missing = [r["localAssetPath"] for r in manifest["screens"]
               if not (UI / r["localAssetPath"]).is_file()]
    check(not missing, f"missing route files: {missing[:5]}")
    scaffolded = sum(
        "يجري ربط الشاشة خلال مراحل تنفيذ المشروع"
        in (UI / r["localAssetPath"]).read_text(encoding="utf-8")
        for r in manifest["screens"]
    )
    print(f"UI SHELL GUARD PASS: 4 Arabic sections, 69 paths, 96 bridge contracts.")
    print(f"FUNCTIONAL COMPLETENESS: {scaffolded}/69 pages still show a placeholder.")
    print("Passing this guard does NOT count as completed user-facing integration.")

if __name__ == "__main__":
    main()
