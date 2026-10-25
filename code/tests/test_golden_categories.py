"""One golden test per policy category of deletion_rules.csv (plus the active data flow of spec 6.5, which is not a row of that file).

Each golden answer is typed out by hand below (status + every step: phase, operation, system, resource, evidence_id) from the rule text and
AGENTS.md 6.4, NOT computed by any code of this repository. The pipeline runs on a minimal dataset built for the case and must reproduce it exactly.
Case n has the account `account_n01` (already deleted, so it is only verified), the retained audit record `audit_record_n02`, and the resource under test `<prefix>_n03`."""
import csv
import unittest

from tests._common import DATASET
import extra_cases as xc

E, F, V = "evidence_x1", "FINDING", "VERIFICATION"
R = "REMEDIATION"


def case(n: int, category: str, rid: str, state: str, account_state: str = "ABSENT", file: str = "evidence_x1", **kw) -> dict:
    u = xc.person(n, f"Golden Person {n}", f"golden{n}@example.test")
    resources = [xc.res(f"account_{n}01", "USER_PROFILE", account_state), xc.res(f"audit_record_{n}02", "SECURITY_AUDIT_LOG", "PRESENT")]
    if category not in ("USER_PROFILE", "SECURITY_AUDIT_LOG"):
        resources.append(xc.res(rid, category, state, file, **kw))
    return xc.mk(f"G-{n:02d}", f"golden {category}", "", u, resources, [])


def account_present_case() -> dict:
    return case(1, "USER_PROFILE", "account_103", "PRESENT", account_state="PRESENT")


def audit(n: int, ev: str = "") -> list:
    return [V, "VERIFY_RECORD_PRESENT", "security_service", f"audit_record_{n}02", ev]


def acct(n: int, ev: str = "") -> list:
    return [V, "VERIFY_RECORD_ABSENT", "account_service", f"account_{n}01", ev]


