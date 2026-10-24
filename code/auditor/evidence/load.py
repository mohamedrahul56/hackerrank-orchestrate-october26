"""Assemble every Obs from every evidence file + the activity stream's catalog rows."""
from __future__ import annotations

import csv
import re
from pathlib import Path

from ..dataset import parse_ts
from . import loaders, logobs
from .records import RESOURCE_RE, EvidenceBase, Obs, flatten, idents_from


def load_evidence(root: Path, include_images: bool = True, image_extractor=None) -> EvidenceBase:
    """Never raises: an unreadable file becomes a warning and the rest of the evidence is still used."""
    base = EvidenceBase()
    with (root / "evidence_files.csv").open(newline="", encoding="utf-8") as fh:
        manifest = list(csv.DictReader(fh))
    for r in manifest:
        path, fid, t = root / r["file_path"], r["file_id"], parse_ts(r["captured_at"])
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
    return base


_CATALOG_CACHE: dict[Path, list[dict]] = {}


def catalog_rows(root: Path) -> list[dict]:
    """LINK_RESOURCE_IDENTITY activity rows: explicit (resource, alias, email, user_id) links published by the data catalog."""
    if root in _CATALOG_CACHE:
        return _CATALOG_CACHE[root]
    rows = []
    with (root / "system_activity.csv").open(newline="", encoding="utf-8") as fh:
        for r in csv.DictReader(fh):
            if r["event_type"] != "LINK_RESOURCE_IDENTITY":
                continue
            d = r["details"]
            m = re.search(r"(?:linked|confirmed)\s+(\S+)\s+\(([^)]*)\)", d)
            alias = m.group(2) if m else ""
            rows.append({"activity_id": r["activity_id"], "t": parse_ts(r["event_time"]), "resource_id": r["resource_id"],
                         "system": r["target_system"], "alias": alias, "idents": idents_from([("d", d)]),
                         "only_subject": "belongs only to" in d})
    _CATALOG_CACHE[root] = rows
    return rows
