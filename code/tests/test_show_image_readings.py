"""show_image_readings: with a fake model the page shows the picture, the raw JSON and the derived observations; without a key it says what to do."""
import contextlib
import io
import os
import tempfile
import unittest
from pathlib import Path

from tests._common import *  # noqa: F401,F403
import show_image_readings as sir
from tests.test_llm_images import GOOD


class ShowImageReadings(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        (self.root / "evidence").mkdir()
        (self.root / "evidence" / "shot.png").write_bytes(b"\x89PNG\r\n fake image bytes")
        (self.root / "evidence_files.csv").write_text("file_id,file_path,origin,captured_at\n"
                                                      "evidence_91,evidence/shot.png,connector_console,2026-08-10T12:00:00Z\n"
                                                      "evidence_92,evidence/notes.txt,other,2026-08-10T12:00:00Z\n", encoding="utf-8")
        (self.root / "system_activity.csv").write_text("activity_id,event_time,event_type,source_system,target_system,resource_id,reported_result,details\n", encoding="utf-8")

    def tearDown(self):
        self.tmp.cleanup()

    def test_only_pngs_are_listed_and_each_gets_picture_json_and_observations(self):
        items = sir.read_all(self.root, lambda *a: GOOD)
        self.assertEqual([i["row"]["file_id"] for i in items], ["evidence_91"])
        page = sir.render_html(items)
        self.assertIn("data:image/png;base64,", page)
        self.assertIn("data_flow_1121", page)
        self.assertIn("ACTIVE", page)                                  # derived from the status words "Healthy, Continuous"
        text = sir.render_text(items)
        self.assertIn("data_flow_1121", text)
        self.assertIn("--- derived observations:", text)

    def test_a_failed_reading_is_shown_not_raised(self):
        def boom(*a):
            raise RuntimeError("model unavailable")
        items = sir.read_all(self.root, boom)
        self.assertIn("model unavailable", items[0]["error"])
        self.assertIn("READING FAILED", sir.render_html(items))

    def test_model_text_is_escaped_in_the_page(self):
        evil = {"screen": "<script>alert(1)</script>", "records": [{"resource_id": "<img src=x onerror=alert(1)>", "selected": False, "emails": [], "user_ids": [],
                                                                      "phones": [], "status_words": [], "shows_direct_identifiers": False, "aliases": []}]}
        page = sir.render_html(sir.read_all(self.root, lambda *a: evil))
        self.assertNotIn("<script>alert(1)", page)

    def test_without_a_key_the_script_explains_and_exits_2(self):
        saved = {k: os.environ.pop(k) for k in ("ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN") if k in os.environ}
        err = io.StringIO()
        try:
            with contextlib.redirect_stderr(err):
                code = sir.main(["--dataset", str(self.root)])
        finally:
            os.environ.update(saved)
        self.assertEqual(code, 2)
        self.assertIn("export ANTHROPIC_API_KEY", err.getvalue())
        self.assertIn("never written to a file", err.getvalue())


if __name__ == "__main__":
    unittest.main()
