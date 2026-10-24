"""Assemble every Obs from every evidence file + the activity stream's catalog rows."""
from __future__ import annotations

import csv
import os
import re
from pathlib import Path

from ..dataset import DatasetError, _rows, parse_ts, try_ts
from ..schema import valid_resource_id
from . import loaders, logobs
from .records import RESOURCE_RE, EvidenceBase, Obs, flatten, idents_from


def manifest_rows(root: Path) -> list[dict]:
    """evidence_files.csv rows with parsed capture time (a malformed row is skipped, never fatal; a missing manifest means no evidence)."""
    try:
        rows = _rows(root / "evidence_files.csv", ("file_id", "file_path", "captured_at"))
    except DatasetError:
        return []
    return [{**r, "t": t} for r in rows if (t := try_ts(r["captured_at"])) is not None and r["file_id"].strip()]


def inside_root(root: Path, rel: str) -> bool:
    """True when `rel`, joined to `root` and normalised WITHOUT following symlinks, stays inside `root` (no '..' escape, no absolute path).
    Only dataset/ may be read."""
    base = os.path.normpath(os.path.abspath(root))
    target = os.path.normpath(os.path.join(base, rel))
    return target == base or target.startswith(base + os.sep)


def load_evidence(root: Path, include_images: bool = True, image_extractor=None, image_workers: int = 1) -> EvidenceBase:
    """Never raises: an unreadable file becomes a warning and the rest of the evidence is still used."""
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
        from . import imagejobs, imageobs
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
                from . import pdfobs
                pdfobs.load_pdf(base, fid, path, t, catalog=catalog_rows(root))
            elif suffix == ".png" and include_images and image_extractor is not None:
                from . import imageobs
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


_CATALOG_CACHE: dict[tuple, list[dict]] = {}


def catalog_rows(root: Path) -> list[dict]:
    """LINK_RESOURCE_IDENTITY activity rows: explicit (resource, alias, email, user_id) links published by the data catalog."""
    f = root / "system_activity.csv"
    stamp = (root, f.stat().st_mtime_ns, f.stat().st_size) if f.exists() else (root, 0, 0)
    if stamp in _CATALOG_CACHE:
        return _CATALOG_CACHE[stamp]
    rows = []
    try:
        activity = _rows(root / "system_activity.csv", ("activity_id", "event_time", "event_type"))
    except DatasetError:
        activity = []
    for r in activity:
        if r["event_type"] != "LINK_RESOURCE_IDENTITY" or try_ts(r["event_time"]) is None:
            continue
        d = r.get("details", "")
        m = re.search(r"(?:linked|confirmed)\s+(\S+)\s+\(([^)]*)\)", d)
        alias = m.group(2) if m else ""
        rows.append({"activity_id": r["activity_id"], "t": parse_ts(r["event_time"]), "resource_id": r.get("resource_id", ""),
                     "system": r.get("target_system", ""), "alias": alias, "idents": idents_from([("d", d)]),
                     "only_subject": "belongs only to" in d})
    _CATALOG_CACHE[stamp] = rows
    return rows
