#!/usr/bin/env python3
"""Convert actual dotnet list-package JSON into a deterministic CycloneDX 1.6
component inventory. Licensing and offline network behaviour require separate
verification; neither is inferred from a successful dependency listing."""
import json
import os
import sys
import uuid
from pathlib import Path
from urllib.parse import quote

def convert(raw):
    projects = raw.get("projects")
    if not isinstance(projects, list) or not projects:
        raise ValueError("dotnet JSON inventory has no projects")
    packages = {}
    for project in projects:
        for framework in project.get("frameworks", []):
            for group in ("topLevelPackages", "transitivePackages"):
                for package in framework.get(group, []):
                    name = package.get("id")
                    version = package.get("resolvedVersion")
                    if not name or not version:
                        raise ValueError("dependency lacks a resolved NuGet version")
                    key = (name.lower(), version)
                    packages[key] = {
                        "type": "library",
                        "name": name,
                        "version": version,
                        "purl": "pkg:nuget/" + quote(name, safe="") + "@" + quote(version, safe=""),
                    }
    if not packages:
        raise ValueError("no resolved dependencies; refusing empty SBOM")
    result = {
        "bomFormat": "CycloneDX",
        "specVersion": "1.6",
        "serialNumber": "urn:uuid:" + str(uuid.uuid5(uuid.NAMESPACE_URL,
            os.environ.get("ALTAMAM_SOURCE_HEAD_SHA", "unidentified-checkout"))),
        "version": 1,
        "metadata": {
            "component": {"type": "application", "name": "AlTamam"},
            "properties": [
                {"name": "altamam:source-head-sha",
                 "value": os.environ.get("ALTAMAM_SOURCE_HEAD_SHA", "unknown")},
                {"name": "altamam:github-run-id",
                 "value": os.environ.get("GITHUB_RUN_ID", "unrecorded")},
                {"name": "altamam:license-review",
                 "value": "NOT_EXECUTED; inventory does not attest licenses"},
                {"name": "altamam:remote-endpoint-monitoring",
                 "value": "NOT_EXECUTED; requires actual Windows network trace"},
            ],
        },
        "components": [packages[key] for key in sorted(packages)]
    }
    return result

def main():
    if len(sys.argv) != 3:
        raise SystemExit("Usage: emit_phase13_sbom.py dotnet-inventory.json sbom.json")
    src, dst = map(Path, sys.argv[1:])
    bom = convert(json.loads(src.read_text(encoding="utf-8-sig")))
    dst.parent.mkdir(parents=True, exist_ok=True)
    dst.write_text(json.dumps(bom, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"Phase13 CycloneDX components: {len(bom['components'])}")

if __name__ == "__main__":
    main()
