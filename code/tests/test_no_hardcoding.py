"""No hardcoded labels: executable code must not mention case ids, and must not contain any resource id that appears in
the labeled sample answers. (Tests and the annotated sentence audit are excluded: they are fixtures, not the solution.)"""
import csv
import json
import re
import unittest

from tests._common import DATASET, ROOT, needs_dataset

CODE_FILES = [p for p in (ROOT / "code").rglob("*.py")
              if "tests" not in p.parts and "__pycache__" not in p.parts]


class HardcodingScan(unittest.TestCase):
    def test_there_is_code_to_scan(self):
        self.assertGreater(len(CODE_FILES), 10)

    def test_no_case_id_literals(self):
        hits = [(str(p.relative_to(ROOT)), m.group(0)) for p in CODE_FILES for m in re.finditer(r"\bcase_\d+\b", p.read_text(encoding="utf-8"))]
        self.assertEqual(hits, [])

    @needs_dataset
    def test_no_sample_answer_fragments(self):
        gold_ids = set()
        with (DATASET / "sample_output.csv").open(encoding="utf-8") as fh:
            for r in csv.DictReader(fh):
                gold_ids |= {s["resource_id"] for s in json.loads(r["steps_json"])}
        evidence_ids = set()
        with (DATASET / "evidence_files.csv").open(encoding="utf-8") as fh:
            evidence_ids = {r["file_id"] for r in csv.DictReader(fh)}
        text = {p: p.read_text(encoding="utf-8") for p in CODE_FILES}
        hits = [(str(p.relative_to(ROOT)), g) for p, t in text.items() for g in gold_ids if re.search(rf"\b{re.escape(g)}\b", t)]
        hits += [(str(p.relative_to(ROOT)), e) for p, t in text.items() for e in evidence_ids if re.search(rf"\b{re.escape(e)}\b", t)]
        self.assertEqual(hits, [])


if __name__ == "__main__":
    unittest.main()
