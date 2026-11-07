namespace SearchAlgorithms;

// Items are a set of unique strings, received once in the constructor (build any index there).
// Adding an existing item or removing a missing one does nothing.
// Queries are ordinal (case-sensitive) and result order is free.
// All members may be called concurrently from many threads (searches while data changes); DisposeAsync is called once, after all of them.
public interface ISearchAlgorithm : IAsyncDisposable
{
    IReadOnlyList<string> GetByStartsWith(string prefix);
    IReadOnlyList<string> GetByEndsWith(string suffix);
    IReadOnlyList<string> Filter(string fragment);
    void Add(string item);
    void Remove(string item);
}

public static class Algorithms
{
    // Register new implementations here; benchmarks and tests pick them up automatically.
    public static readonly Dictionary<string, Func<string[], ISearchAlgorithm>> All = new()
    {
        [nameof(LinearScan)] = items => new LinearScan(items),
        [nameof(ConcurrentLinearScan)] = items => new ConcurrentLinearScan(items),
        [nameof(LuceneIndex)] =  items => new LuceneIndex(items),
    };
}
