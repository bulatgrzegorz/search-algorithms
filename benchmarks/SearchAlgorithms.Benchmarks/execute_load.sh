#!/usr/bin/env bash
# 100 concurrent queries per op (see Req/s), no writes, Medium. Slow algorithms take long here: 100 x query time per op.
# Env: JOB=dry|short|default (default short), ALGORITHM=<name> to run one algorithm only. Extra args go to BenchmarkDotNet, e.g. --profiler EP.
set -e
cd "$(dirname "$0")"
dotnet run -c Release -- --job "${JOB:-short}" --filter '*SearchBenchmarks*(Requests:?100,?WritesPerSecond:?0,*Size:?Medium)' "$@"
