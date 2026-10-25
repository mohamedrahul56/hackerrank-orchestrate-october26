"""The documents and reports must say, in the same words, that the agent layer was never run against a real model."""
import re
import unittest

from tests._common import ROOT

DOCS = ["README.md", "ARCHITECTURE.md", "code/NOTES.md", "code/INTERVIEW_PREP.md", "code/evaluation/usage_report.md",
        "code/evaluation/evaluation_report.md", "code/evaluation/agent_layer_report.md"]


def text(rel):
    return re.sub(r"\s+", " ", (ROOT / rel).read_text(encoding="utf-8")).lower()


class AgentLayerIsDescribedHonestly(unittest.TestCase):
    def test_every_document_says_fakes_only_and_zero_calls_tokens_cost(self):
        for rel in DOCS:
            with self.subTest(doc=rel):
                t = text(rel)
                self.assertIn("with fakes only", t)
                self.assertRegex(t, r"0 (model )?calls")
                self.assertIn("0 tokens", t)
                self.assertIn("no cost", t)

    def test_readme_states_why_the_default_path_cannot_change(self):
        t = text("README.md")
        self.assertIn("byte-identical", t)
        self.assertIn("g10", t)
        self.assertIn("image:agent", t)


if __name__ == "__main__":
    unittest.main()
