using System.Buffers.Binary;
using System.Text;
using RocksDbSharp;
using Directory = System.IO.Directory;

namespace SearchAlgorithms;

// Baseline for RocksDbIndex: the textbook setup. Default options (WAL on, default cache and compression), one key per entry
// written with one Put each, no compaction. StartsWith seeks and checks each key's prefix; EndsWith and Filter read every key.
// A key is the Key's UTF-8, 0xFF (never in UTF-8, so a prefix can't run on into the Id), then the Id's 4 bytes.
public sealed class NaiveRocksDbIndex : ISearchAlgorithm
{
    private readonly string _path = Guid.NewGuid().ToString();
    private readonly RocksDb _db;

    public NaiveRocksDbIndex(Entry[] items)
    {
        _db = RocksDb.Open(new DbOptions().SetCreateIfMissing(true), _path);
        foreach (var item in items)
            Add(item);
    }

    public IReadOnlyList<Entry> GetByStartsWith(string prefix)
    {
        var bytes = Encoding.UTF8.GetBytes(prefix);
        using var iterator = _db.NewIterator();
        var hits = new List<Entry>();
        for (iterator.Seek(bytes); iterator.Valid(); iterator.Next())
        {
            var key = iterator.Key();
            if (!key.AsSpan().StartsWith(bytes)) break;
            hits.Add(Decode(key));
        }
        return hits;
    }

    public IReadOnlyList<Entry> GetByEndsWith(string suffix) => Scan(x => x.EndsWith(suffix, StringComparison.Ordinal));

    public IReadOnlyList<Entry> Filter(string fragment) => Scan(x => x.Contains(fragment, StringComparison.Ordinal));

    private List<Entry> Scan(Func<string, bool> matches)
    {
        using var iterator = _db.NewIterator();
        var hits = new List<Entry>();
        for (iterator.SeekToFirst(); iterator.Valid(); iterator.Next())
        {
            var entry = Decode(iterator.Key());
            if (matches(entry.Key)) hits.Add(entry);
        }
        return hits;
    }

    public void Add(Entry item) => _db.Put(Encode(item), Array.Empty<byte>());

    public void Remove(Entry item) => _db.Remove(Encode(item));

    private static byte[] Encode(Entry entry)
    {
        var utf8 = Encoding.UTF8.GetBytes(entry.Key);
        var key = new byte[utf8.Length + 5];
        utf8.CopyTo(key, 0);
        key[utf8.Length] = 0xFF;
        BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(utf8.Length + 1), entry.Id);
        return key;
    }

    private static Entry Decode(byte[] key) =>
        new(Encoding.UTF8.GetString(key, 0, key.Length - 5), BinaryPrimitives.ReadInt32BigEndian(key.AsSpan(key.Length - 4)));

    public ValueTask DisposeAsync()
    {
        _db.Dispose();
        Directory.Delete(_path, true);
        return ValueTask.CompletedTask;
    }
}
