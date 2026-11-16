using System.Buffers.Binary;
using System.Collections;
using System.Text;
using RocksDbSharp;
using Directory = System.IO.Directory;

namespace SearchAlgorithms;

// All three searches as prefix scans over RocksDB's sorted keys, one column family each (see docs/plan-rocksdb-index.md).
// Items GB19, US99 (· is Separator):
//   default:   GB19, US99                                 GetByStartsWith: keys starting with the query
//   suffix:    91BG, 99SU (UTF-8 bytes reversed)          GetByEndsWith: keys starting with the reversed query
//   substring: one rotation item[o..]·item[..o] per offset, after the 2-byte Repeat at o (big-endian, so keys sort by it first)
//              US99: 0 US99·, 0 S99·U, 0 99·US, 1 9·US9
// The memtable is the delta, compaction the merge, an iterator the snapshot: a search sees every write that returned before it.
// Adding a present item or removing a missing one rewrites or deletes the same keys, so neither needs a check or a lock.
// shortcut: keys are UTF-8, so a lone surrogate in an item or query turns into U+FFFD; use raw UTF-16 keys if such data appears.
public sealed class RocksDbIndex : ISearchAlgorithm
{
    // Never occurs in UTF-8, so no item contains it.
    private const byte Separator = 0xFF;
    private readonly string _path = Guid.NewGuid().ToString();
    // Kept for the database's lifetime; the cache is shared by the three families and sized above the data (Huge ISINs).
    private readonly Cache _cache = Cache.CreateLru(512 << 20);
    // No log: the index is rebuilt from items and deleted on dispose.
    private readonly WriteOptions _writeOptions = new WriteOptions().DisableWal(1);
    private readonly RocksDb _db;
    private readonly ColumnFamilyHandle _prefix, _suffix, _substring;
    // Highest Repeat ever written, never lowered: Filter's buckets above it are empty, so it doesn't seek them.
    private int _maxRepeat;

    // Writes the entry's item into hits (hits.Add gives the room), or nothing to skip it.
    private delegate void Decode(Iterator iterator, Utf8Hits hits);
    
    public RocksDbIndex(string[] items)
    {
        var familyOptions =
            // No compression: keeps decompression out of the first profile.
            // Default 4 KB blocks: 16 and 64 KB measured the same (typeahead and single query, Medium); a scan's cost is per entry, not per block.
            new ColumnFamilyOptions()
            .SetCompression(Compression.No)
            .SetBlockBasedTableFactory(new BlockBasedTableOptions().SetBlockCache(_cache));
        var dbOptions = new DbOptions()
            .SetCreateIfMissing(true)
            .SetCreateMissingColumnFamilies(true)
            .IncreaseParallelism(Environment.ProcessorCount);

        _db = RocksDb.Open(dbOptions, _path, new ColumnFamilies(familyOptions)
        {
            { "suffix", familyOptions }, 
            { "substring", familyOptions }
        });
        
        (_prefix, _suffix, _substring) = (
            _db.GetDefaultColumnFamily(), 
            _db.GetColumnFamily("suffix"), 
            _db.GetColumnFamily("substring"));

        // Duplicates write the same keys, so no Distinct.
        Parallel.ForEach(items.Chunk(4096), chunk =>
        {
            using var batch = new WriteBatch();
            
            foreach (var item in chunk) 
                Stage(batch, item, remove: false);
            
            _db.Write(batch, _writeOptions);
        });
        
        // This index's ForceMerge(1): flushes each memtable and leaves one sorted file per family, so a scan merges nothing.
        foreach (var family in new[] { _prefix, _suffix, _substring })
            _db.CompactRange(null, null, family);
    }

    public IReadOnlyList<string> GetByStartsWith(string prefix) =>
        Scan(_prefix, Encoding.UTF8.GetBytes(prefix), static (iterator, hits) =>
        {
            var key = iterator.GetKeySpan();
            key.CopyTo(hits.Add(key.Length));
        });

    public IReadOnlyList<string> GetByEndsWith(string suffix)
    {
        var key = Encoding.UTF8.GetBytes(suffix);
        key.AsSpan().Reverse();
        return Scan(_suffix, key, static (iterator, hits) =>
        {
            var key = iterator.GetKeySpan();
            var item = hits.Add(key.Length);
            key.CopyTo(item);
            item.Reverse();
        });
    }

