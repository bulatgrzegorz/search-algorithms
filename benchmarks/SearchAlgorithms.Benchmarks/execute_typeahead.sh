#!/usr/bin/env bash
# Typing into a search box: one StartsWith/Filter per keystroke, first 10 hits read, Medium.
# Env: JOB=dry|short|default (default short), ALGORITHM=<name> to run one algorithm only, DATASET=isin|names. Extra args go to BenchmarkDotNet, e.g. --profiler EP.
set -e
cd "$(dirname "$0")"
dotnet run -c Release -- --job "${JOB:-short}" --filter '*TypeaheadBenchmarks*Size:?Medium)' "$@"
