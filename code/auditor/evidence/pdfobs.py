"""PDF evidence: extract the text layer (pypdf) and turn the resources it names into observations.

PDFs here are ordinary business documents (statement, support attachment, compliance journal, retention register).
A document that *names* a resource (by id or by a catalog alias such as a statement number) shows that the record
exists at capture time. The register of disputed orders names no resource id, so it yields nothing - exactly like the
labeled samples, whose users appear in it but have no legal-hold step.
"""
from __future__ import annotations

from datetime import datetime
from pathlib import Path

from .records import RESOURCE_RE, EvidenceBase, Obs, idents_from, system_for


def pdf_text(path: Path) -> str:
    from pypdf import PdfReader
    return "\n".join((page.extract_text() or "") for page in PdfReader(str(path)).pages)


def text_artifact_obs(base: EvidenceBase, file_id: str, file_t: datetime, text: str, catalog: list[dict], source: str) -> None:
    low = text.lower()
    idents = idents_from([("text", text)])
    seen: set[str] = set()
    for m in RESOURCE_RE.finditer(text):
        rid = m.group(0)
        if rid in seen:
            continue
        seen.add(rid)
        prefix = rid.rsplit("_", 1)[0]
        if prefix == "support":
            cat, state = ("SUPPORT_ATTACHMENT", "PRESENT") if "attachment" in low else ("SUPPORT_MESSAGE", "EXPOSED")
        elif prefix == "audit_record":
            cat, state = "SECURITY_AUDIT_LOG", "PRESENT"
        elif prefix == "export":
            cat, state = "USER_EXPORT", "PRESENT"
        else:
            continue            # other prefixes in free text are references, not records we can judge
        base.obs.append(Obs(file_id, file_t, file_t, system_for(rid), rid, cat, state, idents, source))
    for row in catalog:         # resources the document mentions only through a catalog alias (e.g. a statement number)
        if row["alias"] and row["alias"] != row["resource_id"] and row["alias"].lower() in low and row["resource_id"] not in seen:
            seen.add(row["resource_id"])
            prefix = row["resource_id"].rsplit("_", 1)[0]
            cat = {"export": "USER_EXPORT", "object": "SUPPORT_ATTACHMENT"}.get(prefix)
            if cat:
                base.obs.append(Obs(file_id, file_t, file_t, row["system"], row["resource_id"], cat, "PRESENT",
                                    idents | row["idents"], source + ":alias"))


def load_pdf(base: EvidenceBase, file_id: str, path: Path, file_t: datetime, catalog: list[dict]) -> None:
    text_artifact_obs(base, file_id, file_t, pdf_text(path), catalog, "pdf")
