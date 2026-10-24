"""Determinism across environments that must not matter: string hash seed, working directory, locale / default encoding, time zone."""
import importlib.util
import tempfile
import unittest
from pathlib import Path

from tests._common import *  # noqa: F401,F403

spec = importlib.util.spec_from_file_location("determinism_tool", ROOT / "code" / "tools" / "determinism_check.py")
det = importlib.util.module_from_spec(spec)
spec.loader.exec_module(det)


@needs_dataset
class RealPipeline(unittest.TestCase):
    def test_output_notes_and_usage_log_are_byte_identical_across_the_environment_matrix(self):
        with tempfile.TemporaryDirectory() as d:
            runs = det.run_matrix(ROOT, DATASET, Path(d), full=False)          # 3 hash seeds, each with a different cwd + locale + time zone
            lines, ok = det.summarise(runs)
            self.assertEqual([r["exit"] for r in runs], [0] * len(runs))
            self.assertGreaterEqual(len(runs), 9)
            self.assertEqual(len({r["cwd"] for r in runs}), 3)
            self.assertEqual(len({r["locale"] for r in runs}), 3)
            self.assertTrue(ok, "\n".join(lines))
            for f in det.FILES:
                self.assertEqual(len({r["hashes"][f] for r in runs}), 1, f)
                self.assertNotEqual(runs[0]["hashes"][f], "(missing)", f)


class TheCheckerDetectsInstability(unittest.TestCase):
    """A fake program that leaks each environment factor into its output must be flagged for each of them."""

    def fake_root(self, root: Path, body: str) -> None:
        (root / "code").mkdir(parents=True)
        (root / "code" / "main.py").write_text(
            "import argparse, os, locale, sys, time\n"
            "ap = argparse.ArgumentParser(); ap.add_argument('--dataset'); ap.add_argument('--out'); ap.add_argument('--images')\n"
            "a = ap.parse_args()\n" + body, encoding="utf-8")

    def check(self, body: str):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d) / "repo"
            self.fake_root(root, body)
            runs = det.run_matrix(root, Path(d), Path(d) / "work", full=False)
            return det.summarise(runs)

    def test_hash_order_leak(self):
        lines, ok = self.check("names = {f'name_{i}' for i in range(40)}\n"
                               "open(a.out, 'w').write(','.join(names))\n"
                               "open(a.out.replace('.csv', '.notes.md'), 'w').write('x')\n")
        self.assertFalse(ok)
        self.assertTrue(any("output.csv differs" in l for l in lines))

    def test_cwd_leak(self):
        lines, ok = self.check("open(a.out, 'w').write(os.getcwd())\n")
        self.assertFalse(ok)

    def test_locale_encoding_and_timezone_leaks(self):
        _, ok = self.check("open(a.out, 'w').write(os.environ.get('LC_ALL', ''))\n")
        self.assertFalse(ok)
        _, ok = self.check("open(a.out, 'w').write(os.environ.get('TZ', ''))\n")
        self.assertFalse(ok)

    def test_a_stable_program_passes_and_a_crash_is_reported(self):
        _, ok = self.check("open(a.out, 'w').write('same')\nopen(a.out.replace('.csv', '.notes.md'), 'w').write('same')\n")
        self.assertTrue(ok)
        _, ok = self.check("sys.exit(3)\n")
        self.assertFalse(ok)


if __name__ == "__main__":
    unittest.main()
