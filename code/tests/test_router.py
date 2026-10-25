"""The per-file router: the decision is code, images are the only files that can reach a model, and the router changes no observation."""
import tempfile
import unittest
from pathlib import Path

from tests._common import ROOT
from auditor.evidence import router
from auditor.evidence.load import load_evidence

import router_equivalence as eq

DATASET = ROOT / "dataset"
PNG = b"\x89PNG\r\n\x1a\n" + b"\0" * 24
READING = {"screen": "x", "records": [{"resource_id": "search_1", "selected": True, "emails": [], "user_ids": ["user_1"], "phones": [], "status_words": ["Live"],
                                       "shows_direct_identifiers": False, "aliases": []}]}


def make_root(files: dict[str, bytes]) -> Path:
    tmp = tempfile.TemporaryDirectory()
    root = Path(tmp.name)
    rows = ["file_id,file_path,origin,captured_at"]
    for i, (name, data) in enumerate(files.items()):
        p = root / name
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_bytes(data)
        rows.append(f"evidence_{i:02d},{name},synthetic,2026-08-12T00:00:00Z")
    (root / "evidence_files.csv").write_text("\n".join(rows) + "\n")
    make_root.keep.append(tmp)
    return root


make_root.keep = []


class RouteDecision(unittest.TestCase):
    def route(self, name, data=b"x", reader=True):
        root = make_root({name: data})
        return router.route_file("evidence_00", root / name, reader)

    def test_each_format_goes_to_its_deterministic_parser(self):
        for name, handler in (("a.sql", "sql"), ("a.json", "json"), ("a.txt", "text"), ("a.log", "log"), ("A.PDF", "pdf")):
            with self.subTest(name=name):
                r = self.route(name)
                self.assertEqual((r.handler, r.reader), (handler, "parser"))

    def test_only_images_can_reach_the_image_reader_and_only_when_one_is_configured(self):
        self.assertEqual((self.route("s.png", PNG).handler, self.route("s.png", PNG).reader), ("image", "image_reader"))
        r = self.route("s.png", PNG, reader=False)
        self.assertEqual((r.handler, r.reader), ("image", "none"))
        self.assertEqual(r.reason, "image not read (no image extractor configured)")
        for name in ("a.sql", "a.json", "a.txt", "a.log"):
            self.assertEqual(self.route(name, reader=True).reader, "parser")

    def test_unsupported_suffix_is_not_read_and_says_why(self):
        r = self.route("a.xlsx")
        self.assertEqual((r.handler, r.reader, r.reason), ("none", "none", "unsupported file type .xlsx"))

    def test_an_unambiguous_signature_beats_the_name(self):
        r = self.route("report.json", b"%PDF-1.7 ...")
        self.assertEqual((r.handler, r.reader), ("pdf", "parser"))
        self.assertIn("content is a PDF", r.reason)
        r = self.route("shot.txt", PNG)
        self.assertEqual((r.handler, r.reader), ("image", "image_reader"))

    def test_text_formats_are_never_guessed_from_content(self):
        self.assertEqual(self.route("a.txt", b'{"a": 1}').handler, "text")
        self.assertEqual(self.route("a.log", b"SELECT 1;").handler, "log")

    def test_a_png_without_the_signature_still_goes_to_the_reader_which_refuses_it_itself(self):
        r = self.route("shot.png", b"not a png")
        self.assertEqual((r.handler, r.reader), ("image", "image_reader"))
        self.assertIn("signature is missing", r.reason)

    def test_a_missing_file_is_routed_by_its_name_and_reported_by_the_handler(self):
        r = router.route_file("evidence_00", Path("/nonexistent/a.json"), True)
        self.assertEqual((r.handler, r.reader), ("json", "parser"))


class RoutingInTheLoader(unittest.TestCase):
    def test_the_extractor_is_called_for_images_only(self):
        root = make_root({"a.json": b'{"search_1": 1}', "a.txt": b"hello", "s.png": PNG, "t.png": PNG, "a.sql": b"select 1;"})
        seen = []

        def ex(path, *args):
            seen.append(Path(path).suffix)
            return READING

        base = load_evidence(root, True, ex)
        self.assertEqual(sorted(seen), [".png", ".png"])
        self.assertEqual({r.route.file_id: r.route.reader for r in base.routes}["evidence_02"], "image_reader")

    def test_every_manifest_file_gets_a_route_and_an_outcome(self):
        root = make_root({"s.png": PNG, "a.bin": b"x", "bad.json": b"{not json"})
        base = load_evidence(root, True, None)
        by = {r.route.file_id: r for r in base.routes}
        self.assertEqual(len(base.routes), 3)
        self.assertEqual(by["evidence_00"].outcome, "not read")
        self.assertEqual(by["evidence_01"].outcome, "not read")
        self.assertIn(by["evidence_02"].outcome, ("error", "no records", "not read"))
        self.assertTrue(any("unsupported file type .bin" in w for w in base.warnings))

    def test_a_reader_that_fails_leaves_the_file_not_read_and_the_rest_intact(self):
        from auditor.llm import LLMUnavailable
        root = make_root({"s.png": PNG, "a.json": b'{"x": 1}'})

        def ex(path, *args):
            raise LLMUnavailable("no key")

        base = load_evidence(root, True, ex)
        self.assertEqual({r.route.file_id: r.outcome for r in base.routes}["evidence_00"], "not read")
        self.assertTrue(any("not read" in w and "no key" in w for w in base.warnings))

    def test_summary_lines_list_the_table_and_the_files_that_gave_nothing(self):
        root = make_root({"s.png": PNG, "a.bin": b"x"})
        text = "\n".join(router.summary_lines(load_evidence(root, True, None).routes))
        self.assertIn("| handler | reader | outcome | files |", text)
        self.assertIn("evidence_01 (none, not read): unsupported file type .bin", text)


class RouterChangesNoObservation(unittest.TestCase):
    """The old suffix dispatch (frozen in evaluation/router_equivalence.py) and the router give identical observations, coverage and warnings."""

    def test_synthetic_folder_with_odd_files(self):
        root = make_root({"s.png": PNG, "a.bin": b"x", "bad.json": b"{not json", "ok.json": b'{"search_1": {"email": "a@example.test"}}', "r.log": b"", "t.txt": b"search_1"})
        for reader, ex in ((False, None), (True, lambda p, *a: READING)):
            for workers in (1, 3):
                with self.subTest(reader=reader, workers=workers):
                    self.assertEqual(eq.compare(root, reader, ex, workers)["differences"], [])

    @unittest.skipUnless((DATASET / "evidence_files.csv").exists(), "dataset/ is not shipped in code.zip")
    def test_all_94_files_of_the_dataset(self):
        for reader, ex, workers in ((False, None, 1), (True, lambda p, *a: READING, 1), (True, lambda p, *a: READING, 4)):
            with self.subTest(reader=reader, workers=workers):
                result = eq.compare(DATASET, reader, ex, workers)
                self.assertEqual(result["differences"], [])
                self.assertEqual((result["files"], result["routes"]), (94, 94))
                self.assertGreater(result["observations"], 2000)


if __name__ == "__main__":
    unittest.main()
