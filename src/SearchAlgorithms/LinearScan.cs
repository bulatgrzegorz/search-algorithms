namespace SearchAlgorithms;

// Baseline: no index, checks every item on every query. Searches share a read lock, writes take it exclusively.
public sealed class LinearScan(Entry[] items) : ISearchAlgorithm
{
    readonly HashSet<Entry> _items = new(items);
    readonly ReaderWriterLockSlim _lock = new();

    public IReadOnlyList<Entry> GetByStartsWith(string prefix) =>
        FindAll(x => x.StartsWith(prefix, StringComparison.Ordinal));

    public IReadOnlyList<Entry> GetByEndsWith(string suffix) =>
        FindAll(x => x.EndsWith(suffix, StringComparison.Ordinal));

    public IReadOnlyList<Entry> Filter(string fragment) =>
        FindAll(x => x.Contains(fragment, StringComparison.Ordinal));

    public void Add(Entry item)
    {
        _lock.EnterWriteLock();
        try { _items.Add(item); }
        finally { _lock.ExitWriteLock(); }
    }

    public void Remove(Entry item)
    {
        _lock.EnterWriteLock();
        try { _items.Remove(item); }
        finally { _lock.ExitWriteLock(); }
    }

    public ValueTask DisposeAsync()
    {
        _lock.Dispose();
        return ValueTask.CompletedTask;
    }

    List<Entry> FindAll(Func<string, bool> match)
    {
        _lock.EnterReadLock();
        try { return _items.Where(x => match(x.Key)).ToList(); }
        finally { _lock.ExitReadLock(); }
    }
}
