# Common tasks. Everything runs from the repository root; `make` with no target lists them.
# Needs the .NET 10 SDK (https://dotnet.microsoft.com/download). Nothing here needs an API key.
# Override the build configuration with `make test CONFIG=Debug`.

CONFIG ?= Release
CASE   ?=
SLN    := code/Auditor.sln
CLI    := dotnet run --no-build -c $(CONFIG) --project code/src/Auditor.Cli --
TOOLS  := dotnet run --no-build -c $(CONFIG) --project code/tools/Auditor.Tools --

.DEFAULT_GOAL := help
.PHONY: help setup build test run score explain agent reports bench mutation package check clean

help:
	@echo "make setup            restore the NuGet packages (the .NET 10 SDK is the only prerequisite)"
	@echo "make build            build the library, the command line, the tools and the tests (warnings are errors)"
	@echo "make test             run the whole test suite (xUnit); writes code/out/tests.trx"
	@echo "make run              answer all 518 requests: writes ./output.csv, ./output.notes.md, ./output.usage.jsonl"
	@echo "make score            run the 32 labeled sample requests and print the score report"
	@echo "make explain CASE=case_48   print how one case got its answer (read-only, writes nothing)"
	@echo "make agent            run the no-change proofs of the opt-in agent layer -> code/evaluation/agent_layer_report.md"
	@echo "make reports          regenerate evaluation_report.md, extra_cases_results.md, baseline_results.md, agent_layer_report.md (runs the tests first)"
	@echo "make bench            performance report at 1x ... 40x the requests (a few minutes) -> code/evaluation/performance_report.md"
	@echo "make mutation         break 5 rules one at a time in a scratch copy; the tests must fail for each (minutes)"
	@echo "make package          check every submission rule and write ./code.zip"
	@echo "make check            build code.zip and run the final submission checklist (output.csv, log.txt, reports, zip contents)"
	@echo "make clean            remove build output, caches, checkpoints and generated scratch files (never output.csv or the reports)"

setup:
	dotnet restore $(SLN)

build:
	dotnet build $(SLN) -c $(CONFIG) --nologo

test: build
	@mkdir -p code/out
	dotnet test $(SLN) -c $(CONFIG) --no-build --nologo --logger "trx;LogFileName=$(CURDIR)/code/out/tests.trx"

run: build
	$(CLI)

score: build
	@mkdir -p code/out
	$(CLI) --requests sample_deletion_requests.csv --out code/out/pred_samples.csv
	$(TOOLS) score --pred code/out/pred_samples.csv

explain: build
	@test -n "$(CASE)" || { echo "usage: make explain CASE=case_48" >&2; exit 2; }
	$(CLI) --explain $(CASE)

agent: build
	$(TOOLS) agent-proofs

reports: test
	$(TOOLS) baselines
	$(TOOLS) extra-cases
	$(TOOLS) agent-proofs
	$(TOOLS) evaluation-report

bench: build
	$(TOOLS) performance-report

mutation: build
	$(TOOLS) mutation-check --write

package: build
	$(TOOLS) package --require-final

check: build
	$(TOOLS) final-check --build-zip

clean:
	rm -rf code/.cache code/out output.partial.jsonl code.zip
	find . \( -name bin -o -name obj -o -name TestResults \) -type d -prune -exec rm -rf {} +
