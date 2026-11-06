using System.Diagnostics;
using BenchmarkDotNet.Attributes;

namespace SearchAlgorithms.Benchmarks;

// One invocation = `Requests` queries: 1 runs alone (latency), more run concurrently (throughput under load, see Req/s).
// WritesPerSecond > 0 runs a background writer during the whole benchmark to simulate live data.
public class SearchBenchmarks
{
    const int ChurnPool = 1000;

    [ParamsAllValues] public Scale Size;
    [ParamsSource(nameof(AlgorithmNames))] public string Algorithm = "";
    [Params(1, 100, 500)] public int Requests;
    [Params(0, 100, 1000)] public int WritesPerSecond;
    public static IEnumerable<string> AlgorithmNames => Algorithms.All.Keys;

    ISearchAlgorithm _algorithm = null!;
    string[] _prefixes = [], _suffixes = [], _fragments = [];
    int _next;
    readonly CancellationTokenSource _stop = new();
    Task _writer = Task.CompletedTask;

    [GlobalSetup]
    public void Setup()
    {
        var all = Dataset.Load((int)Size + ChurnPool);
        var items = all[..(int)Size];
        _algorithm = Algorithms.All[Algorithm](items);
        (_prefixes, _suffixes, _fragments) = Dataset.Queries(items);
        if (WritesPerSecond > 0) _writer = Task.Run(() => Write(items[..ChurnPool], all[(int)Size..]));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _stop.Cancel();
        _writer.Wait();
    }

    [Benchmark] public long StartsWith() => Run(_prefixes, static (a, q) => a.GetByStartsWith(q));
    [Benchmark] public long EndsWith() => Run(_suffixes, static (a, q) => a.GetByEndsWith(q));
    [Benchmark] public long Filter() => Run(_fragments, static (a, q) => a.Filter(q));

    // Returns total hits so the JIT can't drop the work.
    long Run(string[] queries, Func<ISearchAlgorithm, string, IReadOnlyList<string>> search)
    {
        var start = _next;
        _next = (start + Requests) % queries.Length;
        if (Requests == 1) return search(_algorithm, queries[start]).Count;
        long hits = 0;
        Parallel.For(0, Requests, i => Interlocked.Add(ref hits, search(_algorithm, queries[(start + i) % queries.Length]).Count));
        return hits;
    }

    // One write = an update: swap a present item for an absent one (Remove + Add), so the size stays constant.
    void Write(string[] present, string[] absent)
    {
        var clock = Stopwatch.StartNew();
        long done = 0;
        while (!_stop.IsCancellationRequested)
        {
            for (; done < clock.Elapsed.TotalSeconds * WritesPerSecond && !_stop.IsCancellationRequested; done++)
            {
                var k = (int)(done % present.Length);
                _algorithm.Remove(present[k]);
                _algorithm.Add(absent[k]);
                (present[k], absent[k]) = (absent[k], present[k]);
            }
            Thread.Sleep(1);
        }
        // Shows in the log whether the algorithm kept up with the target rate.
        Console.WriteLine($"Writer: {done / clock.Elapsed.TotalSeconds:N0} writes/s achieved, target {WritesPerSecond}");
    }
}

// Single-threaded cost of one Add / Remove. Each iteration does `Ops` of them, then an unmeasured cleanup undoes them.
// Ops is kept small so Small (10k) drifts at most 10% during an iteration.
[InvocationCount(Ops)]
public class MutationBenchmarks
{
    const int Ops = 1000;

    [ParamsAllValues] public Scale Size;
    [ParamsSource(nameof(AlgorithmNames))] public string Algorithm = "";
    public static IEnumerable<string> AlgorithmNames => Algorithms.All.Keys;

    ISearchAlgorithm _algorithm = null!;
    string[] _removable = [], _fresh = [];
    int _i;

    [GlobalSetup]
    public void Setup()
    {
        var all = Dataset.Load((int)Size + Ops);
        _algorithm = Algorithms.All[Algorithm](all[..(int)Size]);
        (_removable, _fresh) = (all[..Ops], all[(int)Size..]);
    }

    [Benchmark] public void Add() => _algorithm.Add(_fresh[_i++ % Ops]);
    [Benchmark] public void Remove() => _algorithm.Remove(_removable[_i++ % Ops]);

    // Undo the whole pool (no-ops for untouched items), so it's correct however many invocations ran.
    [IterationCleanup(Target = nameof(Add))]
    public void UndoAdd() { foreach (var x in _fresh) _algorithm.Remove(x); _i = 0; }

    [IterationCleanup(Target = nameof(Remove))]
    public void UndoRemove() { foreach (var x in _removable) _algorithm.Add(x); _i = 0; }
}

// Index build cost; Allocated approximates the index's memory footprint.
public class BuildBenchmarks
{
    [ParamsAllValues] public Scale Size;
    [ParamsSource(nameof(AlgorithmNames))] public string Algorithm = "";
    public static IEnumerable<string> AlgorithmNames => Algorithms.All.Keys;

    string[] _items = [];

    [GlobalSetup] public void Setup() => _items = Dataset.Load((int)Size);

    [Benchmark] public ISearchAlgorithm Build() => Algorithms.All[Algorithm](_items);
}
