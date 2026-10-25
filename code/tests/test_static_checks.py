"""The code stays clean under the linters configured in code/pyproject.toml (skipped when they are not installed: pip install -r code/requirements-dev.txt)."""
import shutil
import subprocess
import unittest

from tests._common import ROOT


def run(*cmd):
    return subprocess.run(cmd, cwd=ROOT / "code", capture_output=True, text=True)


class StaticChecks(unittest.TestCase):
    @unittest.skipUnless(shutil.which("ruff"), "ruff is not installed")
    def test_ruff_finds_nothing(self):
        r = run("ruff", "check", ".", "--no-cache")
        self.assertEqual(r.returncode, 0, r.stdout[-3000:])

    @unittest.skipUnless(shutil.which("mypy"), "mypy is not installed")
    def test_mypy_finds_nothing(self):
        r = run("mypy", "--no-incremental")
        self.assertEqual(r.returncode, 0, r.stdout[-3000:])

    def test_the_tooling_configuration_ships(self):
        text = (ROOT / "code" / "pyproject.toml").read_text(encoding="utf-8")
        for section in ("[tool.ruff.lint]", "[tool.mypy]"):
            self.assertIn(section, text)
        self.assertTrue((ROOT / "code" / "requirements-dev.txt").exists())


if __name__ == "__main__":
    unittest.main()