# category -> (rule id in deletion_rules.csv or "spec 6.5", case, golden status, golden steps)
GOLDEN: dict[str, tuple[str, dict, str, list]] = {
    "USER_PROFILE": ("rule_01", account_present_case(), "INCOMPLETE", [
        [F, "REPORT_REMAINING_RECORD", "account_service", "account_101", E], [R, "DELETE_RECORD", "account_service", "account_101", ""],
        [V, "VERIFY_RECORD_ABSENT", "account_service", "account_101", ""], audit(1)]),
    "SECONDARY_IDENTIFIER": ("rule_02", case(2, "SECONDARY_IDENTIFIER", "contact_203", "PRESENT"), "INCOMPLETE", [
        [F, "REPORT_REMAINING_RECORD", "account_service", "contact_203", E], [R, "DELETE_RECORD", "account_service", "contact_203", ""],
        acct(2), [V, "VERIFY_RECORD_ABSENT", "account_service", "contact_203", ""], audit(2)]),
    "SEARCH_INDEX_ENTRY": ("rule_03", case(3, "SEARCH_INDEX_ENTRY", "search_303", "PRESENT"), "INCOMPLETE", [
        [F, "REPORT_REMAINING_RECORD", "search_service", "search_303", E], [R, "DELETE_RECORD", "search_service", "search_303", ""],
        acct(3), [V, "VERIFY_RECORD_ABSENT", "search_service", "search_303", ""], audit(3)]),
    "CACHE_ENTRY": ("rule_04", case(4, "CACHE_ENTRY", "cache_403", "PRESENT"), "INCOMPLETE", [
        [F, "REPORT_REMAINING_RECORD", "cache_service", "cache_403", E], [R, "DELETE_RECORD", "cache_service", "cache_403", ""],
        acct(4), [V, "VERIFY_RECORD_ABSENT", "cache_service", "cache_403", ""], audit(4)]),
    "SUPPORT_MESSAGE": ("rule_05", case(5, "SUPPORT_MESSAGE", "support_503", "EXPOSED"), "INCOMPLETE", [
        [F, "REPORT_EXPOSED_PII", "support_platform", "support_503", E], [R, "REDACT_RECORD", "support_platform", "support_503", ""],
        acct(5), audit(5), [V, "VERIFY_RECORD_REDACTED", "support_platform", "support_503", ""]]),
    "SUPPORT_ATTACHMENT": ("rule_06", case(6, "SUPPORT_ATTACHMENT", "object_603", "PRESENT"), "INCOMPLETE", [
        [F, "REPORT_REMAINING_RECORD", "object_storage", "object_603", E], [R, "DELETE_RECORD", "object_storage", "object_603", ""],
        acct(6), [V, "VERIFY_RECORD_ABSENT", "object_storage", "object_603", ""], audit(6)]),
    "ANALYTICS_PROFILE": ("rule_07", case(7, "ANALYTICS_PROFILE", "analytics_703", "EXPOSED"), "INCOMPLETE", [
        [F, "REPORT_EXPOSED_PII", "analytics_platform", "analytics_703", E], [R, "ANONYMIZE_RECORD", "analytics_platform", "analytics_703", ""],
        acct(7), audit(7), [V, "VERIFY_RECORD_ANONYMIZED", "analytics_platform", "analytics_703", ""]]),
    "PRODUCT_TELEMETRY": ("rule_08", case(8, "PRODUCT_TELEMETRY", "telemetry_803", "EXPOSED"), "INCOMPLETE", [
        [F, "REPORT_EXPOSED_PII", "analytics_platform", "telemetry_803", E], [R, "ANONYMIZE_RECORD", "analytics_platform", "telemetry_803", ""],
        acct(8), audit(8), [V, "VERIFY_RECORD_ANONYMIZED", "analytics_platform", "telemetry_803", ""]]),
    "USER_EXPORT": ("rule_09", case(9, "USER_EXPORT", "export_903", "PRESENT"), "INCOMPLETE", [
        [F, "REPORT_REMAINING_RECORD", "export_service", "export_903", E], [R, "DELETE_RECORD", "export_service", "export_903", ""],
        acct(9), [V, "VERIFY_RECORD_ABSENT", "export_service", "export_903", ""], audit(9)]),
    "MARKETING_PROFILE": ("rule_10", case(10, "MARKETING_PROFILE", "marketing_1003", "PRESENT"), "INCOMPLETE", [
        [F, "REPORT_REMAINING_RECORD", "marketing_platform", "marketing_1003", E], [R, "DELETE_RECORD", "marketing_platform", "marketing_1003", ""],
        acct(10), [V, "VERIFY_RECORD_ABSENT", "marketing_platform", "marketing_1003", ""], audit(10)]),
    "EMAIL_DELIVERY_LOG": ("rule_11", case(11, "EMAIL_DELIVERY_LOG", "email_delivery_1103", "EXPOSED"), "INCOMPLETE", [
        [F, "REPORT_EXPOSED_PII", "email_delivery_service", "email_delivery_1103", E], [R, "REDACT_RECORD", "email_delivery_service", "email_delivery_1103", ""],
        acct(11), audit(11), [V, "VERIFY_RECORD_REDACTED", "email_delivery_service", "email_delivery_1103", ""]]),
    "SECURITY_AUDIT_LOG": ("rule_12", case(12, "SECURITY_AUDIT_LOG", "", ""), "COMPLETE", [acct(12, E), audit(12, E)]),
    "LEGAL_HOLD_RECORD": ("rule_13", None, "COMPLETE", [acct(13, E), [V, "VERIFY_RECORD_PRESENT", "legal_service", "legal_hold_1303", "evidence_x2"], audit(13, E)]),
    "BACKUP_ARCHIVE": ("rule_14", case(14, "BACKUP_ARCHIVE", "backup_1403", "RESTORABLE"), "INCOMPLETE", [
        [F, "REPORT_RESTORE_RISK", "backup_service", "backup_1403", E], [R, "BLOCK_RESTORE", "backup_service", "backup_1403", ""],
        acct(14), audit(14), [V, "VERIFY_RESTORE_BLOCKED", "backup_service", "backup_1403", ""]]),
    "ACCOUNT_TOMBSTONE": ("rule_15", case(15, "ACCOUNT_TOMBSTONE", "tombstone_1503", "EXPOSED"), "INCOMPLETE", [
        [F, "REPORT_EXPOSED_PII", "identity_service", "tombstone_1503", E], [R, "REDACT_RECORD", "identity_service", "tombstone_1503", ""],
        acct(15), audit(15), [V, "VERIFY_RECORD_REDACTED", "identity_service", "tombstone_1503", ""]]),
    "DATA_FLOW": ("spec 6.5", case(16, "DATA_FLOW", "data_flow_1603", "ACTIVE"), "INCOMPLETE", [
        [F, "REPORT_ACTIVE_DATA_FLOW", "integration_service", "data_flow_1603", E], [R, "DISABLE_DATA_FLOW", "integration_service", "data_flow_1603", ""],
        acct(16), audit(16), [V, "VERIFY_DATA_FLOW_DISABLED", "integration_service", "data_flow_1603", ""]]),
}


