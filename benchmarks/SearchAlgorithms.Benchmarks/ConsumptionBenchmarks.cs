using BenchmarkDotNet.Attributes;

namespace SearchAlgorithms.Benchmarks;

// Complements Count/indexed-top-ten benchmarks with common ways callers consume the returned list.
public class ConsumptionBenchmarks
{
    [Params(Scale.Medium)] public Scale Size;
    [ParamsSource(nameof(AlgorithmNames))] public string Algorithm = "";
    [Params(0, 1000)] public int WritesPerSecond;
    public static IEnumerable<string> AlgorithmNames => Selected.AlgorithmNames;

    private ISearchAlgorithm _algorithm = null!;
    private string[] _prefixes = [], _suffixes = [], _fragments = [];
    private int _next;
    private readonly CancellationTokenSource _stop = new();
    private Task _writer = Task.CompletedTask;

    [GlobalSetup]
    public void Setup()
    {
        var all = Dataset.Load((int)Size + 1000);
        var items = all[..(int)Size];
        _algorithm = Algorithms.All[Algorithm](items);
        (_prefixes, _suffixes, _fragments) = Dataset.Queries(items);
        if (WritesPerSecond > 0) _writer = Task.Run(() => Console.WriteLine(
            $"Writer: {Writer.Run(_algorithm, items[..1000], all[(int)Size..], WritesPerSecond, _stop.Token):N0} writes/s achieved, target {WritesPerSecond}"));
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _stop.CancelAsync();
        await _writer;
        await _algorithm.DisposeAsync();
        _stop.Dispose();
    }

    [Benchmark]
    public long FilterTake10()
    {
        var q = _fragments[_next++ % _fragments.Length][..1];
        long sum = 0;
        foreach (var item in _algorithm.Filter(q).Take(10)) sum += item.Key.Length + item.Id;
        return sum;
    }

    [Benchmark]
    public long FilterAll()
    {
        var q = _fragments[_next++ % _fragments.Length][..1];
        return ReadAll(_algorithm.Filter(q));
    }

    [Benchmark]
    public long FilterNarrowAll()
    {
        var q = _fragments[_next++ % _fragments.Length];
        return ReadAll(_algorithm.Filter(q));
    }

    [Benchmark]
    public long FilterIndex64()
    {
        var q = _fragments[_next++ % _fragments.Length][..1];
        var hits = _algorithm.Filter(q);
        if (hits.Count == 0) return 0;
        var hit = hits[Math.Min(64, hits.Count - 1)];
        return hit.Key.Length + hit.Id;
    }

    [Benchmark]
    public long StartsWithAll()
    {
        var q = _prefixes[_next++ % _prefixes.Length][..1];
        return ReadAll(_algorithm.GetByStartsWith(q));
    }

    [Benchmark]
    public long EndsWithAll()
    {
        var q = _suffixes[_next++ % _suffixes.Length][^1..];
        return ReadAll(_algorithm.GetByEndsWith(q));
    }

    private static long ReadAll(IReadOnlyList<Entry> hits)
    {
        long sum = 0;
        foreach (var item in hits) sum += item.Key.Length + item.Id;
        return sum;
    }
}
