#!/usr/bin/env bash
# Single-query latency of StartsWith/EndsWith/Filter, no writes, Small + Medium.
# Env: JOB=dry|short|default (default short), ALGORITHM=<name> to run one algorithm only. Extra args go to BenchmarkDotNet, e.g. --profiler EP.
set -e
cd "$(dirname "$0")"
dotnet run -c Release -- --job "${JOB:-short}" --filter '*SearchBenchmarks*(Requests:?1,?WritesPerSecond:?0,*Size:?Small)' '*SearchBenchmarks*(Requests:?1,?WritesPerSecond:?0,*Size:?Medium)' "$@"
