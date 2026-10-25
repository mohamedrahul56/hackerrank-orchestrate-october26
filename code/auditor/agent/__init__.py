"""A small, bounded, model-driven reader for the screenshots (opt-in: `--images agent`).

The model DESCRIBES a screenshot through two tools (`zoom_region`, `submit_reading`); everything it says passes a deterministic gate (`gate.py`) before it can become
an observation, and any failure falls back to "not read" (the deterministic result). See ARCHITECTURE.md and prompts/.
"""
