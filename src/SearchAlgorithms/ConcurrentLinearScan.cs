using System.Collections.Concurrent;

namespace SearchAlgorithms;

// LinearScan without the read lock: ConcurrentDictionary may be enumerated while it's written,
// so searches never block, but a search running during writes may or may not see them.
public sealed class ConcurrentLinearScan(string[] items) : ISearchAlgorithm
{
    readonly ConcurrentDictionary<string, byte> _items = new(items.Select(x => KeyValuePair.Create(x, (byte)0)), StringComparer.Ordinal);

    public IReadOnlyList<string> GetByStartsWith(string prefix) =>
        FindAll(x => x.StartsWith(prefix, StringComparison.Ordinal));

    public IReadOnlyList<string> GetByEndsWith(string suffix) =>
        FindAll(x => x.EndsWith(suffix, StringComparison.Ordinal));

    public IReadOnlyList<string> Filter(string fragment) =>
        FindAll(x => x.Contains(fragment, StringComparison.Ordinal));

    public void Add(string item) => _items.TryAdd(item, 0);

    public void Remove(string item) => _items.TryRemove(item, out _);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // Enumerating the dictionary is lock-free; .Keys would take every lock and copy.
    List<string> FindAll(Func<string, bool> match) =>
        _items.Where(x => match(x.Key)).Select(x => x.Key).ToList();
}
