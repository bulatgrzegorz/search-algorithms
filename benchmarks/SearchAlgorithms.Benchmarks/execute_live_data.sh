#!/usr/bin/env bash
# Search latency with no writes vs 1000 writes/s in the background, Medium.
# Env: JOB=dry|short|default (default short), ALGORITHM=<name> to run one algorithm only.
set -e
cd "$(dirname "$0")"
dotnet run -c Release -- --job "${JOB:-short}" --filter '*SearchBenchmarks*(Requests:?1,?WritesPerSecond:?0,*Size:?Medium)' '*SearchBenchmarks*(Requests:?1,?WritesPerSecond:?1000,*Size:?Medium)'
