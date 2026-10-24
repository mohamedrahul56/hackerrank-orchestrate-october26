"""Version stamps, the LLM cache key, and the settings file with its overrides."""
import io
import json
import re
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from unittest import mock

from tests._common import *  # noqa: F401,F403
from tests.test_llm_images import GOOD, FakeClient, reply
from auditor import settings as S
from auditor import versions
from auditor.audit import Config
from auditor.evidence import imageobs
from auditor.llm import LLM


class CacheInvalidation(unittest.TestCase):
    """Every version that goes into the cache key must make a stored reading unusable when it changes."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.dir = Path(self.tmp.name)
        self.img = self.dir / "shot.png"
        self.img.write_bytes(b"\x89PNG fake bytes")
        self.fake = FakeClient([reply(GOOD) for _ in range(12)])

    def tearDown(self):
        self.tmp.cleanup()

    def llm(self, **kw):
        return LLM(cache_dir=self.dir / "cache", usage_path=self.dir / "u.jsonl", client=self.fake, sleep=lambda s: None, **kw)

    def read(self, llm, system=imageobs.SYSTEM, instruction=imageobs.INSTRUCTION, schema=imageobs.SCHEMA, version=imageobs.PROMPT_VERSION):
        return llm.read_image_json("image", self.img, system, instruction, schema, version)

    def calls_after(self, llm, **kw):
        before = len(self.fake.calls)
        self.read(llm, **kw)
        return len(self.fake.calls) - before

    def test_same_versions_hit_the_cache(self):
        stamp = {"pipeline": "p1", "rules": "r1"}
        self.assertEqual(self.calls_after(self.llm(stamp=stamp)), 1)
        self.assertEqual(self.calls_after(self.llm(stamp=stamp)), 0, "a second LLM object with the same versions must reuse the stored reading")

    def test_a_pipeline_version_change_invalidates(self):
        self.assertEqual(self.calls_after(self.llm(stamp={"pipeline": "p1", "rules": "r1"})), 1)
        self.assertEqual(self.calls_after(self.llm(stamp={"pipeline": "p2", "rules": "r1"})), 1)
        self.assertEqual(self.calls_after(self.llm(stamp={"pipeline": "p1", "rules": "r1"})), 0, "the old entry is still valid for the old version")

    def test_a_rules_file_change_invalidates(self):
        self.assertEqual(self.calls_after(self.llm(stamp={"pipeline": "p1", "rules": "r1"})), 1)
        self.assertEqual(self.calls_after(self.llm(stamp={"pipeline": "p1", "rules": "r2"})), 1)

    def test_a_prompt_label_change_invalidates(self):
        llm = self.llm(stamp={"pipeline": "p1", "rules": "r1"})
        self.assertEqual(self.calls_after(llm), 1)
        self.assertEqual(self.calls_after(llm, version="img-v2"), 1)

    def test_a_prompt_text_change_invalidates_even_when_the_label_is_not_bumped(self):
        llm = self.llm(stamp={"pipeline": "p1", "rules": "r1"})
        self.assertEqual(self.calls_after(llm), 1)
        self.assertEqual(self.calls_after(llm, instruction=imageobs.INSTRUCTION + " Also list tooltips."), 1)
        self.assertEqual(self.calls_after(llm, system=imageobs.SYSTEM + " Be brief."), 1)

    def test_a_schema_or_model_change_invalidates(self):
        llm = self.llm(stamp={"pipeline": "p1", "rules": "r1"})
        self.assertEqual(self.calls_after(llm), 1)
        self.assertEqual(self.calls_after(llm, schema={**imageobs.SCHEMA, "description": "changed"}), 1)
        self.assertEqual(self.calls_after(self.llm(stamp={"pipeline": "p1", "rules": "r1"}, model="other-model")), 1)

    def test_the_default_stamp_follows_the_module_pipeline_version(self):
        self.assertEqual(self.calls_after(self.llm()), 1)
        self.assertEqual(self.calls_after(self.llm()), 0)
        with mock.patch.object(versions, "PIPELINE_VERSION", "9999.0.0"):
            self.assertEqual(self.calls_after(self.llm()), 1, "bumping PIPELINE_VERSION must invalidate")

    def test_the_stamp_is_not_the_code_digest(self):
        # editing code that cannot change what a model says about an image must not throw readings away
        st = versions.stamp(DATASET if DATASET.exists() else self.dir, "fp")
        self.assertEqual(set(versions.cache_stamp(st)), {"pipeline", "rules"})


class Digests(unittest.TestCase):
    def test_rules_digest_follows_the_file_bytes(self):
        with tempfile.TemporaryDirectory() as d:
            p = Path(d) / "deletion_rules.csv"
            self.assertEqual(versions.rules_digest(Path(d)), "missing")
            p.write_text("a,b\n1,2\n", encoding="utf-8")
            first = versions.rules_digest(Path(d))
            p.write_text("a,b\n1,3\n", encoding="utf-8")
            self.assertNotEqual(first, versions.rules_digest(Path(d)))
            self.assertRegex(first, r"^[0-9a-f]{64}$")

    def test_prompt_digest_covers_text_and_schema(self):
        base = versions.prompts()["image_reading"]
        with mock.patch.object(imageobs, "INSTRUCTION", imageobs.INSTRUCTION + "!"):
            self.assertNotEqual(base["digest"], versions.prompts()["image_reading"]["digest"])
            self.assertEqual(base["version"], versions.prompts()["image_reading"]["version"])

    def test_code_digest_is_stable_between_calls(self):
        self.assertEqual(versions.code_digest(), versions.code_digest())

    def test_stamp_lines_name_every_version(self):
        text = "\n".join(versions.stamp_lines(versions.stamp(Path("/nonexistent"), "fp123")))
        for needle in (versions.PIPELINE_VERSION, "image_reading", imageobs.PROMPT_VERSION, "rules file", "code sha256", "fp123"):
            self.assertIn(needle, text)


class SettingsLayers(unittest.TestCase):
    def write(self, text):
        d = tempfile.TemporaryDirectory()
        self.addCleanup(d.cleanup)
        p = Path(d.name) / "s.toml"
        p.write_text(text, encoding="utf-8")
        return p

    def test_shipped_file_equals_the_built_in_defaults(self):
        self.assertEqual(S.from_file(S.DEFAULT_FILE), S.Settings())
        self.assertEqual(S.load(env={}), S.Settings())

    def test_every_setting_is_documented_in_the_shipped_file(self):
        lines = S.DEFAULT_FILE.read_text(encoding="utf-8").splitlines()
        for section, keys in S._SECTIONS.items():
            start = lines.index(f"[{section}]")
            for key in keys:
                idx = next(i for i in range(start + 1, len(lines)) if re.match(rf"{key}\s*=", lines[i]))
                self.assertTrue(lines[idx - 1].lstrip().startswith("#"), f"{section}.{key} has no comment line above it")

    def test_precedence_file_then_env_then_cli(self):
        f = self.write('[llm]\nmodel = "from-file"\n[discovery]\nphase_margin = 3.5\n')
        self.assertEqual(S.load(f, env={}).model, "from-file")
        self.assertEqual(S.load(f, env={"AUDITOR_VLM_MODEL": "from-env"}).model, "from-env")
        s = S.load(f, ["llm.model=from-cli"], env={"AUDITOR_VLM_MODEL": "from-env"})
        self.assertEqual((s.model, s.tune.phase_margin), ("from-cli", 3.5))

    def test_bad_input_is_an_error_not_a_default(self):
        for text in ("[nope]\na = 1\n", "[pipeline]\ntypo = true\n", '[pipeline]\ndiscovery = "yes"\n', "[discovery]\nchain_gaps_seconds = [1]\n",
                     "[discovery]\naccount_window_minutes = [10, -10]\n", '[llm]\nimages = "maybe"\n', "[pipeline\n"):
            with self.assertRaises(S.SettingsError, msg=text):
                S.from_file(self.write(text))
        with self.assertRaises(S.SettingsError):
            S.from_file(Path("/nonexistent/settings.toml"))
        for item in ("pipeline.discovery", "pipeline.discovery=2", "nodot=1", "audit.later_link_max_days=abc"):
            with self.assertRaises(S.SettingsError, msg=item):
                S.load(overrides=[item], env={})

    def test_fingerprint_changes_with_answer_relevant_settings_only(self):
        base = S.Settings()
        for dotted, value in (("pipeline.discovery", "false"), ("discovery.phase_margin", "2.5"), ("discovery.chain_gaps_seconds", "[390, 450]"),
                              ("audit.later_link_max_days", "3"), ("pipeline.provisional_rows", "false")):
            self.assertNotEqual(base.fingerprint(), S.apply(base, dotted, value).fingerprint(), dotted)
        self.assertEqual(base.fingerprint(), S.apply(base, "llm.cache_dir", "/elsewhere").fingerprint())
        off = S.apply(base, "llm.images", "off")
        self.assertEqual(off.fingerprint(), S.apply(off, "llm.model", "x").fingerprint(), "with images off the model names cannot matter")
        self.assertNotEqual(base.fingerprint(), S.apply(base, "llm.model", "x").fingerprint())

    def test_config_is_built_from_settings(self):
        s = S.load(overrides=["pipeline.discovery=false", "discovery.phase_margin=3", "pipeline.timing_heuristics=false"], env={})
        cfg = Config.from_settings(s)
        self.assertFalse(cfg.use_discovery)
        self.assertFalse(cfg.use_timing_heuristics)
        self.assertEqual(cfg.tune.phase_margin, 3.0)
        self.assertTrue(cfg.allow_later_linking)


@unittest.skipUnless(DATASET.exists(), "dataset/ not present")
class CliStampsAndOverrides(unittest.TestCase):
    def run_cli(self, *args):
        import importlib.util
        spec = importlib.util.spec_from_file_location("auditor_cli_vs", ROOT / "code" / "main.py")
        cli = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cli)
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            rc = cli.main(list(args))
        return rc, out.getvalue(), err.getvalue()

    def test_notes_and_usage_log_carry_the_version_stamp(self):
        with tempfile.TemporaryDirectory() as d:
            out = Path(d) / "o.csv"
            rc, _, err = self.run_cli("--requests", "sample_deletion_requests.csv", "--out", str(out), "--images", "off")
            self.assertEqual(rc, 0, err)
            notes = out.with_suffix(".notes.md").read_text(encoding="utf-8")
            self.assertIn("## Version stamp", notes)
            self.assertIn(versions.PIPELINE_VERSION, notes)
            self.assertIn(versions.rules_digest(DATASET), notes)
            self.assertIn(imageobs.PROMPT_VERSION, notes)
            self.assertIn(S.apply(S.Settings(), "llm.images", "off").fingerprint(), notes)
            self.assertIn("`pipeline.discovery=true`", notes)
            run_info = [json.loads(l) for l in out.with_suffix(".usage.jsonl").read_text().splitlines() if '"run_info"' in l][0]
            self.assertEqual(run_info["stamp"]["rules"], versions.rules_digest(DATASET))

    def test_set_flag_changes_the_stamp_and_a_dedicated_flag_wins_over_set(self):
        with tempfile.TemporaryDirectory() as d:
            a, b = Path(d) / "a.csv", Path(d) / "b.csv"
            self.assertEqual(self.run_cli("--requests", "sample_deletion_requests.csv", "--out", str(a), "--images", "off")[0], 0)
            self.assertEqual(self.run_cli("--requests", "sample_deletion_requests.csv", "--out", str(b), "--images", "off",
                                          "--set", "discovery.phase_margin=9", "--set", "pipeline.discovery=true", "--no-discovery")[0], 0)
            na, nb = (x.with_suffix(".notes.md").read_text(encoding="utf-8") for x in (a, b))
            fp = lambda t: re.search(r"settings fingerprint: `(\w+)`", t).group(1)  # noqa: E731
            self.assertNotEqual(fp(na), fp(nb))
            self.assertIn("`pipeline.discovery=false`", nb, "--no-discovery wins over --set pipeline.discovery=true")
            self.assertIn("`discovery.phase_margin=9.0`", nb)

    def test_config_file_and_show_settings(self):
        with tempfile.TemporaryDirectory() as d:
            f = Path(d) / "s.toml"
            f.write_text("[discovery]\nphase_margin = 4.0\n", encoding="utf-8")
            rc, out, _ = self.run_cli("--config", str(f), "--set", "audit.later_link_max_days=2", "--show-settings")
            self.assertEqual(rc, 0)
            self.assertIn("discovery.phase_margin = 4.0", out)
            self.assertIn("audit.later_link_max_days = 2", out)
            self.assertIn("settings fingerprint", out)

    def test_bad_settings_stop_the_run_with_exit_2_and_write_nothing(self):
        with tempfile.TemporaryDirectory() as d:
            out = Path(d) / "o.csv"
            rc, _, err = self.run_cli("--out", str(out), "--set", "pipeline.typo=1")
            self.assertEqual(rc, 2)
            self.assertIn("unknown setting", err)
            self.assertFalse(out.exists())


if __name__ == "__main__":
    unittest.main()