    // Each item comes back once, from its first match: there Repeat < fragment.Length, at every later match Repeat >= fragment.Length
    // (the fragment also starts earlier). So only buckets 0 .. fragment.Length - 1 are read, and every entry in them is a hit.
    //   US99, Filter("9"): bucket 0 has 99·US -> US99; 9·US9 sits in bucket 1, never read.
    // Against Repeat as the value (every entry walked, value read, duplicates skipped), Medium typeahead Filter:
    // ISINs 19.9 -> 11.8 ms, names 16.0 -> 8.6 ms; single 3-6 char Filter ISINs 798 -> 682 us, names unchanged.
    public IReadOnlyList<string> Filter(string fragment)
    {
        // Every item contains "", also "" itself, which has no substring entry.
        if (fragment.Length == 0) return GetByStartsWith(fragment);

        var utf8 = Encoding.UTF8.GetBytes(fragment);
        var hits = new Utf8Hits();
        // Every bucket's iterator reads this one snapshot, so the search is one point in time (without it, a Remove then Add
        // between two buckets' iterators could return both items). _maxRepeat is read after taking it: Stage raises it before
        // writing, so every batch the snapshot holds has already raised it.
        using var snapshot = _db.CreateSnapshot();
        var buckets = Math.Min(fragment.Length, Volatile.Read(ref _maxRepeat) + 1);
        for (var repeat = 0; repeat < buckets; repeat++)
        {
            var key = new byte[2 + utf8.Length];
            BinaryPrimitives.WriteUInt16BigEndian(key, (ushort)repeat);
            utf8.CopyTo(key, 2);
            Scan(_substring, key, static (iterator, hits) =>
            {
                var rotation = iterator.GetKeySpan()[2..];
                var tail = rotation.IndexOf(Separator);
                var item = hits.Add(rotation.Length - 1);
                rotation[..tail].CopyTo(item[^tail..]);
                rotation[(tail + 1)..].CopyTo(item);
            }, hits, snapshot);
        }
        return hits;
    }

    // Every entry of family whose key starts with key, decoded into hits (a new list unless given one to add to),
    // as of snapshot when given, else as of now.
    // Count still walks every hit (~120 ns each through the binding); only the string per hit is saved, see Utf8Hits.
    private Utf8Hits Scan(ColumnFamilyHandle family, byte[] key, Decode decode, Utf8Hits? hits = null, Snapshot? snapshot = null)
    {
        var options = new ReadOptions();
        if (snapshot is not null) options.SetSnapshot(snapshot);

        // We are adding 1 or last byte (turning 'US' -> 'UT'),
        // that's the smallest byte string that sorts after every string starting with 'US'
        // SetIterateUpperBound tells RocksDB to stop there (exclusive),
        // once iterator would reach 'UT' Valid() returns false
        if (key.Length > 0)
        {
            var bound = (byte[])key.Clone();
            bound[^1]++;
            options.SetIterateUpperBound(bound);
        }

        hits ??= new Utf8Hits();
        using (var iterator = _db.NewIterator(family, options))
        {
            if (key.Length > 0) iterator.Seek(key);
            else iterator.SeekToFirst();
            for (; iterator.Valid(); iterator.Next())
                decode(iterator, hits);
        }
        GC.KeepAlive(options); // owns the bound's native copy (AllocHGlobal, freed by its finalizer), read by the iterator until disposed
        return hits;
    }

    public void Add(string item) => Write(item, remove: false);

    public void Remove(string item) => Write(item, remove: true);

    // One batch across the three families is applied atomically, so no search sees an item in one family and not another.
    private void Write(string item, bool remove)
    {
        using var batch = new WriteBatch();
        Stage(batch, item, remove);
        _db.Write(batch, _writeOptions);
    }

