#!/usr/bin/env bash
# Memory (heap, RSS, disk), build time and first-query time per index, one process each, see FootprintRun.cs. Prints a markdown table.
# Args: [item count], default 1000000; 10000000 = all of data/isin.txt.
# Env: ALGORITHM=<name> to run one algorithm only, DATASET=isin|names, HEAP_LIMIT_MB=<n> to run under a container-like GC heap limit.
set -e
cd "$(dirname "$0")"
dotnet run -c Release -- footprint "$@"
