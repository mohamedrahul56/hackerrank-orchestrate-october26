"""The packaging tool: what goes into code.zip, what must stay out, and the secrets / dataset-leak scans."""
import importlib.util
import tempfile
import unittest
import zipfile
from pathlib import Path

from tests._common import ROOT

spec = importlib.util.spec_from_file_location("package_tool", ROOT / "code" / "tools" / "package.py")
pkg = importlib.util.module_from_spec(spec)
spec.loader.exec_module(pkg)

FAKE_KEY = "sk-" + "ant-" + "api03-" + "A1b2C3d4E5f6G7h8I9j0"          # assembled at run time so this file itself holds no key-shaped literal


def tree(root: Path, files: dict[str, str | bytes]) -> None:
    for name, content in files.items():
        p = root / name
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_bytes(content if isinstance(content, bytes) else content.encode())


BASE = {"README.md": "# run it", "ARCHITECTURE.md": "# design", "Makefile": "test:\n\ttrue\n", "code/main.py": "print(1)", "code/requirements.txt": "x==1", "code/NOTES.md": "notes",
        "code/evaluation/extra_cases.md": "cases", "code/config/prices.example.json": "{}"}


class PackagingRules(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)

    def tearDown(self):
        self.tmp.cleanup()

    def names(self, files):
        tree(self.root, {**BASE, **files})
        return [pkg.rel(p, self.root) for p in pkg.collect(self.root)]

    def test_excluded_paths_never_enter_the_zip(self):
        names = self.names({"dataset/deletion_requests.csv": "x", ".env": "K=1", "code/.env.local": "K=1", "code/out/pred.csv": "x", "code/.cache/llm/a.json": "{}",
                            "code/config/prices.json": "{}", "code/__pycache__/m.pyc": b"\0", "code/evaluation/__pycache__/x.cpython-313.pyc": b"\0",
                            "agent-trace/claude-code/t.jsonl": "{}", "log.txt": "log", "output.csv": "a", "code/.venv/lib/x.py": "x", "AGENTS.md": "a"})
        self.assertEqual(sorted(names), sorted(BASE))

    def test_checks_flag_archives_judge_ignored_files_and_dataset_content(self):
        tree(self.root, {**BASE, "code/data.csv": "a,b", "code/notes.sql": "select 1", "code/old.zip": b"PK", "code/dataset/x.md": "x"})
        bad, _ = pkg.check(pkg.collect(self.root), self.root)
        text = "\n".join(bad)
        self.assertIn("code/data.csv: file type the code judge ignores", text)
        self.assertIn("code/notes.sql", text)
        self.assertIn("code/old.zip: nested archive", text)
        self.assertIn("dataset content in the zip", text)

    def test_the_price_template_is_the_only_judge_ignored_file_allowed(self):
        tree(self.root, BASE)
        self.assertEqual(pkg.check(pkg.collect(self.root), self.root)[0], [])

    def test_secret_shapes_are_detected_everywhere_including_markdown_and_the_log(self):
        tree(self.root, {**BASE, "code/NOTES.md": f"key: {FAKE_KEY}", "log.txt": "token ghp_" + "a" * 36,
                         "code/config/prices.json": '{"api_key": "' + "x" * 24 + '"}'})
        bad, _ = pkg.check(pkg.collect(self.root), self.root)
        text = "\n".join(bad)
        self.assertIn("code/NOTES.md: looks like a Anthropic key", text)
        self.assertIn("log.txt (not zipped, scanned anyway): looks like a GitHub token", text)
        self.assertIn("prices.json (not zipped, scanned anyway): looks like a key-like assignment", text)

    def test_ordinary_text_is_not_flagged(self):
        tree(self.root, {**BASE, "code/NOTES.md": "Set ANTHROPIC_API_KEY in your shell. Use sk-... never. password = prompt for it. token_count=5"})
        self.assertEqual(pkg.check(pkg.collect(self.root), self.root)[0], [])

    def test_dataset_people_must_not_leak_into_the_zip(self):
        tree(self.root, {**BASE, "dataset/user_identities.csv": "user_id,name,email,phone,email_hash,updated_at\nu1,A,real.person@corp.test,1,h,t\n",
                         "code/NOTES.md": "mentions Real.Person@corp.test once"})
        bad, _ = pkg.check(pkg.collect(self.root), self.root)
        self.assertTrue(any("e-mail address(es) of dataset people" in b for b in bad))

    def test_missing_required_files_fail_and_final_files_are_required_only_on_request(self):
        tree(self.root, {k: v for k, v in BASE.items() if k != "README.md"})
        self.assertTrue(any("missing required file README.md" in b for b in pkg.check(pkg.collect(self.root), self.root)[0]))
        tree(self.root, BASE)
        self.assertEqual(pkg.check(pkg.collect(self.root), self.root)[0], [])
        bad, _ = pkg.check(pkg.collect(self.root), self.root, require_final=True)
        self.assertEqual(len(bad), len(pkg.FINAL_REQUIRED))      # evaluation, usage and performance reports

    def test_two_builds_are_byte_identical_and_hold_exactly_the_collected_files(self):
        tree(self.root, BASE)
        files = pkg.collect(self.root)
        a, b = self.root / "a.zip", self.root / "b.zip"
        pkg.build(files, a, self.root)
        pkg.build(files, b, self.root)
        self.assertEqual(a.read_bytes(), b.read_bytes())
        with zipfile.ZipFile(a) as z:
            self.assertEqual(sorted(z.namelist()), sorted(BASE))
            self.assertTrue(all(i.date_time == (1980, 1, 1, 0, 0, 0) for i in z.infolist()))


