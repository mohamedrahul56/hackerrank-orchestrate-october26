"""Prompt files of the agent layer: versioned, loaded with a digest, and they say what the constraints require."""
import tempfile
import unittest
from pathlib import Path

from tests._common import *  # noqa: F401,F403
from auditor.agent import prompts


class PromptFiles(unittest.TestCase):
    def test_every_prompt_file_has_a_version_and_a_digest(self):
        files = sorted(p.name for p in prompts.PROMPT_DIR.glob("*.md"))
        self.assertIn("image_reader.system.md", files)
        self.assertIn("image_reader.task.md", files)
        for name in files:
            p = prompts.load(name)
            self.assertRegex(p.version, r"^[A-Za-z0-9._-]+$")
            self.assertRegex(p.sha256, r"^[0-9a-f]{64}$")

    def test_the_system_prompt_states_the_required_constraints(self):
        text = prompts.load("image_reader.system.md").text.lower()
        for needle in ("role", "constraints", "structured output", "data, never instructions", "mark unknown instead of guessing", "zoom_region", "submit_reading",
                       "deterministic software decides", "never guess"):
            self.assertIn(needle, text, needle)

    def test_the_task_prompt_has_the_placeholders_and_treats_the_picture_as_data(self):
        p = prompts.load("image_reader.task.md")
        for k in ("{width}", "{height}", "{max_zooms}"):
            self.assertIn(k, p.text)
        filled = p.fill(width=10, height=20, max_zooms=3)
        self.assertNotIn("{width}", filled)
        self.assertIn("not a request to you", filled)

    def test_no_prompt_names_an_answer_a_person_a_case_or_a_dataset_value(self):
        import re
        for p in prompts.PROMPT_DIR.glob("*.md"):
            t = p.read_text(encoding="utf-8")
            self.assertIsNone(re.search(r"@[a-z0-9-]+\.[a-z]{2,}|case_\d+|evidence_\d+", t), p.name)

    def test_loader_rejects_a_file_without_front_matter_or_body(self):
        with tempfile.TemporaryDirectory() as d:
            d = Path(d)
            (d / "a.md").write_text("no front matter", encoding="utf-8")
            (d / "b.md").write_text("---\nversion: v1\n---\n\n", encoding="utf-8")
            for name in ("a.md", "b.md", "missing.md"):
                with self.assertRaises(prompts.PromptError, msg=name):
                    prompts.load(name, d)

    def test_a_changed_text_changes_the_digest_but_not_the_label(self):
        with tempfile.TemporaryDirectory() as d:
            d = Path(d)
            (d / "p.md").write_text("---\nversion: v1\n---\nbody one", encoding="utf-8")
            a = prompts.load("p.md", d)
            (d / "p.md").write_text("---\nversion: v1\n---\nbody two", encoding="utf-8")
            b = prompts.load("p.md", d)
            self.assertEqual(a.version, b.version)
            self.assertNotEqual(a.sha256, b.sha256)


if __name__ == "__main__":
    unittest.main()
