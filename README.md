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

## Results snapshot (2026-11-08)

Apple M2 (8 cores), macOS 26.5, .NET 10.0.3, BenchmarkDotNet 0.15.8, `JOB=short` (3 warmup + 3 iterations, so expect ±10–50% noise; NaiveLuceneIndex is noisier still).
The four quick scripts with all algorithms, at commit `4622cbc` plus `NaiveLuceneIndex`. Mean times; lower is better except Req/s.

**Single-query latency, no writes** (`execute_main_comparison.sh`), μs:

| Algorithm            | Small StartsWith | Small EndsWith | Small Filter | Medium StartsWith | Medium EndsWith | Medium Filter |
|----------------------|-----------------:|---------------:|-------------:|------------------:|----------------:|--------------:|
| LinearScan           |             55.1 |           32.1 |         75.0 |               585 |             303 |           869 |
| ConcurrentLinearScan |              135 |            145 |          201 |             1,473 |           1,731 |         2,049 |
| LuceneIndex          |             10.6 |            1.7 |          4.5 |               133 |             3.6 |          41.7 |
| NaiveLuceneIndex     |            3,615 |            493 |        1,489 |             5,844 |           4,478 |        15,721 |

**Single-query latency, no writes vs 1000 writes/s** (`execute_live_data.sh`, Medium), μs. Every writer reached 1000 writes/s:

| Algorithm            | StartsWith 0 → 1000 | EndsWith 0 → 1000 | Filter 0 → 1000 |
|----------------------|--------------------:|------------------:|----------------:|
| LinearScan           |           620 → 638 |         350 → 526 |       941 → 890 |
| ConcurrentLinearScan |       1,609 → 1,723 |     2,018 → 1,895 |   2,288 → 2,262 |
| LuceneIndex          |           124 → 523 |          2.9 → 90 |      39 → 262   |
| NaiveLuceneIndex     |      5,717 → 10,218 |     5,226 → 9,486 | 18,199 → 51,005 |

**Throughput, 100 concurrent queries, no writes** (`execute_load.sh`, Medium), Req/s:

| Algorithm            | StartsWith | EndsWith | Filter |
|----------------------|-----------:|---------:|-------:|
| LinearScan           |      4,777 |    3,994 |  3,329 |
| ConcurrentLinearScan |      2,059 |    1,843 |  1,542 |
| LuceneIndex          |     10,182 |  325,043 | 31,177 |
| NaiveLuceneIndex     |         32 |      236 |     80 |

**Build and single write** (`execute_build_and_mutation.sh`, Medium). For the Lucene indexes, Add/Remove only buffer the change; the next search pays for making it visible:

| Algorithm            | Build    | Build allocated | Add      | Remove |
|----------------------|---------:|----------------:|---------:|-------:|
| LinearScan           |   2.5 ms |         2.07 MB |    61 ns |  43 ns |
| ConcurrentLinearScan |  13.4 ms |        11.54 MB |    75 ns |  51 ns |
| LuceneIndex          | 2,552 ms |        4,528 MB | 7,021 ns | 480 ns |
| NaiveLuceneIndex     |   130 ms |          259 MB | 8,188 ns | 789 ns |
