"""Shared test helpers. Tests that need the real dataset skip themselves when dataset/ is absent
(it is deliberately not shipped in code.zip)."""
from __future__ import annotations

import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "code"))
sys.path.insert(0, str(ROOT / "code" / "evaluation"))

DATASET = ROOT / "dataset"
needs_dataset = unittest.skipUnless((DATASET / "sample_output.csv").exists(), "dataset/ not present")
