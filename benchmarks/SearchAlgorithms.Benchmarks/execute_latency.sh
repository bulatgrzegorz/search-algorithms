#!/usr/bin/env bash
# Tail latency (p50..max) at a fixed query rate, open loop, see LatencyRun.cs. Not BenchmarkDotNet: prints a markdown table.
# Args: [size] [queries/s] [seconds per query kind] [writes/s], default Medium 1000 10 0, e.g. ./execute_latency.sh Medium 2000 10 1000
# Env: ALGORITHM=<name> to run one algorithm only, DATASET=isin|names. DOTNET_gcServer=1 for ASP.NET's GC (tails depend on it).
set -e
cd "$(dirname "$0")"
dotnet run -c Release -- latency "$@"
