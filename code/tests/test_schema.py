import unittest

from tests._common import *  # noqa: F401,F403
from auditor.schema import ALLOWED_TRIPLES, OPERATIONS, canonicalize, make_step, sort_key


class SchemaTests(unittest.TestCase):
    def test_fifteen_operations_in_spec_order(self):
        self.assertEqual(len(OPERATIONS), 15)
        names = [o for o, _, _ in OPERATIONS]
        self.assertEqual(names[0], "REPORT_REMAINING_RECORD")
        self.assertEqual(names[4], "DISABLE_DATA_FLOW")       # flows stopped before dependent changes
        self.assertEqual(names[5], "BLOCK_RESTORE")
        self.assertEqual(names[-1], "VERIFY_RESTORE_BLOCKED")
        self.assertEqual(len(ALLOWED_TRIPLES), 15)

    def test_make_step_derives_state(self):
        self.assertEqual(make_step("REMEDIATION", "BLOCK_RESTORE", "backup_service", "backup_1")["state"], "BLOCKED")
        self.assertEqual(make_step("FINDING", "REPORT_RESTORE_RISK", "backup_service", "backup_1", "evidence_01")["state"], "ACTIVE")

    def test_canonical_order_phase_then_operation_then_system_then_resource(self):
        steps = [
            make_step("VERIFICATION", "VERIFY_RECORD_ABSENT", "search_service", "search_1"),
            make_step("REMEDIATION", "DELETE_RECORD", "search_service", "search_1"),
            make_step("FINDING", "REPORT_REMAINING_RECORD", "search_service", "search_1", "evidence_01"),
            make_step("REMEDIATION", "DISABLE_DATA_FLOW", "integration_service", "data_flow_1"),
            make_step("VERIFICATION", "VERIFY_RECORD_ABSENT", "account_service", "account_1"),
        ]
        out = canonicalize(steps)
        self.assertEqual([s["step_no"] for s in out], [1, 2, 3, 4, 5])
        self.assertEqual([(s["phase"][:4], s["operation"], s["system_id"]) for s in out], [
            ("FIND", "REPORT_REMAINING_RECORD", "search_service"),
            ("REME", "DISABLE_DATA_FLOW", "integration_service"),
            ("REME", "DELETE_RECORD", "search_service"),
            ("VERI", "VERIFY_RECORD_ABSENT", "account_service"),
            ("VERI", "VERIFY_RECORD_ABSENT", "search_service"),
        ])

    def test_resource_id_ties_use_plain_string_order(self):
        """Decision (NOTES.md): ties on (system_id) are broken by PLAIN STRING order of resource_id.
        Ids are not zero-padded, so string order differs from numeric order."""
        a = make_step("VERIFICATION", "VERIFY_RECORD_PRESENT", "security_service", "audit_record_987")
        b = make_step("VERIFICATION", "VERIFY_RECORD_PRESENT", "security_service", "audit_record_9528")
        c = make_step("VERIFICATION", "VERIFY_RECORD_PRESENT", "security_service", "audit_record_10")
        self.assertLess("audit_record_9528", "audit_record_987")          # the premise: string != numeric
        self.assertGreater(9528, 987)
        out = canonicalize([a, b, c])
        self.assertEqual([s["resource_id"] for s in out], ["audit_record_10", "audit_record_9528", "audit_record_987"])
        self.assertEqual(sort_key(a)[2:], ("security_service", "audit_record_987"))

    def test_canonicalize_does_not_mutate_input(self):
        s = make_step("FINDING", "REPORT_REMAINING_RECORD", "account_service", "account_1", "evidence_01")
        canonicalize([s])
        self.assertEqual(s["step_no"], 0)


if __name__ == "__main__":
    unittest.main()
