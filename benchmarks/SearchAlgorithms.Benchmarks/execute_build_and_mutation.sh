#!/usr/bin/env bash
# Index build time/memory and single Add/Remove cost, Medium.
# Env: JOB=dry|short|default (default short), ALGORITHM=<name> to run one algorithm only.
set -e
cd "$(dirname "$0")"
dotnet run -c Release -- --job "${JOB:-short}" --filter '*BuildBenchmarks*Size:?Medium)' '*MutationBenchmarks*Size:?Medium)'
