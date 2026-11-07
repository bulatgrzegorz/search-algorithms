namespace SearchAlgorithms;

// Baseline: no index, checks every item on every query. Searches share a read lock, writes take it exclusively.
public sealed class LinearScan(string[] items) : ISearchAlgorithm
{
    readonly HashSet<string> _items = new(items, StringComparer.Ordinal);
    readonly ReaderWriterLockSlim _lock = new();

    public IReadOnlyList<string> GetByStartsWith(string prefix) =>
        FindAll(x => x.StartsWith(prefix, StringComparison.Ordinal));

    public IReadOnlyList<string> GetByEndsWith(string suffix) =>
        FindAll(x => x.EndsWith(suffix, StringComparison.Ordinal));

    public IReadOnlyList<string> Filter(string fragment) =>
        FindAll(x => x.Contains(fragment, StringComparison.Ordinal));

    public void Add(string item)
    {
        _lock.EnterWriteLock();
        try { _items.Add(item); }
        finally { _lock.ExitWriteLock(); }
    }

    public void Remove(string item)
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

    List<string> FindAll(Func<string, bool> match)
    {
        _lock.EnterReadLock();
        try { return _items.Where(match).ToList(); }
        finally { _lock.ExitReadLock(); }
    }
}
