"""The two failure-table rows of ARCHITECTURE.md that no other test covers: an unknown email, and a written file that fails validation."""
import importlib.util
import io
import tempfile
import unittest
from contextlib import redirect_stderr
from pathlib import Path
from unittest import mock

from tests._common import DATASET, ROOT
from auditor.audit import Auditor, Config
from auditor.validate import Issue


@unittest.skipUnless(DATASET.exists(), "dataset/ not present")
class FailureModes(unittest.TestCase):
    def test_an_email_that_matches_nobody_gets_a_fallback_row(self):
        a = Auditor(DATASET, Config())
        cid = next(c for c, cls in a.classification.items() if cls.label == "DELETION" and a.users[c])
        a.users[cid] = set()                                      # what the resolver returns for an address no identity ever had
        r = a.audit(cid)
        self.assertEqual((r.status, r.steps), ("NOT_APPLICABLE", []))
        self.assertTrue(r.notes[0].startswith("FALLBACK: request email matches no known identity"))

    def test_validator_errors_give_exit_code_1_and_keep_the_checkpoint(self):
        spec = importlib.util.spec_from_file_location("auditor_cli_fm", ROOT / "code" / "main.py")
        cli = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cli)
        with tempfile.TemporaryDirectory() as d:
            out = Path(d) / "o.csv"
            err = io.StringIO()
            with mock.patch.object(cli, "validate_output_file", return_value=[Issue("case_1", "X", "forced for the test")]), redirect_stderr(err):
                rc = cli.main(["--requests", "sample_deletion_requests.csv", "--out", str(out), "--images", "off"])
            self.assertEqual(rc, 1)
            self.assertIn("forced for the test", err.getvalue())
            self.assertTrue(out.with_suffix(".partial.jsonl").exists(), "kept for inspection when validation failed")
            with redirect_stderr(io.StringIO()):
                self.assertEqual(cli.main(["--requests", "sample_deletion_requests.csv", "--out", str(out), "--images", "off"]), 0)
            self.assertFalse(out.with_suffix(".partial.jsonl").exists(), "a clean run removes it")


if __name__ == "__main__":
    unittest.main()


@unittest.skipUnless(DATASET.exists(), "dataset/ not present")
class HoldoutToolStillRuns(unittest.TestCase):
    """The hold-out measurement reads the discovery's inputs from the auditor; a changed tuple once broke it silently (no test ran it)."""

    def test_one_seed_of_each_holdout_runs_and_never_assigns_a_wrong_resource(self):
        import holdout
        a = Auditor(DATASET, Config())
        for res in (holdout.account_holdout(a, 0.3, range(1)), holdout.analytics_holdout(a, 0.3, range(1))):
            self.assertGreater(res.n, 0)
            self.assertEqual(res.counts["wrong"] + res.counts["stolen"], 0)
            self.assertGreater(res.counts["correct"], 0)
