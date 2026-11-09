#!/usr/bin/env python3
"""Validate a frozen CI candidate inventory; NEVER claim physical UAT.
This validates metadata consistency only. Actual distributed file SHA-256
must be rechecked against the binary on the independent Windows machine.
"""
import json
import re
import sys
from pathlib import Path

SOURCE_RE=re.compile(r"[0-9a-f]{40}\Z")
SHA_RE=re.compile(r"[0-9a-f]{64}\Z")
VERSION_RE=re.compile(r"[0-9]+\.[0-9]+\.[0-9]+-ci\Z")
EXPECTED={
    ("Windows-32bit","Portable"):("win-x86","Portable","zip"),
    ("Windows-32bit","Installer"):("win-x86","Setup","exe"),
    ("Windows-64bit","Portable"):("win-x64","Portable","zip"),
    ("Windows-64bit","Installer"):("win-x64","Setup","exe")
}
CI_STATUS="CI_CANDIDATE_ONLY_NOT_PRODUCTION_ACCEPTANCE"
CHECKS="four file bytes, exact per-architecture SHA256SUMS and metadata, same checkout/source/version"

def fail(reason):
    raise ValueError("Untrusted frozen CI inventory: "+reason)

def validate(d):
    if not isinstance(d,dict):
        fail("expected object")
    if d.get("schema")!="AlTamam.Phase15.CandidateInventory.v1":
        fail("unknown inventory schema")
    if d.get("status")!=CI_STATUS:
        fail("production acceptance is never inferred from CI")
    source=d.get("sourceHeadSha")
    checkout=d.get("testedCheckoutSha")
    if not isinstance(source,str) or not SOURCE_RE.fullmatch(source) or source!=checkout:
        fail("CI source and tested checkout SHA mismatch")
    for name in ("githubRunId","coreRunId"):
        value=d.get(name)
        if not isinstance(value,str) or not re.fullmatch(r"[1-9][0-9]{7,19}",value):
            fail("missing or invalid "+name)
    if d["githubRunId"]==d["coreRunId"]:
        fail("Core and Release run IDs must differ")
    version=d.get("version")
    if not isinstance(version,str) or not VERSION_RE.fullmatch(version):
        fail("invalid CI candidate version")
    if type(d.get("releasePackageCount")) is not int or d["releasePackageCount"]!=4:
        fail("must have exactly four customer package classes")
    files=d.get("releaseFiles")
    if not isinstance(files,list) or len(files)!=4:
        fail("exactly four customer distributions required")
    seen=set()
    digests=set()
    for item in files:
        if not isinstance(item,dict):
            fail("bad package item")
        classkey=(item.get("architecture"),item.get("class"))
        if classkey not in EXPECTED or classkey in seen:
            fail("duplicated/unknown architecture and package class")
        seen.add(classkey)
        runtime,kind,ext=EXPECTED[classkey]
        filename=f"AlTamamDaily_{version}_{kind}_{runtime}.{ext}"
        artifact=f"AlTamam-{item['class']}-{item['architecture']}-rc-{version}"
        if item.get("file")!=filename or item.get("githubArtifactName")!=artifact:
            fail("artifact filenames do not match declared release class")
        if item.get("runtimeIdentifier")!=runtime:
            fail("wrong CPU architecture")
        size=item.get("bytes")
        if type(size) is not int or size<=0:
            fail("invalid customer package size")
        digest=item.get("sha256")
        if not isinstance(digest,str) or not SHA_RE.fullmatch(digest) or digest in digests:
            fail("invalid or repeated package SHA256")
        digests.add(digest)
    if seen!=set(EXPECTED):
        fail("not all x86/x64 Portable/Installer classes present")
    if d.get("checksPassed")!=CHECKS:
        fail("the original independent audit must be explicitly identified")
    for field in ("independentCleanOfflineWindowsAcceptance",
                  "differentMachineEncryptedRecovery","realPowerFailureRecovery"):
        if d.get(field)!="NOT_EXECUTED":
            fail(field+" cannot be marked passed by CI inventory")
    if d.get("ownerSignedFinalRelease") is not False:
        fail("no owner signature was collected")
    return d

if __name__=="__main__":
    if len(sys.argv)!=2:
        sys.exit("Usage: validate-frozen-candidate.py PATH.json")
    try:
        content=json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
        validate(content)
    except (ValueError, OSError, KeyError, TypeError) as ex:
        sys.exit("FAIL: "+str(ex))
    print("PASS: frozen four-class CI inventory shape and NO-GO; not physical rehash or owner acceptance")