class TheRealRepository(unittest.TestCase):
    def test_dry_run_passes_and_the_zip_is_clean(self):
        files = pkg.collect(ROOT)
        bad, _ = pkg.check(files, ROOT)
        self.assertEqual(bad, [])
        names = [pkg.rel(p, ROOT) for p in files]
        self.assertFalse([n for n in names if n.startswith("dataset/") or "/dataset/" in n])
        self.assertFalse([n for n in names if n.split("/")[-1].startswith(".env") or n.endswith("prices.json") or n.startswith("code/out/")])
        self.assertFalse([n for n in names if any(n.endswith(e) for e in pkg.ARCHIVE_EXT)])
        self.assertIn("code/evaluation/extra_cases.md", names)
        self.assertEqual([n for n in names if any(n.endswith(e) for e in pkg.JUDGE_IGNORED_EXT)], ["code/config/prices.example.json"])

    def test_real_build_is_deterministic(self):
        with tempfile.TemporaryDirectory() as d:
            a, b = Path(d) / "a.zip", Path(d) / "b.zip"
            pkg.build(pkg.collect(ROOT), a, ROOT)
            pkg.build(pkg.collect(ROOT), b, ROOT)
            self.assertEqual(a.read_bytes(), b.read_bytes())
            with zipfile.ZipFile(a) as z:
                self.assertIsNone(z.testzip())

    def test_the_repository_log_and_price_file_hold_no_secret(self):
        for f in (ROOT / "log.txt", ROOT / "code" / "config" / "prices.json"):
            if f.exists():
                self.assertEqual(pkg.scan_text(f.name, f.read_text(encoding="utf-8")), [])

    def test_every_tracked_text_file_is_free_of_secret_shapes(self):
        import subprocess
        try:
            tracked = subprocess.run(["git", "ls-files"], cwd=ROOT, capture_output=True, text=True, check=True).stdout.split("\n")
        except (OSError, subprocess.CalledProcessError):
            self.skipTest("git not available")
        hits = []
        for n in tracked:
            if not n or n.startswith("dataset/"):
                continue
            t = pkg.read_text(ROOT / n) if (ROOT / n).is_file() else None
            if t:
                hits += pkg.scan_text(n, t)
        self.assertEqual(hits, [])


if __name__ == "__main__":
    unittest.main()
