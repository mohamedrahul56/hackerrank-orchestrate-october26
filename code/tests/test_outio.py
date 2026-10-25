import csv
import json
import tempfile
import unittest
from pathlib import Path

from tests._common import DATASET, needs_dataset
from auditor.dataset import load_dataset
from auditor.outio import dumps_steps, write_output
from auditor.validate import validate_output_file
from score import read_rows


class OutIoTests(unittest.TestCase):
    def test_compact_json_and_quoting_roundtrip(self):
        steps = [{"step_no": 1, "phase": "FINDING", "system_id": "s", "resource_id": "r", "operation": "REPORT_REMAINING_RECORD", "state": "PRESENT", "evidence_id": "e"}]
        self.assertNotIn(" ", dumps_steps(steps))
        self.assertEqual(dumps_steps([]), "[]")
        with tempfile.TemporaryDirectory() as d:
            p = Path(d) / "o.csv"
            write_output(p, [("c1", "INCOMPLETE", steps), ("c2", "NOT_APPLICABLE", [])])
            raw = p.read_bytes()
            self.assertNotIn(b"\r", raw)                           # LF only, like the samples
            with p.open(newline="", encoding="utf-8") as fh:
                rows = list(csv.reader(fh))
        self.assertEqual(rows[0], ["case_id", "deletion_status", "steps_json"])
        self.assertEqual(json.loads(rows[1][2]), steps)
        self.assertEqual(rows[2], ["c2", "NOT_APPLICABLE", "[]"])

    @needs_dataset
    def test_written_gold_revalidates_and_matches_sample_file(self):
        ds = load_dataset(DATASET, "sample_deletion_requests.csv")
        gold = read_rows(DATASET / "sample_output.csv")
        with tempfile.TemporaryDirectory() as d:
            p = Path(d) / "o.csv"
            write_output(p, [(r.case_id, gold[r.case_id].status, gold[r.case_id].steps) for r in ds.requests])
            self.assertEqual(validate_output_file(p, ds), [])
            again = read_rows(p)
        self.assertEqual({c: (r.status, r.steps) for c, r in again.items()}, {c: (r.status, r.steps) for c, r in gold.items()})

    @needs_dataset
    def test_template_header_matches(self):
        header = (DATASET / "output.csv").read_text(encoding="utf-8").splitlines()[0]
        self.assertEqual(header, "case_id,deletion_status,steps_json")


if __name__ == "__main__":
    unittest.main()
