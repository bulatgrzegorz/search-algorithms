# search-algorithms

BenchmarkDotNet comparison of string search algorithms (`GetByStartsWith`, `GetByEndsWith`, `Filter` = contains) on real ISINs,
including live data (`Add` / `Remove` while searches run).

## Data

```sh
data/download.sh   # GLEIF ISIN->LEI mapping, ~9.4M unique ISINs -> data/isin.txt (gitignored)
```

Sizes (`Scale` in `Dataset.cs`): Small 10k, Medium 100k, Huge 1M. Each is a seeded random sample, and Small ⊂ Medium ⊂ Huge.
Queries are 3–6 char cuts of random items (seeded), so every algorithm sees the same queries.

## Add an algorithm

1. Implement `ISearchAlgorithm` in `src/SearchAlgorithms` (build the index in the constructor).
2. Register it in `Algorithms.All`.
3. `dotnet test`: checks it returns the same results as a LINQ scan, also after adds/removes, and that searching during writes doesn't throw.

Every member can be called concurrently (searches while data changes), so the implementation owns its synchronization.

## Run

```sh
cd benchmarks/SearchAlgorithms.Benchmarks
dotnet run -c Release -- --filter '*'                         # everything
dotnet run -c Release -- --filter '*SearchBenchmarks.Filter*'  # one method
dotnet run -c Release -- --filter '*.Filter(*Size: Small*'     # by parameter value
dotnet run -c Release -- --filter '*' --job short              # quicker, less precise
ALGORITHM=LinearScan dotnet run -c Release -- --filter '*'   # one algorithm
```

Quick runs (minutes, not hours): `execute_main_comparison.sh`, `execute_live_data.sh`, `execute_load.sh`, `execute_build_and_mutation.sh` in the benchmark folder.
Each takes `JOB=dry|short|default` (default `short`) and `ALGORITHM=<name>`, e.g. `ALGORITHM=LuceneIndex ./execute_main_comparison.sh`.

- `SearchBenchmarks`: Size × Algorithm × `Requests` (1 = single-query latency; 100/500 = that many concurrent queries per op) × `WritesPerSecond` (0/100/1000 updates/s by a background writer, each one Remove + Add). `Req/s` = Requests / Mean. Whether the writer kept up is logged: `grep Writer: BenchmarkDotNet.Artifacts/*.log`.
- `MutationBenchmarks`: single-threaded cost of one `Add` / `Remove`.
- `BuildBenchmarks`: index build time; `Allocated` ≈ index memory.
- Results: `BenchmarkDotNet.Artifacts/results` (markdown, csv, html).

## GitHub Actions

Manual only: Actions → Benchmarks → Run workflow (pick filter and job), or `gh workflow run benchmarks.yml -f filter='*' -f job=short`.
Tests run first, then one parallel job per registered algorithm, each with GitHub's 6 h job limit; a too-slow algorithm times out alone.
`short` is the default; `default` is precise but can take hours on the full filter.
Tables land in each job's summary, everything else in the `benchmark-results-<Algorithm>` artifacts.
Hosted runners are shared VMs (4 vCPU), so compare algorithms within one run, not across runs.
