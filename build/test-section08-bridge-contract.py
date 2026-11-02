#!/usr/bin/env python3
"""Section 08 fail-closed static contract: UI actions must be explicit C# handlers.

This is a preflight drift check, not a substitute for real Windows bridge/E2E,
service-level RBAC, physical backup restore or production acceptance.
"""
import json
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
WEB = ROOT / "src" / "AlTamam.WebUI"
UI_JS = WEB / "assets" / "js"
ACTIONS = WEB / "action-manifest.json"
CATALOG = ROOT / "src" / "AlTamam.Application" / "Bridge" / "BridgeHandlerCatalog.cs"
CLIENT = UI_JS / "bridge-client.js"
RESPONSE = ROOT / "src" / "AlTamam.Contracts" / "Bridge" / "BridgeMessages.cs"
DISPATCHER = ROOT / "src" / "AlTamam.Application" / "Bridge" / "WebView2BridgeDispatcher.cs"


def handlers(source):
    # Includes C# switch arms such as '"A" or "B" =>'; no default arm.
    arms = re.findall(
        r'^\s*((?:"[A-Za-z]\w*"\s+or\s+)*"[A-Za-z]\w*")\s*=>',
        source, flags=re.MULTILINE
    )
    return {item for arm in arms for item in re.findall(r'"([A-Za-z]\w+)"', arm)}


def ui_calls():
    calls = set()
    dynamic = {}
    for path in sorted(UI_JS.glob("*.js")):
        if path.name == "bridge-client.js":
            continue  # transport uses invoke(action), not an application command
        source = path.read_text(encoding="utf-8")
        calls.update(re.findall(r'\.invoke\(\s*["\']([A-Za-z]\w+)["\']', source))
        dynamic[path.name] = re.findall(r'\.invoke\(\s*([A-Za-z_$]\w*)\s*[,)]', source)
    return calls, {k: v for k, v in dynamic.items() if v}


class Section08Contract(unittest.TestCase):
    def test_ui_actions_are_canonical_and_have_explicit_native_handlers(self):
        manifest = json.loads(ACTIONS.read_text(encoding="utf-8"))["actions"]
        canonical = {row["action"] for row in manifest}
        self.assertEqual(len(canonical), len(manifest))
        native = handlers(CATALOG.read_text(encoding="utf-8"))
        called, dynamic = ui_calls()
        self.assertTrue(called, "No UI bridge invocations found; scanner drift")
        self.assertFalse(called - canonical, "UI invokes actions absent from manifest")
        self.assertFalse(called - native, "UI invokes actions handled only by fallback")
        # Every dynamically chosen business action must be listed here deliberately.
        self.assertEqual(dynamic, {"admin-settings.js": ["action"],
                                   "attendance-ui.js": ["action"]})
        source = (UI_JS / "admin-settings.js").read_text(encoding="utf-8")
        for action in ("UpdateAuditPresentation", "DeleteAuditPresentation"):
            self.assertIn('action==="' + action + '"', source)
            self.assertIn(action, native)
        source = (UI_JS / "attendance-ui.js").read_text(encoding="utf-8")
        for action in ("SubmitAttendance", "ReviseTodayAttendance"):
            self.assertIn('"' + action + '"', source)
            self.assertIn(action, native)

    def test_nonimplemented_native_restore_is_not_exposed_as_success(self):
        declared = {a["action"] for a in json.loads(ACTIONS.read_text(encoding="utf-8"))["actions"]}
        native = handlers(CATALOG.read_text(encoding="utf-8"))
        # A new fallback MUST be reviewed rather than silently declared done.
        self.assertEqual(declared - native, {"RestoreDatabaseAtomically"})
        called, _ = ui_calls()
        self.assertNotIn("RestoreDatabaseAtomically", called)
        source = CATALOG.read_text(encoding="utf-8")
        self.assertIn("BridgeErrorCodes.MissingData", source)
        self.assertIn('''الإجراء '{req.Action}' معتمد''', source)

    def test_auto_operation_id_registry_matches_canonical_mutating_actions(self):
        manifest = json.loads(ACTIONS.read_text(encoding="utf-8"))["actions"]
        required = {x["action"] for x in manifest
                    if x["isMutating"] and x["action"] != "Logout"}
        client = CLIENT.read_text(encoding="utf-8")
        match = re.search(r'const MUTATING_ACTIONS = new Set\(\[(.*?)\]\);',
                          client, flags=re.DOTALL)
        self.assertIsNotNone(match)
        supplied = set(re.findall(r'"([A-Za-z]\w+)"', match.group(1)))
        self.assertEqual(supplied, required)

    def test_structured_error_contract_and_no_forged_server_identity(self):
        response = RESPONSE.read_text(encoding="utf-8")
        for name in ("Code", "MessageAr", "ValidationErrors", "ConflictInfo",
                     "AuditEventId", "ExpectedVersion", "ActualVersion"):
            self.assertIn(name, response)
        dispatch = DISPATCHER.read_text(encoding="utf-8")
        self.assertIn("principal.Role", dispatch)
        self.assertIn("request.SessionNonce", dispatch)
        self.assertIn("Data = response.Success ? response.Data : null", dispatch)
        self.assertIn("ValidationErrors", dispatch)
        self.assertIn("ConflictInfo", dispatch)


if __name__ == "__main__":
    unittest.main(verbosity=2)
