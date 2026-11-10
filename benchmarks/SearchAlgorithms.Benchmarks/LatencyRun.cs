using System.Diagnostics;

namespace SearchAlgorithms.Benchmarks;

// Tail latency at a fixed arrival rate, as a server sees it, which BenchmarkDotNet's mean of a closed loop can't show.
// Open loop: a query is sent every 1/rate s whether or not earlier ones finished, and its latency counts from when it was
// due, so a stall (GC, index refresh, merge) shows in every query queued behind it, not just in the one that hit it.
// Args: [size] [queries/s] [seconds per query kind] [writes/s], default Medium 1000 10 0. ALGORITHM and DATASET as for the benchmarks.
internal static class LatencyRun
{
    private const int ChurnPool = 1000, WarmupSeconds = 2;

    public static void Run(string[] args)
    {
        var size = args.Length > 0 ? Enum.Parse<Scale>(args[0]) : Scale.Medium;
        var rate = args.Length > 1 ? int.Parse(args[1]) : 1000;
        var seconds = args.Length > 2 ? int.Parse(args[2]) : 10;
        var writesPerSecond = args.Length > 3 ? int.Parse(args[3]) : 0;

        var all = Dataset.Load((int)size + ChurnPool);
        var items = all[..(int)size];
        var (prefixes, suffixes, fragments) = Dataset.Queries(items);
        (string Name, string[] Queries, Func<ISearchAlgorithm, string, IReadOnlyList<string>> Search)[] kinds =
        [
            ("StartsWith", prefixes, static (a, q) => a.GetByStartsWith(q)),
            ("EndsWith", suffixes, static (a, q) => a.GetByEndsWith(q)),
            ("Filter", fragments, static (a, q) => a.Filter(q)),
        ];

        Console.WriteLine($"Dataset {Dataset.Name}, {size}: {rate:N0} queries/s for {seconds} s per query kind, {writesPerSecond:N0} writes/s. Latency in μs.");
        Console.WriteLine();
        Console.WriteLine("| Algorithm | Query | p50 | p90 | p99 | p99.9 | Max | Writes/s achieved |");
        Console.WriteLine("|-----------|-------|----:|----:|----:|------:|----:|------------------:|");
        foreach (var name in Selected.AlgorithmNames)
        {
            var algorithm = Algorithms.All[name](items);
            // Copies, so this algorithm's writer starts from the same items as the last one's.
            var (present, absent) = (items[..ChurnPool], all[(int)size..]);
            foreach (var (kind, queries, search) in kinds)
            {
                using var stop = new CancellationTokenSource();
                var writer = writesPerSecond > 0
                    ? Task.Run(() => Writer.Run(algorithm, present, absent, writesPerSecond, stop.Token))
                    : Task.FromResult(0d);
                Measure(algorithm, queries, search, rate, WarmupSeconds); // JIT tiers up, lazy caches fill
                var latencies = Measure(algorithm, queries, search, rate, seconds);
                stop.Cancel();

                Array.Sort(latencies);
                string At(double percentile) => (latencies[(int)Math.Ceiling(percentile * latencies.Length) - 1] * 1e6 / Stopwatch.Frequency).ToString("N1");
                Console.WriteLine($"| {name} | {kind} | {At(0.5)} | {At(0.9)} | {At(0.99)} | {At(0.999)} | {At(1)} | {writer.Result:N0} |");
            }
            algorithm.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    // Each query's latency in Stopwatch ticks, from when it was due to when it returned.
    // Reads only .Count, like SearchBenchmarks, so the numbers compare.
    private static long[] Measure(ISearchAlgorithm algorithm, string[] queries, Func<ISearchAlgorithm, string, IReadOnlyList<string>> search, int rate, int seconds)
    {
        var latencies = new long[rate * seconds];
        using var done = new CountdownEvent(latencies.Length);
        var start = Stopwatch.GetTimestamp();
        for (var i = 0; i < latencies.Length; i++)
        {
            var (k, query, due) = (i, queries[i % queries.Length], start + i * Stopwatch.Frequency / rate);
            // Spins one core instead of sleeping: Sleep(1) oversleeps by up to a few ms, which would land in every latency.
            var spin = new SpinWait();
            while (Stopwatch.GetTimestamp() < due) spin.SpinOnce(sleep1Threshold: -1);
            ThreadPool.UnsafeQueueUserWorkItem(_ =>
            {
                _ = search(algorithm, query).Count;
                latencies[k] = Stopwatch.GetTimestamp() - due;
                done.Signal();
            }, null);
        }
        done.Wait();
        return latencies;
    }
}
