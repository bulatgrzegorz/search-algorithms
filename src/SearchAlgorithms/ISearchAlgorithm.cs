namespace SearchAlgorithms;

// What queries match (Key) and what the caller gets back to find its entity by (Id).
// An entry is both: two entities may share a Key (two companies with one name), one entity may have several Keys (aliases).
public readonly record struct Entry(string Key, int Id);

// Items are a set of unique entries, received once in the constructor (build any index there).
// Adding an existing entry or removing a missing one does nothing.
// Queries match Keys, ordinal (case-sensitive), and result order is free.
// All members may be called concurrently from many threads (searches while data changes); DisposeAsync is called once, after all of them.
public interface ISearchAlgorithm : IAsyncDisposable
{
    IReadOnlyList<Entry> GetByStartsWith(string prefix);
    IReadOnlyList<Entry> GetByEndsWith(string suffix);
    IReadOnlyList<Entry> Filter(string fragment);
    void Add(Entry item);
    void Remove(Entry item);
}

public static class Algorithms
{
    // Register new implementations here; benchmarks and tests pick them up automatically.
    public static readonly Dictionary<string, Func<Entry[], ISearchAlgorithm>> All = new()
    {
        // [nameof(LinearScan)] = items => new LinearScan(items),
        // [nameof(ConcurrentLinearScan)] = items => new ConcurrentLinearScan(items),
        // [nameof(LuceneIndex)] =  items => new LuceneIndex(items),
        // [nameof(NaiveLuceneIndex)] = items => new NaiveLuceneIndex(items),
        [nameof(SortedArrayIndex)] = items => new SortedArrayIndex(items),
        [nameof(RocksDbIndex)] = items => new RocksDbIndex(items),
        [nameof(NaiveRocksDbIndex)] = items => new NaiveRocksDbIndex(items),
    };
}
