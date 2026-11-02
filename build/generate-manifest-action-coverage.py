#!/usr/bin/env python3
"""Render auditable Section 08 action coverage from the CURRENT shipped manifest.

An explicit native switch arm establishes routing, NOT full functional acceptance.
Only the physical clean-machine/restore gates can authorize a production GO.
"""
import json
import re
import sys
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MANIFEST = ROOT / "src/AlTamam.WebUI/action-manifest.json"
CATALOG = ROOT / "src/AlTamam.Application/Bridge/BridgeHandlerCatalog.cs"
OUTPUT = ROOT / "docs/implementation/MANIFEST_ACTION_COVERAGE.md"


def render_coverage():
    actions = json.loads(MANIFEST.read_text(encoding="utf-8"))["actions"]
    source = CATALOG.read_text(encoding="utf-8")
    arms = re.findall(
        r'^\s*((?:"[A-Za-z]\w*"\s+or\s+)*"[A-Za-z]\w*")\s*=>',
        source, flags=re.MULTILINE
    )
    wired = {name for arm in arms for name in re.findall(r'"([A-Za-z]\w+)"', arm)}
    canonical = {a["action"] for a in actions}
    if len(canonical) != len(actions) or canonical - wired != {"RestoreDatabaseAtomically"}:
        raise ValueError("Unexpected missing/duplicate C# action; review manifest and restore gate.")
    groups = defaultdict(list)
    for action in actions:
        groups[action["owningService"]].append(action)
    lines = [
        "# AlTamam — Current C# Bridge Action Coverage",
        "",
        "Generated from the current shipped `action-manifest.json` and explicit `BridgeHandlerCatalog.cs` switch arms.",
        "This table is a **routing inventory**, not an assertion that every command passes feature,",
        "Windows E2E, clean-machine or physical restore acceptance tests.",
        "",
        f"**Manifest:** {len(actions)} actions / {len(groups)} owning services.",
        f"**Explicit native handlers:** {len(canonical & wired)}.",
        "**Intentionally unavailable:** `RestoreDatabaseAtomically` (native physical restore acceptance gate).",
        "",
        "## Service-level coverage",
        "",
        "| Owning service | Manifest actions | Explicit C# handlers |",
        "|---|---:|---:|",
    ]
    for service in sorted(groups):
        group = groups[service]
        lines.append(f"| {service} | {len(group)} | {sum(a['action'] in wired for a in group)} |")
    lines.extend([
        "",
        "## Action-by-action routing inventory",
        "",
        "Statuses: **WIRED** = explicit native handler (behavior must be tested separately);",
        "**DISABLED** = no allowed execution or default-success fallback.",
        "Idempotency and role data remain authoritative in the JSON manifest and C# mirror.",
        "",
    ])
    for service in sorted(groups):
        lines.extend([
            f"### {service}",
            "",
            "| Canonical action | Kind | Declared idempotency | Native routing |",
            "|---|---|---|---|",
        ])
        for row in sorted(groups[service], key=lambda x: x["action"]):
            name = row["action"]
            kind = "MUTATION" if row["isMutating"] else "READ"
            idem = "yes" if row["idempotentByRequestId"] else "no"
            state = "WIRED" if name in wired else "DISABLED"
            lines.append(f"| `{name}` | {kind} | {idem} | {state} |")
        lines.append("")
    lines.extend([
        "## Unresolved acceptance evidence (not marked PASS)",
        "",
        "- The 69-route screen/navigation inventory is not proof of each route's full end-to-end contract.",
        "- Error metadata, actor identity and durable audit receipts require real service/bridge tests per action.",
        "- Missing, skipped, running and failed automatic attendance attempts are not durably recorded",
        "  by the completion-only ledger; an unrecorded due time is **not** a proven failure.",
        "- Destructive `RestoreDatabaseAtomically` stays disabled until owner-approved native",
        "  restore, rollback and independent physical Windows acceptance pass.",
        "- Four packaged Windows artifacts and SHA-256 passing CI do not replace owner clean-machine",
        "  offline acceptance or production signoff.",
        "",
        "Regenerate with `python build/generate-manifest-action-coverage.py` and verify",
        "with `python build/generate-manifest-action-coverage.py --check`.",
        "",
    ])
    return "\n".join(lines)


if __name__ == "__main__":
    rendered = render_coverage()
    if sys.argv[1:] == ["--check"]:
        if not OUTPUT.exists() or OUTPUT.read_text(encoding="utf-8") != rendered:
            raise SystemExit("Action coverage inventory is outdated; regenerate before merge.")
        print("Action coverage inventory matches the current manifest and C# switch handlers.")
    elif len(sys.argv) == 1:
        OUTPUT.parent.mkdir(parents=True, exist_ok=True)
        OUTPUT.write_text(rendered, encoding="utf-8")
        print("Updated", OUTPUT)
    else:
        raise SystemExit("Usage: generate-manifest-action-coverage.py [--check]")
