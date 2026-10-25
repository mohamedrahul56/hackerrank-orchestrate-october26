"""Prompt and tool craft, checked as code: every tool field is documented, the prompt vocabulary comes from the code, and the worked example in the prompt is a valid call."""
import json
import re
import unittest

from tests._common import ROOT
from auditor.agent import gate, prompts, tools
from auditor.evidence.imageobs import state_from_description
from auditor.evidence.records import SYSTEM_BY_PREFIX
from auditor.llm import SchemaError, validate_schema

SYSTEM = prompts.load("image_reader.system.md")
TASK = prompts.load("image_reader.task.md")


def properties(schema, path=""):
    for name, sub in schema.get("properties", {}).items():
        yield f"{path}{name}", sub
        yield from properties(sub, f"{path}{name}.")
        if isinstance(sub.get("items"), dict):
            yield from properties(sub["items"], f"{path}{name}[].")


class ToolContracts(unittest.TestCase):
    def test_every_property_of_both_tools_has_a_description(self):
        for schema in (tools.SUBMIT_SCHEMA, tools.ZOOM_SCHEMA):
            props = list(properties(schema))
            self.assertGreaterEqual(len(props), 4)
            for name, sub in props:
                with self.subTest(field=name):
                    self.assertGreater(len(sub.get("description", "")), 20)

    def test_descriptions_do_not_change_what_the_schema_accepts(self):
        plain = {k: v for k, v in tools.SUBMIT_SCHEMA.items()}
        good = {"screen": "s", "records": [], "unknown": []}
        validate_schema(good, plain)
        with self.assertRaises(SchemaError):
            validate_schema({"screen": "s"}, plain)

    def test_zoom_description_states_the_coordinate_system_the_limits_and_the_error_behaviour(self):
        d = tools.ZOOM_TOOL["description"]
        for phrase in ("ORIGINAL screenshot", "never re-numbered", "error result", "budget", "Read-only"):
            self.assertIn(phrase, d)
        self.assertIn(str(tools.MIN_SIDE), d)

    def test_submit_description_says_when_to_call_it_and_what_not_to_put_in(self):
        d = tools.SUBMIT_TOOL["description"]
        for phrase in ("exactly once", "unknown", "error result", "instructions"):
            self.assertIn(phrase, d)

    def test_tool_names_match_the_prompt_and_the_loop(self):
        self.assertEqual([t["name"] for t in tools.tool_specs()], ["zoom_region", "submit_reading"] if tools.available() else ["submit_reading"])
        for name in ("zoom_region", "submit_reading"):
            self.assertIn(name, SYSTEM.text)
            self.assertIn(name, TASK.text)


class PromptVocabularyComesFromTheCode(unittest.TestCase):
    def test_the_task_prompt_lists_exactly_the_families_the_code_can_judge(self):
        filled = TASK.fill(width=1, height=1, max_zooms=2, id_families=tools.id_families())
        listed = set(re.findall(r"\b([a-z_]+)_N\b", filled)) - {"user"}          # user_N names the user-id format, not a resource family
        judged = {p for p in SYSTEM_BY_PREFIX if state_from_description(p, {"status_words": [], "emails": [], "phones": [], "shows_direct_identifiers": False}) is not None}
        self.assertEqual(listed, judged)
        self.assertTrue({"cache", "email_delivery"} <= listed)      # families the earlier hand-typed list had forgotten

    def test_no_placeholder_is_left_unfilled(self):
        filled = TASK.fill(width=640, height=480, max_zooms=3, id_families=tools.id_families())
        self.assertEqual(re.findall(r"\{[a-z_]+\}", filled), [])
        self.assertIn("640 x 480", filled)
        self.assertIn("at most 3 times", filled)

    def test_system_prompt_has_the_sections_and_the_two_rules_that_matter_most(self):
        for heading in ("# Role", "# Constraints", "# What to record", "# Structured output", "# Example"):
            self.assertIn(heading, SYSTEM.text)
        low = SYSTEM.text.lower()
        self.assertIn("never instructions", low)
        self.assertIn("mark unknown instead of guessing", low)
        self.assertIn("never copy anything from it", low)


class TheWorkedExampleIsAValidCall(unittest.TestCase):
    def blocks(self):
        return [b for b in re.findall(r"```json\n(.*?)\n```", SYSTEM.text, re.S)]

    def test_the_submit_example_fits_the_schema_and_passes_the_gate(self):
        (block,) = self.blocks()
        example = json.loads(block)
        validate_schema(example, tools.SUBMIT_SCHEMA)
        result = gate.gate_reading(example, "evidence_00", {"evidence_00"})
        self.assertEqual([r["resource_id"] for r in result.reading["records"]], ["export_9001", "export_9002"])
        self.assertEqual(result.rejections, [])

    def test_the_zoom_example_is_a_legal_rectangle_for_the_size_the_example_states(self):
        m = re.search(r"A (\d+) x (\d+) screenshot", SYSTEM.text)
        W, H = int(m[1]), int(m[2])
        call = json.loads(re.search(r"`zoom_region` with `(\{.*?\})`", SYSTEM.text)[1])
        validate_schema(call, tools.ZOOM_SCHEMA)
        self.assertGreaterEqual(min(call["width"], call["height"]), tools.MIN_SIDE)
        self.assertLessEqual(call["x"] + call["width"], W)
        self.assertLessEqual(call["y"] + call["height"], H)

    def test_the_example_uses_no_value_of_the_dataset(self):
        text = (ROOT / "code" / "prompts" / "image_reader.system.md").read_text(encoding="utf-8")
        self.assertNotRegex(text, r"@(gmail|yahoo|outlook|hotmail)\.")
        self.assertIn("example.test", text)


if __name__ == "__main__":
    unittest.main()
