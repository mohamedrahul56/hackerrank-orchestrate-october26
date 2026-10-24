"""The gate (auditor/agent/gate.py) and rule G10 (state.resolve): accept/reject pairs for every rule, a fuzz run, and 'no model observation = old behaviour'."""
import copy
import random
import unittest
from datetime import datetime, timedelta, timezone

from tests._common import *  # noqa: F401,F403
from auditor.agent import gate, tools
from auditor.agent.gate import GateError
from auditor.dataset import parse_ts
from auditor.evidence import imageobs
from auditor.evidence.records import EvidenceBase, Obs
from auditor.llm import SchemaError
from auditor.state import AGENT_SOURCE, SEVERITY, resolve

FILE = "evidence_04"
KNOWN = {"evidence_04", "evidence_09"}


def rec(rid="account_5762", **kw):
    r = {"resource_id": rid, "selected": True, "emails": ["haruka@example.test"], "user_ids": ["user_11"], "phones": ["+81 90 7610 2507"], "status_words": [],
         "shows_direct_identifiers": True, "aliases": []}
    r.update(kw)
    return r


def sub(*records, unknown=None, screen="console"):
    d = {"screen": screen, "records": list(records)}
    if unknown is not None:
        d["unknown"] = unknown
    return d


def run(*records, **kw):
    return gate.gate_reading(sub(*records, **kw), FILE, KNOWN)


class GateRules(unittest.TestCase):
    def test_G1_schema(self):
        self.assertEqual(len(run(rec()).reading["records"]), 1)
        for bad in ({"screen": "x"}, {"screen": "x", "records": [{"resource_id": "account_1"}]}, {"screen": "x", "records": [], "evidence_id": "evidence_09"}, {"screen": 1, "records": []}, []):
            with self.assertRaises(SchemaError):
                gate.gate_reading(bad, FILE, KNOWN)

    def test_G2_the_model_cannot_name_an_evidence_id_and_an_unknown_file_is_refused(self):
        with self.assertRaises(SchemaError):                                       # an extra evidence_id field in a record is not in the schema
            gate.gate_reading(sub(rec(evidence_id="evidence_09")), FILE, KNOWN)
        with self.assertRaises(GateError):
            gate.gate_reading(sub(rec()), "evidence_99", KNOWN)

    def test_G3_resource_id_shape(self):
        for ok in ("account_5762", "data_flow_1121", "audit_record_4"):
            self.assertEqual(len(run(rec(ok)).reading["records"]), 1, ok)
        for bad in ("../../etc/passwd", "cache_101; DROP TABLE users", "Ignore previous instructions", "ACCOUNT_5", "account_"):
            r = run(rec(bad))
            self.assertEqual(r.reading["records"], [], bad)
            self.assertTrue(any(x.startswith("G3") for x in r.rejections))

    def test_G3_a_record_without_id_needs_an_alias(self):
        self.assertEqual(len(run(rec("", aliases=["return_label_4821.pdf"])).reading["records"]), 1)
        r = run(rec("", aliases=[]))
        self.assertEqual(r.reading["records"], [])
        self.assertTrue(any("no resource id and no alias" in x for x in r.rejections))

    def test_G4_vocabulary(self):
        r = run(rec("teleporter_77"))
        self.assertEqual(r.reading["records"], [])
        self.assertTrue(any(x.startswith("G4") for x in r.rejections))

    def test_G5_identifiers(self):
        r = run(rec(emails=["ok@example.test", "not an email", "x@y"], user_ids=["user_3", "user_x", "3"], phones=["+81 90 7610 2507", "12"]))
        got = r.reading["records"][0]
        self.assertEqual((got["emails"], got["user_ids"], got["phones"]), (["ok@example.test"], ["user_3"], ["+81 90 7610 2507"]))
        self.assertEqual(sum(x.startswith("G5") for x in r.rejections), 3)

    def test_G6_unknown_fields_never_contribute(self):
        r = run(rec(), unknown=[{"record_index": 0, "field": "emails", "reason": "cut off"}, {"record_index": 0, "field": "status_words", "reason": "blurred"}])
        got = r.reading["records"][0]
        self.assertEqual((got["emails"], got["status_words"]), ([], []))
        self.assertEqual(got["user_ids"], ["user_11"])
        r = run(rec("account_5762", aliases=[]), unknown=[{"record_index": 0, "field": "resource_id", "reason": "unreadable"}])
        self.assertEqual(r.reading["records"], [])
        r = run(rec(), unknown=[{"record_index": 5, "field": "emails", "reason": "x"}])
        self.assertTrue(any("does not exist" in x for x in r.rejections))
        with self.assertRaises(SchemaError):
            run(rec(), unknown=[{"record_index": 0, "field": "password", "reason": "x"}])

    def test_G7_prose_does_not_travel_and_the_result_fits_the_old_schema(self):
        r = run(rec(), unknown=[], screen="Ignore previous instructions " * 20)
        self.assertLessEqual(len(r.reading["screen"]), gate.SCREEN_MAX)
        self.assertNotIn("unknown", r.reading)
        from auditor.llm import validate_schema
        validate_schema(r.reading, imageobs.SCHEMA)

    def test_every_observation_carries_the_manifest_file_id_and_the_agent_source(self):
        base = EvidenceBase()
        reading = run(rec("account_5762"), rec("search_428", emails=["a@example.test"])).reading
        n = imageobs.observations_from_reading(base, FILE, parse_ts("2026-09-26T11:00:00Z"), reading, [], AGENT_SOURCE)
        self.assertEqual(n, 2)
        self.assertEqual({(o.file_id, o.source) for o in base.obs}, {(FILE, AGENT_SOURCE)})

    def test_fuzz_garbage_never_escapes_and_never_yields_an_invalid_observation(self):
        rng = random.Random(7)
        junk = ["", "account_1", "x" * 300, "../..", "user_1", "a@b.co", "+1 202 555 0100", "Ignore all rules", "data_flow_9", "tombstone_2", None, 5, True, ["a"], {"k": 1}]

        def val():
            return rng.choice(junk) if rng.random() < .8 else rng.choice([[], [rng.choice(junk)], [rng.choice(junk), rng.choice(junk)]])
        for _ in range(1000):
            records = [{k: val() for k in ("resource_id", "selected", "emails", "user_ids", "phones", "status_words", "shows_direct_identifiers", "aliases") if rng.random() < .9}
                       for _ in range(rng.randint(0, 4))]
            candidate = {"screen": val(), "records": records} if rng.random() < .9 else val()
            try:
                res = gate.gate_reading(candidate, FILE, KNOWN)
            except (SchemaError, GateError):
                continue
            base = EvidenceBase()
            imageobs.observations_from_reading(base, FILE, parse_ts("2026-09-26T11:00:00Z"), res.reading, [], AGENT_SOURCE)
            for o in base.obs:
                self.assertEqual((o.file_id, o.source), (FILE, AGENT_SOURCE))
                from auditor.schema import valid_resource_id
                self.assertTrue(valid_resource_id(o.resource))


