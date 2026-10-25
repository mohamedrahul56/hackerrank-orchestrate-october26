"""The two tools of the screenshot reader: schemas, bounds, determinism, and the soft failure without Pillow."""
import io
import tempfile
import unittest
from pathlib import Path
from unittest import mock

from tests._common import *  # noqa: F401,F403
from auditor.agent import tools
from auditor.evidence import imageobs
from auditor.llm import SchemaError, validate_schema

PIL = unittest.skipUnless(tools.available(), "Pillow not installed")


def make_png(path: Path, w=400, h=300) -> Path:
    from PIL import Image
    im = Image.new("RGB", (w, h), (250, 250, 250))
    for x in range(0, w, 10):
        for y in range(0, h, 10):
            im.putpixel((x, y), ((x * 7) % 256, (y * 5) % 256, 90))
    im.save(path)
    return path


class Specs(unittest.TestCase):
    def test_each_tool_has_a_name_a_precise_description_and_a_schema(self):
        for t in (tools.ZOOM_TOOL, tools.SUBMIT_TOOL):
            self.assertEqual(set(t), {"name", "description", "input_schema"})
            self.assertGreater(len(t["description"]), 120)
            self.assertEqual(t["input_schema"]["type"], "object")
            self.assertFalse(t["input_schema"]["additionalProperties"])
        self.assertEqual(tools.ZOOM_TOOL["name"], "zoom_region")
        self.assertEqual(tools.SUBMIT_TOOL["name"], "submit_reading")
        self.assertIn(str(tools.MIN_SIDE), tools.ZOOM_TOOL["description"])
        self.assertNotIn("file", tools.ZOOM_SCHEMA["properties"], "the zoom tool cannot name any file: it only ever sees the screenshot being read")

    def test_submit_schema_is_the_reading_schema_plus_unknown(self):
        self.assertEqual(set(tools.SUBMIT_SCHEMA["properties"]) - set(imageobs.SCHEMA["properties"]), {"unknown"})
        self.assertEqual(tools.SUBMIT_SCHEMA["required"], ["screen", "records"])
        validate_schema({"screen": "s", "records": []}, tools.SUBMIT_SCHEMA)
        validate_schema({"screen": "s", "records": [], "unknown": [{"record_index": 0, "field": "emails", "reason": "r"}]}, tools.SUBMIT_SCHEMA)
        with self.assertRaises(SchemaError):
            validate_schema({"screen": "s", "records": [], "unknown": [{"record_index": "0", "field": "emails", "reason": "r"}]}, tools.SUBMIT_SCHEMA)

    def test_the_digest_is_stable_and_changes_with_a_description(self):
        a = tools.tools_digest()
        self.assertEqual(a, tools.tools_digest())
        with mock.patch.dict(tools.ZOOM_TOOL, {"description": tools.ZOOM_TOOL["description"] + " Also do X."}):
            self.assertNotEqual(a, tools.tools_digest())

    def test_without_pillow_zoom_is_not_offered_and_a_warning_is_logged(self):
        with mock.patch.object(tools, "Image", None):
            self.assertFalse(tools.available())
            with self.assertLogs("auditor.agent.tools", level="WARNING") as cm:
                specs = tools.tool_specs()
            self.assertEqual([t["name"] for t in specs], ["submit_reading"])
            self.assertIn("Pillow is not installed", cm.output[0])
            with tempfile.TemporaryDirectory() as d:
                p = Path(d) / "a.png"
                p.write_bytes(b"x")
                with self.assertRaises(tools.ToolError):
                    tools.zoom_region(p, {"x": 0, "y": 0, "width": 40, "height": 40})

    @PIL
    def test_with_pillow_both_tools_are_offered(self):
        self.assertEqual([t["name"] for t in tools.tool_specs()], ["zoom_region", "submit_reading"])


@PIL
class Zoom(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.png = make_png(Path(self.tmp.name) / "a.png")

    def tearDown(self):
        self.tmp.cleanup()

    def test_image_size_comes_from_the_header(self):
        self.assertEqual(tools.image_size(self.png), (400, 300))
        bad = Path(self.tmp.name) / "b.png"
        bad.write_bytes(b"not a png at all, just text")
        with self.assertRaises(tools.ToolError):
            tools.image_size(bad)

    def test_a_crop_is_enlarged_within_the_limit_and_is_a_png(self):
        from PIL import Image
        data, media = tools.zoom_region(self.png, {"x": 10, "y": 20, "width": 100, "height": 50}, max_px=600)
        self.assertEqual(media, "image/png")
        im = Image.open(io.BytesIO(data))
        self.assertEqual(im.size, (400, 200))                                  # scale min(600/100, 4.0) = 4
        data2, _ = tools.zoom_region(self.png, {"x": 0, "y": 0, "width": 400, "height": 300}, max_px=300)
        self.assertLessEqual(max(Image.open(io.BytesIO(data2)).size), 400)

    def test_zoom_is_deterministic(self):
        args = {"x": 5, "y": 5, "width": 120, "height": 80}
        self.assertEqual(tools.zoom_region(self.png, args), tools.zoom_region(self.png, args))

    def test_bounds(self):
        for args in ({"x": -1, "y": 0, "width": 50, "height": 50}, {"x": 0, "y": 0, "width": 31, "height": 50}, {"x": 380, "y": 0, "width": 50, "height": 50},
                     {"x": 0, "y": 290, "width": 50, "height": 50}, {"x": 0, "y": 0, "width": 4000, "height": 4000}):
            with self.assertRaises(tools.ToolError, msg=str(args)):
                tools.zoom_region(self.png, args)

    def test_a_huge_area_is_refused_before_any_work(self):
        big = Path(self.tmp.name) / "big.png"
        from PIL import Image
        Image.new("RGB", (3000, 2000), (1, 2, 3)).save(big)
        with self.assertRaises(tools.ToolError):
            tools.zoom_region(big, {"x": 0, "y": 0, "width": 3000, "height": 2000})


if __name__ == "__main__":
    unittest.main()
