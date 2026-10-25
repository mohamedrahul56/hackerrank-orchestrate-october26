"""Proof that the per-file router changed no observation: the old suffix dispatch is kept here as a frozen reference and compared with the router-based `load_evidence`.

`compare(root, extractor)` loads the whole evidence set both ways and returns what differs (observations, coverage, warnings). `tests/test_router.py` asserts it is empty for all
94 files of the dataset (and for synthetic folders); `evaluation/agent_proofs.py` prints the same comparison in the agent-layer report.
"""
from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from auditor.dataset import DatasetError, _rows, parse_ts, try_ts  # noqa: E402,F401
from auditor.evidence import imagejobs, imageobs, loaders, logobs  # noqa: E402
from auditor.evidence.load import catalog_rows, inside_root, load_evidence, manifest_rows  # noqa: E402
from auditor.evidence.records import EvidenceBase  # noqa: E402
from auditor.schema import valid_resource_id  # noqa: E402


def legacy_load_evidence(root: Path, include_images: bool = True, image_extractor=None, image_workers: int = 1) -> EvidenceBase:
    """The suffix dispatch as it was before the router existed (frozen copy; the router must reproduce it exactly)."""
    base = EvidenceBase()
    manifest = manifest_rows(root)
    if not manifest:
        base.warnings.append("evidence manifest missing, empty or unreadable: no evidence files were used")
    safe = []
    for r in manifest:
        if inside_root(root, r["file_path"]):
            safe.append(r)
        else:
            base.warnings.append(f"{r['file_id']}: manifest path {r['file_path'][:60]!r} points outside the dataset folder; not read")
    manifest = safe
    if include_images and image_extractor is not None and image_workers > 1:
        pngs = [root / r["file_path"] for r in manifest if (root / r["file_path"]).suffix.lower() == ".png"]
        image_extractor = imagejobs.prefetch(image_extractor, pngs, imageobs.SYSTEM, imageobs.INSTRUCTION, imageobs.SCHEMA, imageobs.PROMPT_VERSION, image_workers)
    for r in manifest:
        path, fid, t = root / r["file_path"], r["file_id"], r["t"]
        try:
            suffix = path.suffix.lower()
            if suffix == ".sql":
                loaders.load_sql(base, fid, path, t)
            elif suffix == ".json":
                loaders.load_json(base, fid, path, t)
            elif suffix == ".txt":
                loaders.load_text(base, fid, path, t)
            elif suffix == ".log":
                logobs.load_log(base, fid, path, t)
            elif suffix == ".pdf":
                from auditor.evidence import pdfobs
                pdfobs.load_pdf(base, fid, path, t, catalog=catalog_rows(root))
            elif suffix == ".png" and include_images and image_extractor is not None:
                imageobs.load_image(base, fid, path, t, image_extractor, catalog=catalog_rows(root))
            elif suffix == ".png":
                base.warnings.append(f"{fid}: image not read (no image extractor configured)")
            else:
                base.warnings.append(f"{fid}: unsupported file type {suffix}")
        except Exception as e:  # noqa: BLE001 - a corrupt file must never stop the run
            base.warnings.append(f"{fid}: could not read {path.name}: {type(e).__name__}: {e}")
    odd = [o for o in base.obs if not valid_resource_id(o.resource)]
    if odd:                                                       # text that is not a resource id (a path, a sentence, SQL) is not a resource: drop it, say so
        base.obs = [o for o in base.obs if valid_resource_id(o.resource)]
        base.warnings.append(f"{len(odd)} observation(s) dropped: resource id is not of the form <name>_<number> (first: {odd[0].file_id} {odd[0].resource[:40]!r})")
    return base


def compare(root: Path, include_images: bool = False, image_extractor=None, image_workers: int = 1) -> dict:
    """Load `root` with the legacy dispatch and with the router; return counts and the list of differences (empty = identical)."""
    old = legacy_load_evidence(root, include_images, image_extractor, image_workers)
    new = load_evidence(root, include_images, image_extractor, image_workers)
    diffs = []
    for what in ("obs", "coverage", "warnings"):
        a, b = getattr(old, what), getattr(new, what)
        if a != b:
            diffs.append(f"{what}: {len(a)} legacy vs {len(b)} routed" + ("" if len(a) != len(b) else " (same count, different content)"))
    return {"files": len(manifest_rows(root)), "observations": len(new.obs), "warnings": len(new.warnings), "routes": len(new.routes), "differences": diffs}
