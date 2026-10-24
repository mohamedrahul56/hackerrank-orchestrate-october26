"""The mutation check (tools/mutation_check.py) is only meaningful while each mutation still applies to the current source."""
import importlib.util
import sys
import unittest

from tests._common import *  # noqa: F401,F403

spec = importlib.util.spec_from_file_location("mutation_tool", ROOT / "code" / "tools" / "mutation_check.py")
mut = importlib.util.module_from_spec(spec)
sys.modules["mutation_tool"] = mut                     # dataclasses look their module up here
spec.loader.exec_module(mut)


class MutationDefinitions(unittest.TestCase):
    def test_every_mutation_applies_exactly_once_and_changes_something(self):
        for m in mut.MUTATIONS:
            text = (ROOT / m.file).read_text(encoding="utf-8")
            self.assertEqual(text.count(m.old), 1, m.key)
            self.assertNotEqual(m.old, m.new, m.key)

    def test_three_primary_mutations_with_distinct_keys(self):
        self.assertEqual(sum(1 for m in mut.MUTATIONS if m.primary), 3)
        self.assertEqual(len({m.key for m in mut.MUTATIONS}), len(mut.MUTATIONS))

    def test_the_mutation_test_set_exists(self):
        for module in mut.TEST_MODULES:
            self.assertTrue((ROOT / "code" / (module.replace(".", "/") + ".py")).exists(), module)


if __name__ == "__main__":
    unittest.main()
