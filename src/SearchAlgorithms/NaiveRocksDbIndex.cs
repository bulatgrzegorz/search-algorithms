using RocksDbSharp;
using Directory = System.IO.Directory;

namespace SearchAlgorithms;

// Baseline for RocksDbIndex: the textbook setup. Default options (WAL on, default cache and compression), one string key per item
// written with one Put each, no compaction. StartsWith seeks and checks each key's prefix; EndsWith and Filter read every key.
public sealed class NaiveRocksDbIndex : ISearchAlgorithm
{
    private readonly string _path = Guid.NewGuid().ToString();
    private readonly RocksDb _db;

    public NaiveRocksDbIndex(string[] items)
    {
        _db = RocksDb.Open(new DbOptions().SetCreateIfMissing(true), _path);
        foreach (var item in items)
            Add(item);
    }

    public IReadOnlyList<string> GetByStartsWith(string prefix)
    {
        using var iterator = _db.NewIterator();
        var hits = new List<string>();
        for (iterator.Seek(prefix); iterator.Valid(); iterator.Next())
        {
            var key = iterator.StringKey();
            if (!key.StartsWith(prefix, StringComparison.Ordinal)) break;
            hits.Add(key);
        }
        return hits;
    }

    public IReadOnlyList<string> GetByEndsWith(string suffix) => Scan(x => x.EndsWith(suffix, StringComparison.Ordinal));

    public IReadOnlyList<string> Filter(string fragment) => Scan(x => x.Contains(fragment, StringComparison.Ordinal));

    private List<string> Scan(Func<string, bool> matches)
    {
        using var iterator = _db.NewIterator();
        var hits = new List<string>();
        for (iterator.SeekToFirst(); iterator.Valid(); iterator.Next())
        {
            var key = iterator.StringKey();
            if (matches(key)) hits.Add(key);
        }
        return hits;
    }

    public void Add(string item) => _db.Put(item, "");

    public void Remove(string item) => _db.Remove(item);

    public ValueTask DisposeAsync()
    {
        _db.Dispose();
        Directory.Delete(_path, true);
        return ValueTask.CompletedTask;
    }
}
