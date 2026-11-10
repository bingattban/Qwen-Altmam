#!/usr/bin/env python3
"""Negative-path regression for the current phases 00/01/02 source and CI gates."""
import importlib.util
from pathlib import Path
import unittest
from unittest.mock import patch

ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location("phase00102",ROOT/"build/verify-phase00-02-gates.py")
gate=importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)

class PhaseGateTests(unittest.TestCase):
    def test_real_source(self):
        self.assertEqual([],gate.verify(gate.collect(ROOT)))
    def test_manifest_and_database_drift(self):
        f=gate.collect(ROOT)
        for key,bad in [("screen_count",68),("action_count",102),
                        ("registered_migrations",[1,2,4]),("embedded_migrations",[1]),
                        ("schema_version",15)]:
            self.assertTrue(gate.verify(dict(f,**{key:bad})),key)
    def test_sqlcipher_and_destructive_restore_gates(self):
        f=gate.collect(ROOT)
        self.assertTrue(gate.verify(dict(f,sqlcipher_version="unapproved")))
        self.assertTrue(gate.verify(dict(f,destructive_restore_wired=True)))
        self.assertTrue(gate.verify(dict(f,v013_storage_constraints=False)))
        self.assertTrue(gate.verify(dict(f,current_schema_pin_verified=False)))
    def test_test_first_and_four_artifacts(self):
        f=gate.collect(ROOT)
        for key in ("two_workflows","core_tests","release_test_gate",
                    "four_package_matrix","phase02_integration_on_both"):
            self.assertTrue(gate.verify(dict(f,**{key:False})),key)
    def test_restore_guard_accepts_only_explicit_refusal_not_executable_arm(self):
        original = Path.read_text
        catalog = ROOT / "src/AlTamam.Application/Bridge/BridgeHandlerCatalog.cs"
        source = original(catalog, encoding="utf-8")
        self.assertFalse(gate.collect(ROOT)["destructive_restore_wired"])
        refusal = 'Task.FromResult(Fail(req, BridgeSharedServices.Error('
        start = source.index('"RestoreDatabaseAtomically" =>')
        for changed in (
            source[:start] + source[start:].replace(refusal,
                'Task.FromResult(Done(req, BridgeSharedServices.Error(', 1),
            source[:start] + source[start:].replace('"RestoreDatabaseAtomically" =>',
                '"RestoreDatabaseAtomically" => ExecuteRestore(), //', 1),
        ):
            def read(path, *args, **kwargs):
                return changed if path == catalog else original(path, *args, **kwargs)
            with patch.object(Path, "read_text", read):
                facts = gate.collect(ROOT)
            self.assertTrue(facts["destructive_restore_wired"])
            self.assertTrue(gate.verify(facts))

if __name__=="__main__":unittest.main()
