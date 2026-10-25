"""Kill a run in the middle, resume it, and require the same output.csv (and notes) as a run that was never interrupted."""
import hashlib
import json
import os
import shutil
import signal
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

from tests._common import DATASET, ROOT
from auditor import checkpoint as ck
from auditor import settings as S
from auditor import versions
from auditor.audit import Auditor, CaseResult, Config

MAIN = ROOT / "code" / "main.py"


def sha(p: Path) -> str:
    return hashlib.sha256(p.read_bytes()).hexdigest()


def clean_env(**extra):
    env = {k: v for k, v in os.environ.items() if k not in ("ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "AUDITOR_TEST_PAUSE_AFTER")}
    env.update(extra)
    return env


def run_main(out: Path, *args, env=None):
    p = subprocess.run([sys.executable, str(MAIN), "--out", str(out), "--images", "off", *args], capture_output=True, text=True, env=env or clean_env())
    return p.returncode, p.stderr


def run_and_kill(out: Path, after: int, *args):
    """Start a real run that stops itself after `after` finished cases, and SIGKILL it there (no cleanup code gets to run)."""
    p = subprocess.Popen([sys.executable, str(MAIN), "--out", str(out), "--images", "off", *args], stderr=subprocess.PIPE, text=True,
                         env=clean_env(AUDITOR_TEST_PAUSE_AFTER=str(after)))
    try:
        for line in p.stderr:
            if line.startswith("CHECKPOINT-REACHED"):
                break
        else:
            raise AssertionError("the run ended before reaching the kill point")
        p.send_signal(signal.SIGKILL)
        p.wait(timeout=30)
    finally:
        if p.poll() is None:
            p.kill()
        p.stderr.close()
    assert p.returncode == -signal.SIGKILL


@unittest.skipUnless(DATASET.exists(), "dataset/ not present")
class KillAndResume(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.base = Path(cls.tmp.name)
        (cls.base / "clean").mkdir()
        cls.clean = cls.base / "clean" / "o.csv"
        rc, err = run_main(cls.clean)
        assert rc == 0, err
        cls.clean_sha, cls.clean_notes = sha(cls.clean), sha(cls.clean.with_suffix(".notes.md"))
        cls.total = sum(1 for _ in cls.clean.read_text().splitlines()) - 1

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def workdir(self, name):
        d = self.base / name
        shutil.rmtree(d, ignore_errors=True)
        d.mkdir()
        return d / "o.csv"

    def partial_lines(self, out):
        return out.with_suffix(".partial.jsonl").read_text(encoding="utf-8").splitlines()

    def test_a_clean_run_leaves_no_checkpoint(self):
        self.assertFalse(self.clean.with_suffix(".partial.jsonl").exists())
        self.assertEqual(self.total, 518)

    def test_sigkill_midway_then_resume_gives_the_identical_output(self):
        out = self.workdir("killed")
        run_and_kill(out, 200)
        self.assertFalse(out.exists(), "a killed run must not have written an output file")
        self.assertEqual(len(self.partial_lines(out)), 1 + 200, "a header and the 200 finished cases")
        rc, err = run_main(out)
        self.assertEqual(rc, 0, err)
        self.assertIn("resumed (200 cases kept)", err)
        self.assertIn(f"200 of {self.total} cases reused, {self.total - 200} audited", err)
        self.assertEqual(sha(out), self.clean_sha, "the resumed output.csv must equal the clean run's byte for byte")
        self.assertEqual(sha(out.with_suffix(".notes.md")), self.clean_notes)
        self.assertFalse(out.with_suffix(".partial.jsonl").exists(), "the checkpoint is removed after a successful run")

    def test_killed_twice_still_resumes(self):
        out = self.workdir("twice")
        run_and_kill(out, 50)
        run_and_kill(out, 300)                                     # the second run reuses 50, audits 250 more, and is killed
        self.assertEqual(len(self.partial_lines(out)), 1 + 300)
        rc, err = run_main(out)
        self.assertEqual(rc, 0, err)
        self.assertIn("300 of 518 cases reused", err)
        self.assertEqual(sha(out), self.clean_sha)

    def test_a_line_cut_short_by_the_kill_is_dropped_and_redone(self):
        out = self.workdir("torn")
        run_and_kill(out, 120)
        with out.with_suffix(".partial.jsonl").open("a", encoding="utf-8") as fh:
            fh.write('{"d":{"case_id":"case_999","kind":"case","status":"COMPL')          # a write that was cut off
        rc, err = run_main(out)
        self.assertEqual(rc, 0, err)
        self.assertIn("resumed (120 cases kept, damaged tail dropped)", err)
        self.assertEqual(sha(out), self.clean_sha)

    def test_a_damaged_line_in_the_middle_drops_it_and_everything_after(self):
        out = self.workdir("damaged")
        run_and_kill(out, 100)
        p = out.with_suffix(".partial.jsonl")
        lines = self.partial_lines(out)
        obj = json.loads(lines[41])
        obj["d"]["status"] = "COMPLETE" if obj["d"]["status"] != "COMPLETE" else "INCOMPLETE"       # content no longer matches its checksum
        lines[41] = json.dumps(obj, separators=(",", ":"))
        p.write_text("\n".join(lines) + "\n", encoding="utf-8")
        rc, err = run_main(out)
        self.assertEqual(rc, 0, err)
        self.assertIn("resumed (40 cases kept", err)                # header + 40 good lines before the damaged one
        self.assertEqual(sha(out), self.clean_sha, "the tampered result must not reach output.csv")

    def test_a_checkpoint_for_other_settings_is_discarded_not_mixed_in(self):
        out = self.workdir("settings")
        run_and_kill(out, 200)
        rc, err = run_main(out, "--set", "discovery.phase_margin=3.0")
        self.assertEqual(rc, 0, err)
        self.assertIn("discarded: the checkpoint was made for other inputs, versions or settings", err)
        self.assertIn(f"0 of {self.total} cases reused", err)
        ref = self.workdir("settings_ref")
        self.assertEqual(run_main(ref, "--set", "discovery.phase_margin=3.0")[0], 0)
        self.assertEqual(sha(out), sha(ref), "same as a clean run with those settings")

    def test_restart_ignores_the_checkpoint_and_keep_checkpoint_keeps_it(self):
        out = self.workdir("restart")
        run_and_kill(out, 150)
        rc, err = run_main(out, "--restart", "--keep-checkpoint")
        self.assertEqual(rc, 0, err)
        self.assertIn("restart requested", err)
        self.assertIn(f"0 of {self.total} cases reused", err)
        self.assertEqual(len(self.partial_lines(out)), 1 + self.total)
        rc, err = run_main(out)                                    # everything is in the kept checkpoint: nothing is audited
        self.assertEqual(rc, 0, err)
        self.assertIn(f"{self.total} of {self.total} cases reused, 0 audited", err)
        self.assertEqual(sha(out), self.clean_sha)


@unittest.skipUnless(DATASET.exists(), "dataset/ not present")
class ResumeSkipsFinishedCases(unittest.TestCase):
    def test_finished_cases_are_not_audited_again(self):
        a = Auditor(DATASET, Config())
        order = sorted(a.requests, key=lambda c: (len(c), c))
        with tempfile.TemporaryDirectory() as d:
            path = Path(d) / "p.jsonl"
            first = ck.Checkpoint(path, "key1")
            for cid in order[:100]:
                first.add(a.audit(cid))
            first.close()
            calls = []
            real = a.audit
            a.audit = lambda cid: (calls.append(cid), real(cid))[1]
            second = ck.Checkpoint(path, "key1")
            self.assertEqual(len(second.done), 100)
            results, reused = ck.audit_all(a, order, second)
            second.close()
            self.assertEqual((reused, calls), (100, order[100:]))
            self.assertEqual([r.case_id for r in results], order)
            a.audit = real
            self.assertEqual([(r.status, r.steps, r.notes) for r in results[:100]], [(x.status, x.steps, x.notes) for x in map(real, order[:100])],
                             "what was stored equals what a fresh audit gives")


class RunKey(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.ds = Path(self.tmp.name)
        (self.ds / "deletion_requests.csv").write_text("case_id\ncase_1\n", encoding="utf-8")
        (self.ds / "deletion_rules.csv").write_text("a,b\n1,2\n", encoding="utf-8")
        (self.ds / "evidence").mkdir()
        (self.ds / "evidence" / "e.txt").write_text("evidence", encoding="utf-8")
        self.stamp = versions.stamp(self.ds, S.Settings().fingerprint())

    def key(self, **kw):
        args = dict(requests_file="deletion_requests.csv", stamp=self.stamp, images_on=False, image_obs="")
        args.update(kw)
        return ck.run_key(self.ds, **args)

    def test_same_inputs_same_key(self):
        self.assertEqual(self.key(), self.key())

    def test_every_ingredient_changes_the_key(self):
        base = self.key()
        changed = {
            "requests file": self.key(requests_file="sample_deletion_requests.csv"),
            "pipeline version": self.key(stamp={**self.stamp, "pipeline": "other"}),
            "rules digest": self.key(stamp={**self.stamp, "rules": "other"}),
            "prompt digest": self.key(stamp={**self.stamp, "prompts": {"image_reading": {"version": "v2", "digest": "x"}}}),
            "code digest": self.key(stamp={**self.stamp, "code": "other"}),
            "settings": self.key(stamp={**self.stamp, "settings": S.apply(S.Settings(), "pipeline.discovery", "false").fingerprint()}),
            "images on": self.key(images_on=True),
            "image readings": self.key(images_on=True, image_obs="abc"),
        }
        for what, k in changed.items():
            self.assertNotEqual(k, base, what)
        self.assertEqual(len(set(changed.values())), len(changed), "no two different changes collide")

    def test_any_dataset_file_change_changes_the_key(self):
        base = self.key()
        (self.ds / "evidence" / "e.txt").write_text("evidence!", encoding="utf-8")
        edited = self.key()
        self.assertNotEqual(base, edited)
        (self.ds / "evidence" / "e.txt").write_text("evidence", encoding="utf-8")
        self.assertEqual(self.key(), base)
        (self.ds / "evidence" / "new.txt").write_text("x", encoding="utf-8")
        self.assertNotEqual(self.key(), base)

    def test_checkpoint_with_wrong_key_or_format_is_discarded(self):
        path = self.ds / "p.jsonl"
        c = ck.Checkpoint(path, "k1")
        c.add(CaseResult("case_1", "COMPLETE", [], [], [], False))
        c.close()
        again = ck.Checkpoint(path, "k1")
        self.assertEqual(again.how, "resumed (1 cases kept)")
        again.close()
        other = ck.Checkpoint(path, "k2")
        self.assertTrue(other.how.startswith("discarded"))
        self.assertEqual(other.done, {})
        other.close()
        self.assertEqual(len(path.read_text().splitlines()), 1, "the old cases are gone, only the new header remains")
        path.write_text("not a checkpoint\n", encoding="utf-8")
        bad = ck.Checkpoint(path, "k1")
        self.assertEqual(bad.how, "discarded: header unreadable")
        bad.close()

    def test_round_trip_keeps_every_field(self):
        r = CaseResult("case_7", "INCOMPLETE", [{"step_no": 1, "phase": "FINDING"}], ["a note"], [("account_1", "evidence_02", "2026-09-17T12:00:00Z")], True)
        self.assertEqual(ck.from_payload(json.loads(json.dumps(ck.to_payload(r)))), r)


if __name__ == "__main__":
    unittest.main()
