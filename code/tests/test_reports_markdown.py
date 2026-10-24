"""The submission's reports must render fully as Markdown (the code judge reads .md only): balanced fences, well-formed tables, no raw Python reprs."""
import re
import unittest
from pathlib import Path

from tests._common import *  # noqa: F401,F403

REPORTS = sorted((ROOT / "code" / "evaluation").glob("*.md")) + [ROOT / "README.md", ROOT / "ARCHITECTURE.md", ROOT / "code" / "NOTES.md", ROOT / "code" / "SPEC.md", ROOT / "code" / "INTERVIEW_PREP.md"]
REQUIRED = [ROOT / "code" / "evaluation" / "evaluation_report.md", ROOT / "code" / "evaluation" / "usage_report.md", ROOT / "README.md", ROOT / "code" / "INTERVIEW_PREP.md",
            ROOT / "code" / "evaluation" / "extra_cases.md"]
BAD_TOKENS = ("{{", "Counter(", "defaultdict(", "<object at", "Traceback (most recent", "nan%", "None%", "{'", "datetime.datetime(", "frozenset(")


def cells(line: str) -> int:
    s = line.strip()
    s = s[1:] if s.startswith("|") else s
    s = s[:-1] if s.endswith("|") and not s.endswith("\\|") else s
    return len(re.split(r"(?<!\\)\|", re.sub(r"`[^`]*`", lambda m: m.group(0).replace("|", "\\|"), s)))


def problems(path: Path) -> list[str]:
    text = path.read_text(encoding="utf-8")
    out = []
    fences = [l for l in text.splitlines() if l.lstrip().startswith("```")]
    if len(fences) % 2:
        out.append("unbalanced code fences")
    in_fence = False
    block: list[str] = []
    lines = text.splitlines()
    for i, l in enumerate(lines + [""]):
        if l.lstrip().startswith("```"):
            in_fence = not in_fence
            continue
        if in_fence:
            continue
        if l.startswith("|"):
            block.append(l)
            continue
        if block:
            if len(block) < 2 or not re.fullmatch(r"\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)*\|?", block[1].strip()):
                out.append(f"table before line {i + 1} has no separator row")
            else:
                n = cells(block[0])
                for r in block[2:]:
                    if cells(r) != n:
                        out.append(f"table before line {i + 1}: row has {cells(r)} cells, header {n}: {r[:80]}")
                        break
            block = []
    visible = re.sub(r"```.*?```", "", text, flags=re.S)
    for t in BAD_TOKENS:
        if t in re.sub(r"`[^`\n]*`", "", visible):
            out.append(f"raw Python/debug text {t!r} in the rendered Markdown")
    return out


class ReportsRenderAsMarkdown(unittest.TestCase):
    def test_required_documents_exist(self):
        missing = [str(p.relative_to(ROOT)) for p in REQUIRED if not p.exists()]
        self.assertEqual(missing, [])

    def test_every_markdown_report_is_well_formed(self):
        bad = {str(p.relative_to(ROOT)): problems(p) for p in REPORTS if p.exists() and problems(p)}
        self.assertEqual(bad, {})

    def test_the_checker_catches_what_it_should(self):
        import tempfile
        with tempfile.TemporaryDirectory() as d:
            p = Path(d) / "x.md"
            p.write_text("| a | b |\n|---|---|\n| 1 |\n\n```\nopen\n", encoding="utf-8")
            found = " ".join(problems(p))
            self.assertIn("unbalanced", found)
            self.assertIn("row has 1 cells", found)
            p.write_text("value Counter({'a': 1})\n", encoding="utf-8")
            self.assertIn("raw Python", " ".join(problems(p)))


if __name__ == "__main__":
    unittest.main()
