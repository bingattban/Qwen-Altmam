#!/usr/bin/env python3
"""Negative-path regression for the current phases 00/01/02 source and CI gates."""
import importlib.util
from pathlib import Path
import unittest

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
                        ("registered_migrations",[1,2,4]),("embedded_migrations",[1])]:
            self.assertTrue(gate.verify(dict(f,**{key:bad})),key)
    def test_sqlcipher_and_destructive_restore_gates(self):
        f=gate.collect(ROOT)
        self.assertTrue(gate.verify(dict(f,sqlcipher_version="unapproved")))
        self.assertTrue(gate.verify(dict(f,destructive_restore_wired=True)))
        self.assertTrue(gate.verify(dict(f,current_schema_pin_verified=False)))
    def test_test_first_and_four_artifacts(self):
        f=gate.collect(ROOT)
        for key in ("two_workflows","core_tests","release_test_gate",
                    "four_package_matrix","phase02_integration_on_both"):
            self.assertTrue(gate.verify(dict(f,**{key:False})),key)

if __name__=="__main__":unittest.main()
