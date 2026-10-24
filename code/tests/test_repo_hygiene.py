"""Guards for the submission rules: no secrets, no dataset in the zip, no tracked .env."""
import subprocess
import unittest

from tests._common import *  # noqa: F401,F403


class HygieneTests(unittest.TestCase):
    @unittest.skipUnless((ROOT / ".gitignore").exists(), ".gitignore is not part of code.zip")
    def test_gitignore_blocks_env_and_zip(self):
        lines = (ROOT / ".gitignore").read_text().split()
        for needed in (".env", "code.zip", "__pycache__/", "code/out/", "code/config/prices.json", "agent-trace/"):
            self.assertIn(needed, lines)

    def test_no_env_file_is_tracked(self):
        try:
            out = subprocess.run(["git", "ls-files"], cwd=ROOT, capture_output=True, text=True, check=True).stdout.split()
        except (OSError, subprocess.CalledProcessError):
            self.skipTest("git not available")
        self.assertEqual([f for f in out if f.split("/")[-1].startswith(".env")], [])

    def test_no_api_key_literals_in_code(self):
        for p in (ROOT / "code").rglob("*.py"):
            text = p.read_text(encoding="utf-8")
            self.assertNotRegex(text, r"sk-ant-[A-Za-z0-9_-]{10,}", str(p))


if __name__ == "__main__":
    unittest.main()