    // The item's 2 + n keys: itself, its reversed bytes (not chars, which would split surrogate pairs), one rotation per char.
    private void Stage(WriteBatch batch, string item, bool remove)
    {
        var utf8 = Encoding.UTF8.GetBytes(item);
        // Room for the longest key: 2-byte Repeat + rotation + separator.
        var key = new byte[2 + utf8.Length + 1];

        Put(utf8, []);
        utf8.CopyTo(key, 0);
        key.AsSpan(0, utf8.Length).Reverse();
        Put(key.AsSpan(0, utf8.Length), [], _suffix);
        
        // By rune, so a surrogate pair's low half gets no rotation of its own (it would encode as U+FFFD and match one).
        // A lone surrogate comes as U+FFFD (1 char, 3 bytes), as Encoding.UTF8 writes it, so o and b stay in step.
        int o = 0, b = 0;
        foreach (var rune in item.EnumerateRunes())
        {
            // write each key as Repeat + item[o..] + 0xFF + item[..o] | US99 -> 0 99·US (o = b = 2)
            // Remove needs Repeat too: it is part of the key.
            // shortcut: Repeat is 2 bytes, so items are limited to 65,535 chars; widen it if longer ones appear.
            var repeat = checked((ushort)RepeatAt(item, o));
            BinaryPrimitives.WriteUInt16BigEndian(key, repeat);
            utf8.AsSpan(b).CopyTo(key.AsSpan(2));
            key[2 + utf8.Length - b] = Separator;
            utf8.AsSpan(0, b).CopyTo(key.AsSpan(2 + utf8.Length - b + 1));
            Put(key, [], _substring);
            // Before the batch is written, so a search that sees this key also reads its bucket.
            if (!remove)
            {
                for (var max = _maxRepeat; repeat > max; max = _maxRepeat)
                    Interlocked.CompareExchange(ref _maxRepeat, repeat, max);
            }
                

            o += rune.Utf16SequenceLength; //chars consumed -> 1 or 2
            b += rune.Utf8SequenceLength; // bytes consumer -> 1-4 bytes
        }
        return;

        void Put(ReadOnlySpan<byte> k, ReadOnlySpan<byte> v, ColumnFamilyHandle? family = null)
        {
            if (remove) batch.Delete(k, family ?? _prefix);
            else batch.Put(k, v, family ?? _prefix);
        }
    }

    // Longest stretch from offset, in chars, that also starts at an earlier offset of item (as SortedArrayIndex.RepeatAt).
    //   ("US99", 3) -> 1   "9" vs "US99", "S99", "99": common prefixes 0, 0, 1
    private static int RepeatAt(string item, int offset)
    {
        var repeat = 0;
        for (var j = 0; j < offset; j++)
            repeat = Math.Max(repeat, item.AsSpan(j).CommonPrefixLength(item.AsSpan(offset)));
        return repeat;
    }

    // Searches dispose their iterators before returning, and the interface calls this after all of them.
    public ValueTask DisposeAsync()
    {
        _db.Dispose();
        Directory.Delete(_path, true);
        return ValueTask.CompletedTask;
    }

    // Hits as UTF-8 back to back, each decoded to a string only when read: a caller reading Count and 10 hits (typeahead)
    // allocates two growing arrays instead of a string per hit (~16 B per ISIN hit instead of ~56 B). Filled by Scan, unchanged once returned.
    // Medium typeahead: StartsWith 18.4 -> 15.8 ms, Filter 22.4 -> 19.9 ms. The doubling arrays of a large result land on the
    // Large Object Heap, so Gen2 collections went up (656 -> 1562 per 1000 typed queries); fixed-size chunks would avoid that.
    //   [GB19, US99]: bytes "GB19US99", ends [4, 8]
    // A hit read twice is decoded twice.
    private sealed class Utf8Hits : IReadOnlyList<string>
    {
        private byte[] _bytes = new byte[256];
        private int[] _ends = new int[16];

        public int Count { get; private set; }

        public string this[int i]
        {
            get
            {
                if (i < 0 || i >= Count) throw new ArgumentOutOfRangeException(nameof(i));
                var start = i == 0 ? 0 : _ends[i - 1];
                return Encoding.UTF8.GetString(_bytes, start, _ends[i] - start);
            }
        }

        // Room for the next hit's bytes, for the caller to fill.
        public Span<byte> Add(int length)
        {
            var start = Count == 0 ? 0 : _ends[Count - 1];
            if (start + length > _bytes.Length) Array.Resize(ref _bytes, Math.Max(_bytes.Length * 2, start + length));
            if (Count == _ends.Length) Array.Resize(ref _ends, _ends.Length * 2);
            _ends[Count++] = start + length;
            return _bytes.AsSpan(start, length);
        }

        public IEnumerator<string> GetEnumerator()
        {
            for (var i = 0; i < Count; i++) yield return this[i];
        }
        
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
