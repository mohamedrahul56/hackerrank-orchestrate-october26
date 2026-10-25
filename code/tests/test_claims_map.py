"""The claim-to-code map in README.md cannot rot: every file, symbol and test it names must exist."""
import re
import unittest

from tests._common import ROOT


def rows():
    text = (ROOT / "README.md").read_text(encoding="utf-8")
    body = text.split("<!-- claims:start -->")[1].split("<!-- claims:end -->")[0]
    out = []
    for line in body.splitlines():
        cells = [c.strip() for c in line.strip().strip("|").split("|")]
        if len(cells) == 3 and cells[1].startswith("`") and "::" in cells[1]:
            out.append((cells[0], cells[1].strip("`"), cells[2].strip("`")))
    return out


class ClaimsMap(unittest.TestCase):
    def test_the_map_has_rows(self):
        self.assertGreaterEqual(len(rows()), 15)

    def test_every_file_symbol_and_test_exists(self):
        for claim, ref, test in rows():
            with self.subTest(claim=claim[:60]):
                path, symbol = ref.split("::")
                f = ROOT / path
                self.assertTrue(f.is_file(), f"{path} does not exist")
                defined = re.search(rf"^\s*(?:def|class)\s+{re.escape(symbol)}\b|^{re.escape(symbol)}\s*[:=]", f.read_text(encoding="utf-8"), re.M)
                self.assertTrue(defined, f"{symbol} is not defined in {path}")
                self.assertTrue((ROOT / "code" / "tests" / f"{test}.py").is_file(), f"code/tests/{test}.py does not exist")


if __name__ == "__main__":
    unittest.main()
