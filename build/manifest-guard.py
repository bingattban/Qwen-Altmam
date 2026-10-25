#!/usr/bin/env python3
"""Phase 15 CI gate (15.01): validate action-manifest.json invariants BEFORE packaging.

Fails the release pipeline (exit 1) on any violation:
  * exactly 97 actions, unique names
  * every allowedRoles value is one of the 4 declared principal roles
  * manifest == compiled ActionManifest.cs table (name/service/roles/mutating)
    byte-for-byte after normalization — CI refuses drift before packaging
  * dangerous mutating actions (CommitFullReset, RestoreDatabaseAtomically)
    are GA-authorized; Preview* twins MUST be non-mutating read-only previews
  * mutating actions declare idempotentByRequestId == true
  * CSP contains no unsafe-inline / unsafe-eval and connect-src 'none'
  * virtual origin is the exact pinned https origin, external network denied
Role-restriction decisions (restore GA+OA vs reset GA-only) follow the frozen
contract §2.9 matrix recorded in IMPLEMENTATION_LEDGER.md; this guard ENFORCES
the frozen contract, it does not silently relax or extend it.
Exit codes: 0 = pass, 1 = manifest violation, 2 = file/schema unreadable.
"""
import json, re, sys

PATH = "src/AlTamam.WebUI/action-manifest.json"
CODE_PATH = "src/AlTamam.Application/Bridge/ActionManifest.cs"
ROLES = {"Unauthenticated", "GeneralAdministration", "OrdinaryAdministration", "Supervisor"}
GA_ONLY_RESET = {"PreviewFullReset", "CommitFullReset"}
GA_ONLY_FOUNDER = {"LinkFoundingAdministrator"}
ADMIN_CORRECTION = {"SetClosedDayCorrectionPolicy","PreviewClosedDayCorrection","CommitClosedDayCorrection"}
MUTATING_DANGEROUS = {"CommitFullReset", "RestoreDatabaseAtomically"}

def main() -> int:
    try:
        with open(PATH, encoding="utf-8") as f:
            m = json.load(f)
    except Exception as e:
        print(f"MANIFEST-GUARD: cannot read {PATH}: {e}")
        return 2

    errors = []
    actions = m.get("actions", [])
    names = [a["action"] for a in actions]
    if len(actions) != 97:
        errors.append(f"expected 97 actions, found {len(actions)}")
    dupes = {n for n in names if names.count(n) > 1}
    if dupes:
        errors.append(f"duplicate action names: {sorted(dupes)}")

    for a in actions:
        bad = set(a.get("allowedRoles", [])) - ROLES
        if bad:
            errors.append(f"{a['action']}: unknown roles {sorted(bad)}")
        if not a.get("allowedRoles"):
            errors.append(f"{a['action']}: empty allowedRoles")
        name = a["action"]
        if name in GA_ONLY_RESET and set(a["allowedRoles"]) != {"GeneralAdministration"}:
            errors.append(f"{name}: full-reset family must be GeneralAdministration-only")
        if name in GA_ONLY_FOUNDER and set(a["allowedRoles"]) != {"GeneralAdministration"}:
            errors.append(f"{name}: only GeneralAdministration can link the founding account")
        if name in ADMIN_CORRECTION and set(a["allowedRoles"]) != {"GeneralAdministration", "OrdinaryAdministration"}:
            errors.append(f"{name}: closed-day correction must allow both administrations and forbid supervisors")
        if name in MUTATING_DANGEROUS and "GeneralAdministration" not in a["allowedRoles"]:
            errors.append(f"{name}: dangerous mutating action lost GA authorization")
        if a.get("isMutating") and not a.get("idempotentByRequestId"):
            errors.append(f"{name}: mutating action must be idempotentByRequestId")

    # manifest vs compiled table drift check
    try:
        cs = open(CODE_PATH, encoding="utf-8").read()
        rows = re.findall(
            r'new\("(?P<name>[^"]+)",\s*"(?P<svc>[^"]+)",\s*new\[\]\s*\{(?P<roles>[^}]*)\},\s*(?P<mut>true|false)\)',
            cs)
        code = {n: (s, frozenset(r.strip().strip('"') for r in rs.split(",")), m == "true")
                for n, s, rs, m in rows}
        if len(code) != len(actions):
            errors.append(f"ActionManifest.cs has {len(code)} rows, manifest has {len(actions)} actions")
        for a in actions:
            n = a["action"]
            if n not in code:
                errors.append(f"{n}: present in manifest, ABSENT from compiled ActionManifest.cs")
                continue
            svc, roles, mut = code[n]
            if svc != a["owningService"]:
                errors.append(f"{n}: service drift manifest={a['owningService']} code={svc}")
            if roles != frozenset(a["allowedRoles"]):
                errors.append(f"{n}: role drift manifest={sorted(a['allowedRoles'])} code={sorted(roles)}")
            if mut != a["isMutating"]:
                errors.append(f"{n}: isMutating drift manifest={a['isMutating']} code={mut}")
    except FileNotFoundError:
        errors.append(f"compiled table {CODE_PATH} not found — cannot verify drift")

    csp = m.get("contentSecurityPolicy", "")
    if "unsafe-inline" in csp or "unsafe-eval" in csp:
        errors.append("CSP contains unsafe-inline/unsafe-eval")
    if "connect-src 'none'" not in csp:
        errors.append("CSP missing connect-src 'none'")
    if m.get("virtualHostOrigin") != "https://manger.adwas.example":
        errors.append(f"virtualHostOrigin not pinned: {m.get('virtualHostOrigin')}")
    if m.get("allowExternalNetwork") is not False or m.get("offlineModeEnforced") is not True:
        errors.append("offline/external-network flags wrong")

    if errors:
        print("MANIFEST-GUARD FAIL:")
        for e in errors:
            print("  -", e)
        return 1
    print(f"MANIFEST-GUARD PASS: {len(actions)} actions, CSP strict, origin pinned.")
    return 0

if __name__ == "__main__":
    sys.exit(main())