def legal_hold_case() -> dict:
    n = 13
    u = xc.person(n, "Golden Person 13", "golden13@example.test")
    resources = [xc.res("account_1301", "USER_PROFILE", "ABSENT"), xc.res("audit_record_1302", "SECURITY_AUDIT_LOG", "PRESENT"),
                 xc.res("legal_hold_1303", "LEGAL_HOLD_RECORD", "PRESENT", "evidence_x2", retained_until="2033-12-31T23:59:59Z")]
    c = xc.mk("G-13", "golden LEGAL_HOLD_RECORD", "", u, resources, [])
    c["evidence"].append(xc.legal_hold_file("legal_hold_1303", u["user_id"], "2033-12-31T23:59:59Z"))
    return c


GOLDEN["LEGAL_HOLD_RECORD"] = ("rule_13", legal_hold_case(), *GOLDEN["LEGAL_HOLD_RECORD"][2:])


def steps_in_compact_form(steps: list[dict]) -> list[list[str]]:
    return [[s["phase"], s["operation"], s["system_id"], s["resource_id"], s["evidence_id"]] for s in steps]


class GoldenPerCategory(unittest.TestCase):
    def run_golden(self, category: str):
        rule, c, status, expected = GOLDEN[category]
        got_status, steps = xc.run_pipeline(c)
        self.assertEqual(got_status, status, f"{category} ({rule})")
        self.assertEqual(steps_in_compact_form(steps), expected, f"{category} ({rule})")
        self.assertEqual([s["step_no"] for s in steps], list(range(1, len(steps) + 1)))
        self.assertEqual([s["state"] for s in steps], [dict(self.STATE)[s[1]] for s in expected])     # the state implied by each operation

    STATE = [("REPORT_REMAINING_RECORD", "PRESENT"), ("REPORT_EXPOSED_PII", "PRESENT"), ("REPORT_ACTIVE_DATA_FLOW", "ACTIVE"), ("REPORT_RESTORE_RISK", "ACTIVE"),
             ("DISABLE_DATA_FLOW", "DISABLED"), ("BLOCK_RESTORE", "BLOCKED"), ("DELETE_RECORD", "ABSENT"), ("REDACT_RECORD", "REDACTED"), ("ANONYMIZE_RECORD", "ANONYMIZED"),
             ("VERIFY_RECORD_ABSENT", "ABSENT"), ("VERIFY_RECORD_PRESENT", "PRESENT"), ("VERIFY_RECORD_REDACTED", "REDACTED"), ("VERIFY_RECORD_ANONYMIZED", "ANONYMIZED"),
             ("VERIFY_DATA_FLOW_DISABLED", "DISABLED"), ("VERIFY_RESTORE_BLOCKED", "BLOCKED")]

    def test_every_category_of_the_rules_file_has_a_golden_test(self):
        rules = xc.RULES_CSV.splitlines()
        embedded = {r["data_category"]: r["rule_id"] for r in csv.DictReader(rules)}
        real = DATASET / "deletion_rules.csv"
        if real.exists():
            with real.open(newline="", encoding="utf-8") as fh:
                from_file = {r["data_category"]: r["rule_id"] for r in csv.DictReader(fh)}
            self.assertEqual(from_file, embedded)
        self.assertEqual(set(GOLDEN) - {"DATA_FLOW"}, set(embedded))
        for cat, rid in embedded.items():
            self.assertEqual(GOLDEN[cat][0], rid, cat)


