using System.Collections.Concurrent;

namespace SearchAlgorithms;

// LinearScan without the read lock: ConcurrentDictionary may be enumerated while it's written,
// so searches never block, but a search running during writes may or may not see them.
public sealed class ConcurrentLinearScan(Entry[] items) : ISearchAlgorithm
{
    private readonly ConcurrentDictionary<Entry, byte> _items = new(items.Select(x => KeyValuePair.Create(x, (byte)0)));

    public IReadOnlyList<Entry> GetByStartsWith(string prefix) =>
        FindAll(x => x.StartsWith(prefix, StringComparison.Ordinal));

    public IReadOnlyList<Entry> GetByEndsWith(string suffix) =>
        FindAll(x => x.EndsWith(suffix, StringComparison.Ordinal));

    public IReadOnlyList<Entry> Filter(string fragment) =>
        FindAll(x => x.Contains(fragment, StringComparison.Ordinal));

    public void Add(Entry item) => _items.TryAdd(item, 0);

    public void Remove(Entry item) => _items.TryRemove(item, out _);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // Enumerating the dictionary is lock-free; .Keys would take every lock and copy.
    private List<Entry> FindAll(Func<string, bool> match) =>
        _items.Where(x => match(x.Key.Key)).Select(x => x.Key).ToList();
}
