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
//   substring: one rotation item[o..]·item[..o] per offset, Repeat at o as the value
//              US99: US99· (0), S99·U (0), 99·US (0), 9·US9 (1)
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

    // Each item comes back once, from its first match: a later rotation is skipped when the fragment also starts at an
    // earlier offset (fragment.Length <= Repeat). US99, Filter("9"): 99·US kept (0), 9·US9 skipped (1).
    public IReadOnlyList<string> Filter(string fragment)
    {
        // Every item contains "", also "" itself, which has no substring entry.
        if (fragment.Length == 0) return GetByStartsWith(fragment);

        return Scan(_substring, Encoding.UTF8.GetBytes(fragment), (iterator, hits) =>
        {
            var key = iterator.GetKeySpan();
            var value = iterator.GetValueSpan();

            if (fragment.Length <= BinaryPrimitives.ReadUInt16LittleEndian(value)) return;
            var tail = key.IndexOf(Separator);
            var item = hits.Add(key.Length - 1);
            key[..tail].CopyTo(item[^tail..]);
            key[(tail + 1)..].CopyTo(item);
        });
    }

    // Every entry of family whose key starts with key, decoded.
    // Count still walks every hit (~120 ns each through the binding); only the string per hit is saved, see Utf8Hits.
    private Utf8Hits Scan(ColumnFamilyHandle family, byte[] key, Decode decode)
    {
        var options = new ReadOptions();
        
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

        var hits = new Utf8Hits();
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
        var key = new byte[utf8.Length + 1];
        Span<byte> repeat = stackalloc byte[2];

        Put(utf8, []);
        utf8.CopyTo(key, 0);
        key.AsSpan(0, utf8.Length).Reverse();
        Put(key.AsSpan(0, utf8.Length), [], _suffix);
        
        // By rune, so a surrogate pair's low half gets no rotation of its own (it would encode as U+FFFD and match one).
        // A lone surrogate comes as U+FFFD (1 char, 3 bytes), as Encoding.UTF8 writes it, so o and b stay in step.
        int o = 0, b = 0;
        foreach (var rune in item.EnumerateRunes())
        {
            // write each key as item[o..] + 0xFF + item[..o] | US99 -> 99·US (o = b = 2)
            utf8.AsSpan(b).CopyTo(key);
            key[utf8.Length - b] = Separator;
            utf8.AsSpan(0, b).CopyTo(key.AsSpan(utf8.Length - b + 1));
            
            // shortcut: Repeat is 2 bytes, so items are limited to 65,535 chars; widen it if longer ones appear.
            if (!remove) BinaryPrimitives.WriteUInt16LittleEndian(repeat, checked((ushort)RepeatAt(item, o)));
            Put(key, repeat, _substring);

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
