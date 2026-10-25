# Common tasks. Everything runs from the repository root; `make` with no target lists them.
# Override the interpreter with `make test PYTHON=python3.13`. Nothing here needs an API key.

PYTHON ?= python3
CASE   ?=

.DEFAULT_GOAL := help
.PHONY: help setup test lint run score explain edge agent package check bench clean

help:
	@echo "make setup            install the Python dependencies (pypdf; anthropic is only used when ANTHROPIC_API_KEY is set)"
	@echo "make test             run the whole test suite"
	@echo "make lint             ruff (unused imports and variables, likely bugs) and mypy over the code; needs: pip install -r code/requirements-dev.txt"
	@echo "make run              answer all 518 requests: writes ./output.csv, ./output.notes.md, ./output.usage.jsonl"
	@echo "make score            run the 32 labeled sample requests and print the score report"
	@echo "make explain CASE=case_48   print how one case got its answer (read-only, writes nothing)"
	@echo "make edge             run the edge-case sweep (118 scenarios) and write code/evaluation/edge_case_sweep.md"
	@echo "make agent            run the no-change proofs and the fake-client tests of the opt-in agent layer -> code/evaluation/agent_layer_report.md"
	@echo "make package          check every submission rule and write ./code.zip"
	@echo "make check            build code.zip and run the final submission checklist (output.csv, log.txt, reports, zip contents)"
	@echo "make bench            profile the run at 1x, 2x, 5x, 10x and 20x the requests (a few minutes)"
	@echo "make clean            remove caches, checkpoints and generated scratch files (never output.csv or the reports)"

setup:
	$(PYTHON) -m pip install -r code/requirements.txt

test:
	$(PYTHON) -m unittest discover -s code/tests -t code

lint:
	cd code && ruff check . && mypy

run:
	$(PYTHON) code/main.py

score:
	$(PYTHON) code/evaluation/main.py

explain:
	@test -n "$(CASE)" || { echo "usage: make explain CASE=case_48" >&2; exit 2; }
	$(PYTHON) code/main.py --explain $(CASE)

edge:
	$(PYTHON) code/evaluation/edge_sweep.py --quiet

agent:
	$(PYTHON) code/evaluation/agent_proofs.py

package:
	$(PYTHON) code/tools/package.py --require-final

check:
	$(PYTHON) code/tools/final_check.py --build-zip

bench:
	$(PYTHON) code/tools/profile_run.py --repeat 1 2 5 10 20 --runs 3 --table

clean:
	rm -rf code/.cache code/out output.partial.jsonl code.zip
	find . -name __pycache__ -type d -prune -exec rm -rf {} +
