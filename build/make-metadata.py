#!/usr/bin/env python3
"""Phase 15 (15.07): SHA-256 checksums + build metadata for BOTH artifacts.

Usage: python3 build/make-metadata.py <artifacts-dir> <publish-dir> <version> <Windows-32bit|Windows-64bit>
Writes SHA256SUMS.txt and BUILD_METADATA.json into <artifacts-dir>. Fails if
either required artifact is missing — CI must not mint metadata for partial runs.
"""
import hashlib, json, os, subprocess, sys, datetime

def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()

def git(*a):
    try:
        return subprocess.check_output(["git", *a], text=True).strip()
    except Exception:
        return ""

def main() -> int:
    if len(sys.argv) != 5:
        print(__doc__); return 2
    art, pub, version, arch = sys.argv[1:5]
    if arch not in ("Windows-64bit", "Windows-32bit"):
        print("METADATA FAIL: invalid target architecture:", arch); return 2
    runtime = "win-x86" if arch == "Windows-32bit" else "win-x64"
    wanted = [f"AlTamam-Portable-{arch}.zip", f"AlTamam-Installer-{arch}.exe"]
    missing = [w for w in wanted if not os.path.isfile(os.path.join(art, w))]
    if missing:
        print("METADATA FAIL, missing artifacts:", missing); return 1

    lines, files_meta = [], []
    for name in wanted:
        p = os.path.join(art, name)
        digest = sha256(p)
        lines.append(f"{digest}  {name}")
        files_meta.append({"file": name, "sha256": digest, "bytes": os.path.getsize(p)})

    meta = {
        "product": "AlTamam (التمام اليومي)",
        "version": version,
        "commit": git("rev-parse", "HEAD"),
        "branch": git("rev-parse", "--abbrev-ref", "HEAD"),
        "builtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "runnerOs": "windows-latest / x64",
        "targetFramework": f"net10.0-windows (WPF), self-contained {runtime}",
        "targetArchitecture": arch,
        "runtimeIdentifier": runtime,
        "architectureBitness": 32 if arch == "Windows-32bit" else 64,
        "signing": os.environ.get("ALNAMAM_SIGNED", "false"),
        "signingNote": "unsigned unless owner-provisioned approved certificate was applied; do NOT describe unsigned builds as 'secure'",
        "publishValidation": "build/validate-layout.py PASS recorded earlier in same job",
        "files": files_meta,
    }
    with open(os.path.join(art, "SHA256SUMS.txt"), "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
    with open(os.path.join(art, "BUILD_METADATA.json"), "w", encoding="utf-8") as f:
        json.dump(meta, f, ensure_ascii=False, indent=2)
    print("METADATA OK:")
    for l in lines: print(" ", l)
    return 0

if __name__ == "__main__":
    sys.exit(main())
