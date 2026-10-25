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
    check(len(actions) == 97, "action manifest drift")
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
    print(f"UI SHELL GUARD PASS: 4 Arabic sections, 69 paths, 97 bridge contracts.")
    print(f"FUNCTIONAL COMPLETENESS: {scaffolded}/69 pages still show a placeholder.")
    print("Passing this guard does NOT count as completed user-facing integration.")

if __name__ == "__main__":
    main()