def _make(category):
    return lambda self: self.run_golden(category)


for _cat, (_rule, *_rest) in GOLDEN.items():
    setattr(GoldenPerCategory, f"test_golden_{_rule.replace(' ', '_').replace('.', '_')}_{_cat}", _make(_cat))


class GoldenRemediationOrder(unittest.TestCase):
    """AGENTS.md 6.5: disable data flows and block unsafe restores BEFORE changing dependent records. One case with all three kinds of remediation."""

    def test_flow_then_restore_then_delete(self):
        n = 17
        u = xc.person(n, "Golden Person 17", "golden17@example.test")
        c = xc.mk("G-17", "golden order", "", u, [xc.res("account_1701", "USER_PROFILE", "ABSENT"), xc.res("audit_record_1702", "SECURITY_AUDIT_LOG", "PRESENT"),
                                                  xc.res("data_flow_1703", "DATA_FLOW", "ACTIVE"), xc.res("backup_1704", "BACKUP_ARCHIVE", "RESTORABLE"),
                                                  xc.res("search_1705", "SEARCH_INDEX_ENTRY", "PRESENT")], [])
        status, steps = xc.run_pipeline(c)
        self.assertEqual(status, "INCOMPLETE")
        self.assertEqual([(s[0], s[1], s[3]) for s in steps_in_compact_form(steps)], [
            (F, "REPORT_REMAINING_RECORD", "search_1705"), (F, "REPORT_ACTIVE_DATA_FLOW", "data_flow_1703"), (F, "REPORT_RESTORE_RISK", "backup_1704"),
            (R, "DISABLE_DATA_FLOW", "data_flow_1703"), (R, "BLOCK_RESTORE", "backup_1704"), (R, "DELETE_RECORD", "search_1705"),
            (V, "VERIFY_RECORD_ABSENT", "account_1701"), (V, "VERIFY_RECORD_ABSENT", "search_1705"), (V, "VERIFY_RECORD_PRESENT", "audit_record_1702"),
            (V, "VERIFY_DATA_FLOW_DISABLED", "data_flow_1703"), (V, "VERIFY_RESTORE_BLOCKED", "backup_1704")])


class GoldenCompliantVariants(unittest.TestCase):
    """The same categories when the copy is already compliant: nothing to fix (DELETE) or a verification of the required end state."""

    def test_deleted_copy_adds_no_step_but_a_redacted_copy_is_verified(self):
        status, steps = xc.run_pipeline(case(21, "CACHE_ENTRY", "cache_2103", "ABSENT"))
        self.assertEqual((status, steps_in_compact_form(steps)), ("COMPLETE", [acct(21, E), audit(21, E)]))
        status, steps = xc.run_pipeline(case(22, "EMAIL_DELIVERY_LOG", "email_delivery_2203", "REDACTED"))
        self.assertEqual((status, steps_in_compact_form(steps)),
                         ("COMPLETE", [acct(22, E), audit(22, E), [V, "VERIFY_RECORD_REDACTED", "email_delivery_service", "email_delivery_2203", E]]))
        status, steps = xc.run_pipeline(case(23, "BACKUP_ARCHIVE", "backup_2303", "BLOCKED"))
        self.assertEqual((status, steps_in_compact_form(steps)),
                         ("COMPLETE", [acct(23, E), audit(23, E), [V, "VERIFY_RESTORE_BLOCKED", "backup_service", "backup_2303", E]]))


if __name__ == "__main__":
    unittest.main()
