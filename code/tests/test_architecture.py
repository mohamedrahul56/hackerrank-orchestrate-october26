"""ARCHITECTURE.md must stay true: its performance table is the generated report's, and the modules and tests it names exist."""
import re
import unittest

from tests._common import *  # noqa: F401,F403
from performance_report import MARK, wall_time_table

DOC = ROOT / "ARCHITECTURE.md"
REPORT = ROOT / "code" / "evaluation" / "performance_report.md"


class ArchitectureDoc(unittest.TestCase):
    text = DOC.read_text(encoding="utf-8") if DOC.exists() else ""

    def test_the_document_has_every_requested_part(self):
        for needle in ("```mermaid", "flowchart", "## 2. Modules", "## 3. Failure modes", "## 4. Measured performance", "### D1.", "### D2.", "### D3.", "### D4.", "### D5."):
            self.assertIn(needle, self.text)

    def test_performance_table_is_copied_verbatim_from_the_generated_report(self):
        a, b = self.text.index(MARK[0]) + len(MARK[0]), self.text.index(MARK[1])
        self.assertEqual(self.text[a:b].strip(), wall_time_table(REPORT.read_text(encoding="utf-8")),
                         "re-run `python3 code/evaluation/performance_report.py --before ... --after ...` (it rewrites both files)")

    def test_the_numbers_quoted_in_prose_are_in_the_generated_report(self):
        report = REPORT.read_text(encoding="utf-8")
        for needle in ("35.5x", "16.0x", "| x20 | 442 |", "| x40 | 445 |"):
            self.assertIn(needle, report.replace("| x20 | 442 | ", "| x20 | 442 |").replace("| x40 | 445 | ", "| x40 | 445 |"), needle)

    @unittest.skipUnless((ROOT / "dataset" / "deletion_requests.csv").exists(), "dataset/ not present")
    def test_the_example_case_id_in_the_makefile_and_readme_exists(self):
        ids = {l.split(",")[0] for l in (ROOT / "dataset" / "deletion_requests.csv").read_text(encoding="utf-8").splitlines()}
        for name in ("Makefile", "README.md"):
            for cid in re.findall(r"(?:CASE=|--explain )(case_\d+)", (ROOT / name).read_text(encoding="utf-8")):
                self.assertIn(cid, ids, f"{name} shows an example case id that does not exist")

    def test_every_auditor_module_is_in_the_module_table(self):
        section = self.text.split("## 2. Modules")[1].split("## 3.")[0]
        for p in sorted((ROOT / "code" / "auditor").glob("*.py")):
            if p.name != "__init__.py":
                self.assertIn(p.stem, section, f"{p.name} is not mentioned in the module table")
        for p in sorted((ROOT / "code" / "auditor" / "agent").glob("*.py")):
            if p.name != "__init__.py":
                self.assertIn(p.stem, section, f"agent/{p.name} is not mentioned in the module table")
        for p in sorted((ROOT / "code" / "auditor" / "evidence").glob("*.py")):
            if p.name != "__init__.py" and p.stem not in ("loaders", "logobs", "logparse", "pdfobs", "records"):      # covered by the row "auditor/evidence/*"
                self.assertIn(p.stem, section, f"{p.name} is not mentioned in the module table")

    def test_every_test_module_named_in_the_failure_table_exists(self):
        section = self.text.split("## 3. Failure modes")[1].split("## 4.")[0]
        named = set(re.findall(r"`(test_[a-z_]+)`", section))
        self.assertGreaterEqual(len(named), 6)
        for n in named:
            self.assertTrue((ROOT / "code" / "tests" / f"{n}.py").exists(), n)

    def test_every_decision_record_states_its_risk_or_cost(self):
        for d in ("D2", "D3", "D4", "D5"):
            body = self.text.split(f"### {d}.")[1].split("### D")[0]
            self.assertRegex(body, r"\*\*(Cost|Risk|Rejected)\.?\*\*", d)


if __name__ == "__main__":
    unittest.main()
