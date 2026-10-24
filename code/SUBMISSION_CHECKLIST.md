# Submission checklist (prepared in Phase 1; verified item by item in Phase 5)

## Files to upload
- [ ] `code.zip` - built from the repo root with the one command documented in `README.md` (Phase 5). Must contain `README.md`, `code/` (incl. `code/evaluation/evaluation_report.md` and `code/evaluation/usage_report.md`).
- [ ] `output.csv` - root-level, 518 data rows + header, row order = `dataset/deletion_requests.csv`, passes `auditor.validate.validate_output_file`.
- [ ] `chat_transcript` = root `log.txt` (append-only; every entry has non-empty `tool=` and `model=`).
- [ ] `agent-trace.zip` (you export it yourself; steps below).
- [ ] AI interview: `code/INTERVIEW_PREP.md` (Phase 5).

## code.zip must NOT contain
- [ ] `dataset/` (already tracked in the repo by the starter commit - the zip command must exclude it)  - [ ] `.env` / keys  - [ ] `.venv`/`venv`/`node_modules`/`__pycache__`
- [ ] nested archives (`.zip .tar .gz .tgz .7z .rar`)  - [ ] `code/out/`, `code/.cache/`  - [ ] real personal data
- [ ] Reports are Markdown (the code judge ignores `.csv .tsv .json .jsonl .sql .log`)

## Agent trace (`agent_trace_instructions.md`) - you do this at the very end
1. Finish the session(s) and quit Claude Code first (so files are fully written).
2. Create a temp folder `agent-trace/claude-code/`.
3. In each session run `/export` and save the readable transcript into that folder.
4. Copy the raw session JSONL files for this repo from `~/.claude/projects/` (folder name encodes the path; check timestamps) into `agent-trace/claude-code/`. This cloud session may keep its transcript on the claude.ai side: if there is no local JSONL, use the interface's export and say so ("hosted session, no local trace file").
5. Do NOT copy `auth.json`, `settings.json`, credentials, `.env`; skim the files for secrets/personal data.
6. `zip -r agent-trace.zip agent-trace` (or PowerShell `Compress-Archive`), open it once to check contents.
7. If you used another harness (Codex, etc.) add its own subfolder.

## Final-run rules
- [ ] `usage_report.md` comes from a cold-cache full run of all 518 cases (cache dir deleted first); prices only from what you pasted (source + date recorded) or "unpriced".
- [ ] Submission URL taken from your registration email (not from AGENTS.md).