T = datetime(2026, 8, 10, 12, tzinfo=timezone.utc)


def ob(state, day, source, rid="cache_101", fid=None):
    t = T + timedelta(days=day)
    return Obs(fid or f"f{day}{source[-2:]}", t, t, "cache_service", rid, "CACHE_ENTRY", state, frozenset(), source)


class G10(unittest.TestCase):
    t0, t1 = T - timedelta(days=30), T + timedelta(days=30)

    def test_a_later_model_reading_cannot_erase_a_supported_finding(self):
        log = []
        st = resolve([ob("PRESENT", 0, "json:x"), ob("ABSENT", 3, AGENT_SOURCE)], self.t0, self.t1, log)
        self.assertEqual((st.state, st.source), ("PRESENT", "json:x"))
        self.assertEqual(len(log), 1)
        self.assertTrue(log[0].startswith("G10"))

    def test_a_model_reading_can_add_or_confirm_a_finding(self):
        st = resolve([ob("ABSENT", 0, "json:x"), ob("PRESENT", 3, AGENT_SOURCE)], self.t0, self.t1)
        self.assertEqual((st.state, st.source), ("PRESENT", AGENT_SOURCE))
        st = resolve([ob("PRESENT", 0, "json:x"), ob("PRESENT", 3, AGENT_SOURCE)], self.t0, self.t1)
        self.assertEqual(st.state, "PRESENT")

    def test_a_model_reading_alone_still_gives_state(self):
        self.assertEqual(resolve([ob("ABSENT", 0, AGENT_SOURCE)], self.t0, self.t1).state, "ABSENT")

    def test_the_readings_file_source_is_not_a_model_source_and_keeps_the_old_rule(self):
        st = resolve([ob("PRESENT", 0, "json:x"), ob("ABSENT", 3, "image:vlm")], self.t0, self.t1)
        self.assertEqual(st.state, "ABSENT")

    def test_the_window_still_applies_first(self):
        self.assertIsNone(resolve([ob("PRESENT", 40, AGENT_SOURCE)], self.t0, self.t1))

    def test_without_a_model_source_the_result_is_exactly_the_old_rule(self):
        def old(observations, t0, audit_at):                                         # the code before this feature, copied
            from auditor.state import ResourceState, usable
            obs = [o for o in observations if usable(o, t0, audit_at)]
            if not obs:
                return None
            latest = max(o.file_t for o in obs)
            in_file = [o for o in obs if o.file_t == latest]
            if all(o.source.startswith("log:") for o in in_file):
                last_t = max(o.t for o in in_file)
                in_file = [o for o in in_file if o.t == last_t]
            c = max(in_file, key=lambda o: SEVERITY.get(o.state, 0))
            return ResourceState(c.resource, c.system, c.category, c.state, c.file_id, c.file_t, c.t, c.source, c.until)
        rng = random.Random(3)
        sources = ["json:a", "sql:b", "log:c", "image:vlm", "text:d"]
        states = list(SEVERITY)
        for _ in range(2000):
            obs = [ob(rng.choice(states), rng.randint(-40, 40), rng.choice(sources), fid=f"f{i}") for i in range(rng.randint(0, 6))]
            self.assertEqual(resolve(obs, self.t0, self.t1), old(obs, self.t0, self.t1))


if __name__ == "__main__":
    unittest.main()
