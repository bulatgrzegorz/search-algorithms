using System.Collections;
using System.Collections.Concurrent;

namespace SearchAlgorithms;

// Every query is "the block of a sorted array whose entries start with the query", found by two binary searches.
// Published arrays are never changed (callers may still hold slices of them): writes collect in a small delta,
// and a merge folds it into new arrays.
//
// Example used in the comments below, items GB19, US12, US99:
//   ByPrefix   = [GB19, US12, US99]   StartsWith("US") -> block [1, 3) = US12, US99
//   BySuffix   = [US12, GB19, US99]   EndsWith("9")    -> block [1, 3) = GB19, US99
//   Substrings = 12 entries           Filter("9")      -> block [3, 6) = GB19, US99, US99 -> GB19, US99
//     (US12, 2) "12", (GB19, 2) "19", (US12, 3) "2", (GB19, 3) "9", (US99, 3) "9", (US99, 2) "99", (GB19, 1) "B19", ...
public sealed class SortedArrayIndex : ISearchAlgorithm
{
    // Pending changes (Added + Removed) that trigger a merge. A merge copies every item (208 B each), so the threshold grows
    // with them, 1 per 256 items, and each write's share of a merge stays ~53 KB (Huge: 3,906 pending instead of 256 cut it from 814 KB).
    private int MergeThreshold => Math.Max(256, _items.Count / 256);

    private readonly Lock _writeLock = new();
    // Writers only, under _writeLock: makes Add of a present item and Remove of a missing one no-ops.
    private readonly HashSet<string> _items;
    // Searches read it once (Volatile.Read) and use only that; writers publish a whole new one (Volatile.Write),
    // so a search that starts after a write returned sees it.
    private State _state;

    public SortedArrayIndex(string[] items)
    {
        _items = new HashSet<string>(items, StringComparer.Ordinal);
        _state = new State(Build(_items), Added: Delta.Empty, Removed: Delta.Empty);
    }

    // StartsWith("US"): block (1, 3) -> new ArraySegment<string>(ByPrefix, 1, 2) = US12, US99.
    public IReadOnlyList<string> GetByStartsWith(string prefix)
    {
        var state = Volatile.Read(ref _state);
        return WithDelta<PrefixOrder>(state.Base.ByPrefix, state.Removed.ByPrefix, state.Added.ByPrefix, prefix);
    }

    // EndsWith("9"): BySuffix [US12, GB19, US99], block (1, 3) -> GB19, US99.
    public IReadOnlyList<string> GetByEndsWith(string suffix)
    {
        var state = Volatile.Read(ref _state);
        return WithDelta<SuffixOrder>(state.Base.BySuffix, state.Removed.BySuffix, state.Added.BySuffix, suffix);
    }

    // An item matching at several offsets is in the block several times: keep only its first one,
    // the entry whose Repeat (see Substring) is shorter than fragment.
    // Filter("9"): block = (GB19, 3, Repeat 0), (US99, 3, Repeat 1), (US99, 2, Repeat 0).
    //   1 > 0 -> keep (GB19, 3)
    //   1 > 1 is false -> skip (US99, 3): "9" also starts at offset 2
    //   1 > 0 -> keep (US99, 2)
    //   -> [GB19, US99]
    // That kept entry is at the item's first match (IndexOf), so a removed item is one binary search away and skipped there.
    //   Removed [US99]: IndexOf("9") = 2, (US99, 2) is block entry 2 -> skip 2 -> [GB19]
    // The delta is scanned (IndexOf / Contains per item), not indexed like Base: keeping sorted substrings for it made
    // writes ~15x slower on Huge (each write copies them, ~200 B per pending item) and Filter no faster.
    public IReadOnlyList<string> Filter(string fragment)
    {
        // Every item contains "", also "" itself, which has no Substrings entry.
        if (fragment.Length == 0) return GetByStartsWith(fragment);

        var state = Volatile.Read(ref _state);
        var block = FindBlock<Substring, SubstringOrder>(state.Base.Substrings, fragment).AsSpan();
        List<int> removedAt = [];
        foreach (var x in state.Removed.ByPrefix)
        {
            var offset = x.IndexOf(fragment, StringComparison.Ordinal);
            // Its entry starts with fragment, so it is in the block.
            if (offset >= 0) removedAt.Add(block.BinarySearch(new Substring(x, offset, 0), default(SubstringOrder)));
        }
        removedAt.Sort();
        var hits = new List<string>(block.Length + state.Added.Count);
        var r = 0;
        for (var i = 0; i < block.Length; i++)
        {
            var (item, _, repeat) = block[i];
            if (r < removedAt.Count && removedAt[r] == i) r++;
            else if (fragment.Length > repeat) hits.Add(item);
        }
        // Added items are never in Base, so they can't repeat a hit.
        foreach (var x in state.Added.ByPrefix)
            if (x.Contains(fragment, StringComparison.Ordinal)) hits.Add(x);
        return hits;
    }

    // Delta, starting empty (Base = GB19, US12, US99):
    //   Add("FR77")    -> Added = [FR77]
    //   Remove("FR77") -> Added = []        it was only pending, Base never had it
    //   Remove("US12") -> Removed = [US12]
    //   Add("US12")    -> Removed = []      Base still has it
    // So Removed items are always in Base and Added ones never are.
    public void Add(string item)
    {
        lock (_writeLock)
        {
            if (!_items.Add(item)) return;
            var state = _state;
            Publish(state.Removed.Contains(item) ? state with { Removed = state.Removed.Without(item) } : state with { Added = state.Added.With(item) });
        }
    }

    public void Remove(string item)
    {
        lock (_writeLock)
        {
            if (!_items.Remove(item)) return;
            var state = _state;
            Publish(state.Added.Contains(item) ? state with { Added = state.Added.Without(item) } : state with { Removed = state.Removed.With(item) });
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // Under _writeLock.
    private void Publish(State state) =>
        Volatile.Write(ref _state, state.Added.Count + state.Removed.Count >= MergeThreshold ? Merge(state) : state);

    // The three sorts are independent, so they run at once.
    private static Snapshot Build(IReadOnlyCollection<string> items)
    {
        string[] prefixArray = [..items];
        string[] suffixArray = [..items];
        Substring[] substrings = [];
        Parallel.Invoke(
            () => Array.Sort(prefixArray, default(PrefixOrder)),
            () => Array.Sort(suffixArray, default(SuffixOrder)),
            () => substrings = SortedSubstrings(items));
        return new Snapshot(prefixArray, suffixArray, substrings);
    }

    // One entry per char of every item, sorted. The entries are first put in buckets by their first char (a counting sort),
    // so the buckets sort in parallel. Repeat is filled in after sorting, in parallel too: SubstringOrder ignores it.
    //   GB19, US12, US99 -> bucket '1': (GB19, 2), (US12, 2)   bucket '9': (GB19, 3), (US99, 2), (US99, 3)   ...
    private static Substring[] SortedSubstrings(IReadOnlyCollection<string> items)
    {
        // Bucket c is [start[c], start[c + 1]).
        var start = new int[char.MaxValue + 2];
        
        foreach (var item in items)
            foreach (var c in item)
                start[c + 1]++;
            
        for (var c = 1; c < start.Length; c++) start[c] += start[c - 1];

        var substrings = new Substring[start[^1]];
        var next = (int[])start.Clone();
        foreach (var item in items)
            for (var i = 0; i < item.Length; i++) substrings[next[item[i]]++] = new Substring(item, i, 0);

        // Biggest first and one at a time (NoBuffering), so a big bucket doesn't start last and leave the other cores idle.
        var buckets = Enumerable.Range(0, char.MaxValue + 1).Where(c => start[c + 1] - start[c] > 1).OrderByDescending(c => start[c + 1] - start[c]);
        Parallel.ForEach(Partitioner.Create(buckets, EnumerablePartitionerOptions.NoBuffering), c =>Array.Sort(substrings, start[c], start[c + 1] - start[c], default(SubstringOrder)));
        Parallel.ForEach(Partitioner.Create(0, substrings.Length), range =>
        {
            for (var i = range.Item1; i < range.Item2; i++)
                substrings[i] = substrings[i] with { Repeat = RepeatAt(substrings[i].Item, substrings[i].Offset) };
        });
        return substrings;
    }

    // Base plus the delta, without re-sorting: see MergeSorted.
    // ByPrefix [GB19, US12, US99] + Added [US55] + Removed [US12]:
    //   copy [GB19], put US55, skip US12, copy [US99] -> [GB19, US55, US99]
    // BySuffix the same way in its own order; Substrings drops US12's 4 entries and puts US55's 4 at their places.
    private static State Merge(State state)
    {
        var (b, added, removed) = (state.Base, state.Added, state.Removed);
        var next = new Snapshot(
            MergeSorted<string, PrefixOrder>(b.ByPrefix, removed.ByPrefix, added.ByPrefix).ToArray(),
            MergeSorted<string, SuffixOrder>(b.BySuffix, removed.BySuffix, added.BySuffix).ToArray(),
            MergeSorted<Substring, SubstringOrder>(b.Substrings, [.. removed.ByPrefix.SelectMany(SubstringsOf)], [.. added.ByPrefix.SelectMany(SubstringsOf).Order(default(SubstringOrder))]).ToArray());
        return new State(next, Added: Delta.Empty, Removed: Delta.Empty);
    }

    // sorted without `removed`, plus `added`, both of them in TOrder (a total order). Every removed entry is in sorted and no added
    // one is, so each one's place is one binary search, and Merged copies the stretches between those places whole.
    // `removed` may be in any order: sorting its places (ints) is cheaper than sorting it (Merge's removed substrings).
    private static Merged<T> MergeSorted<T, TOrder>(ArraySegment<T> sorted, ReadOnlySpan<T> removed, ReadOnlySpan<T> added) where TOrder : struct, IOrder<T>
    {
        var removedAt = new int[removed.Length];
        for (var i = 0; i < removed.Length; i++) 
            removedAt[i] = sorted.AsSpan().BinarySearch(removed[i], default(TOrder));
        
        Array.Sort(removedAt);
        
        // In order, since added is, so items that go to the same place go there in order.
        var addedAt = new (int At, T Item)[added.Length];
        for (var i = 0; i < added.Length; i++) 
            addedAt[i] = (~sorted.AsSpan().BinarySearch(added[i], default(TOrder)), added[i]);
        
        return new Merged<T>(sorted, removedAt, addedAt);
    }

    private static IEnumerable<Substring> SubstringsOf(string item) => Enumerable.Range(0, item.Length).Select(i => new Substring(item, i, RepeatAt(item, i)));

    // Longest stretch from offset that also starts at an earlier offset of item.
    //   ("US99", 3) -> 1   "9" vs "US99", "S99", "99": common prefixes 0, 0, 1
    //   ("US99", 2) -> 0
    // shortcut: compares against every earlier offset, so a long repetitive item (10k x 'A') builds slowly; fine for ISINs (~170 ms per 1M on one core; Build runs it in parallel).
    private static int RepeatAt(string item, int offset)
    {
        var repeat = 0;
        for (var j = 0; j < offset; j++)
            repeat = Math.Max(repeat, item.AsSpan(j).CommonPrefixLength(item.AsSpan(offset)));
        return repeat;
    }

    // The block with the delta applied. The delta is kept in the same order (see Delta), so its part that matches is a block too.
    // No removed item matches and no added one matches: return the block itself, uncopied.
    // Added [US55], Removed [US12]:
    //   StartsWith("US"): block [US12, US99], removed [US12], added [US55] -> skip US12, put US55 -> [US55, US99]
    //   StartsWith("GB"): block [GB19], nothing in the delta starts with "GB" -> the block itself
    // Merged holds only positions until something reads it, so a caller that only needs Count never copies the block.
    private static IReadOnlyList<string> WithDelta<TOrder>(string[] sorted, string[] removed, string[] added, string query) where TOrder : struct, IOrder<string>
    {
        var block = FindBlock<string, TOrder>(sorted, query);
        var (removedBlock, addedBlock) = (FindBlock<string, TOrder>(removed, query), FindBlock<string, TOrder>(added, query));
        return removedBlock.Count == 0 && addedBlock.Count == 0 ? block : MergeSorted<string, TOrder>(block, removedBlock, addedBlock);
    }

    // The entries where TOrder.CompareToQuery returns 0. It is < 0 before that block and > 0 after it,
    // so Start = first entry with compare >= 0, End = first entry with compare > 0.
    // ByPrefix [GB19, US12, US99] with PrefixOrder, "US": [< 0, 0, 0] -> [1, 3)
    // A miss is an empty block:                         "FR": [> 0, > 0, > 0] -> [0, 0)
    private static ArraySegment<T> FindBlock<T, TOrder>(T[] sorted, string query) where TOrder : IOrder<T>
    {
        // A Boundary never equals an entry, so BinarySearch always misses and returns ~(index where the boundary would go).
        var start = ~sorted.AsSpan().BinarySearch(new Boundary<T, TOrder>(query, PastBlock: false));
        var end = start + ~sorted.AsSpan(start).BinarySearch(new Boundary<T, TOrder>(query, PastBlock: true));
        return new ArraySegment<T>(sorted, start, end - start);
    }

    // Sorts after the entries before the block (PastBlock: after the block's own entries too) and before all others.
    private readonly record struct Boundary<T, TOrder>(string Query, bool PastBlock) : IComparable<T> where TOrder : IOrder<T>
    {
        public int CompareTo(T? entry)
        {
            var c = TOrder.CompareToQuery(entry!, Query);
            return c < 0 || (PastBlock && c == 0) ? 1 : -1;
        }
    }

    // An order the arrays are sorted in (Compare), and its queries' blocks: CompareToQuery is 0 for the entries that match,
    // < 0 before them and > 0 after them. Structs, so the generic methods above get a copy each with the comparison inlined.
    private interface IOrder<in T> : IComparer<T>
    {
        static abstract int CompareToQuery(T entry, string query);
    }

    private readonly struct PrefixOrder : IOrder<string>
    {
        public int Compare(string? x, string? y) => string.CompareOrdinal(x, y);

        // 0 if item starts with prefix, else ordinal order of item's first prefix.Length chars against prefix.
        //   ("US12", "US")  -> 0
        //   ("GB19", "US")  -> < 0   'G' < 'U'
        //   ("US12", "USA") -> < 0   '1' < 'A'
        //   ("U",    "US")  -> < 0   item ran out first, so it sorts before (tests have 1-char items)
        public static int CompareToQuery(string item, string prefix) => string.CompareOrdinal(item, 0, prefix, 0, prefix.Length);
    }

    private readonly struct SuffixOrder : IOrder<string>
    {
        public int Compare(string? strA, string? strB)
        {
            if (ReferenceEquals(strA, strB))
            {
                return 0;
            }

            // They can't both be null at this point.
            if (strA == null)
            {
                return -1;
            }
            if (strB == null)
            {
                return 1;
            }

            // i = distance from the end: the last chars first, so the difference nearest the end decides.
            for (var i = 0; i < Math.Min(strA.Length, strB.Length); i++)
            {
                if(strA[strA.Length - 1 - i] != strB[strB.Length - 1 - i]) return strA[strA.Length - 1 - i] - strB[strB.Length - 1 - i];
            }

            return strA.Length - strB.Length;
        }

        // PrefixOrder.CompareToQuery walking both strings from their last char; the same walk as Compare, stopped after suffix.Length chars.
        //   ("GB19", "9")   -> 0
        //   ("US12", "9")   -> < 0   '2' < '9'
        //   ("US12", "B12") -> > 0   '2' = '2', '1' = '1', then 'S' > 'B'
        //   ("12",   "312") -> < 0   item ran out first
        public static int CompareToQuery(string item, string suffix)
        {
            for (var i = 0; i < suffix.Length; i++)
            {
                if (i == item.Length) return -1;
                var c = item[item.Length - 1 - i] - suffix[suffix.Length - 1 - i];
                if (c != 0) return c;
            }
            return 0;
        }
    }

    private readonly struct SubstringOrder : IOrder<Substring>
    {
        // Ties ("9" of GB19 and of US99) broken by item, so no two entries are equal and Merge's binary search finds the exact one.
        public int Compare(Substring x, Substring y)
        {
            var c = x.Item.AsSpan(x.Offset).SequenceCompareTo(y.Item.AsSpan(y.Offset));
            return c != 0 ? c : string.CompareOrdinal(x.Item, y.Item);
        }

        // PrefixOrder.CompareToQuery on the item's text starting at entry.Offset.
        //   ((US99, 2), "9") -> 0     "99" starts with "9"
        //   ((US12, 1), "9") -> > 0   "S12": 'S' > '9'
        public static int CompareToQuery(Substring entry, string fragment) =>
            string.CompareOrdinal(entry.Item, entry.Offset, fragment, 0, fragment.Length);
    }

    private sealed record Snapshot(string[] ByPrefix, string[] BySuffix, Substring[] Substrings);

    // State(Base: GB19, US12, US99, Added: [US55], Removed: [US12]) -> searches see GB19, US55, US99.
    private sealed record State(Snapshot Base, Delta Added, Delta Removed);

    // Pending items, sorted like Base's ByPrefix and BySuffix, so a query finds its part of them with FindBlock too.
    // They stay small (< MergeThreshold), so writers copy them instead of changing them.
    private sealed record Delta(string[] ByPrefix, string[] BySuffix)
    {
        public static readonly Delta Empty = new([], []);

        public int Count => ByPrefix.Length;

        public bool Contains(string item) => ByPrefix.AsSpan().BinarySearch(item, default(PrefixOrder)) >= 0;

        public Delta With(string item) => new(Insert<PrefixOrder>(ByPrefix, item), Insert<SuffixOrder>(BySuffix, item));

        public Delta Without(string item) => new(Remove<PrefixOrder>(ByPrefix, item), Remove<SuffixOrder>(BySuffix, item));

        private static string[] Insert<TOrder>(string[] sorted, string item) where TOrder : struct, IOrder<string>
        {
            var at = ~sorted.AsSpan().BinarySearch(item, default(TOrder));
            return [.. sorted.AsSpan(0, at), item, .. sorted.AsSpan(at)];
        }

        private static string[] Remove<TOrder>(string[] sorted, string item) where TOrder : struct, IOrder<string>
        {
            var at = sorted.AsSpan().BinarySearch(item, default(TOrder));
            return [.. sorted.AsSpan(0, at), .. sorted.AsSpan(at + 1)];
        }
    }

    // sorted without the entries at removedAt, plus each addedAt item put before sorted[At]; both in order.
    // Only the positions until something reads it (indexer, enumeration), which copies the stretches between them once.
    private sealed class Merged<T>(ArraySegment<T> sorted, int[] removedAt, (int At, T Item)[] addedAt) : IReadOnlyList<T>
    {
        // Racing readers may both copy; either result is the same.
        private T[]? _array;

        public int Count => sorted.Count - removedAt.Length + addedAt.Length;

        public T this[int index] => ToArray()[index];

        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)ToArray()).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public T[] ToArray() => _array ??= Copy();

        private T[] Copy()
        {
            var result = new T[Count];
            int from = 0, to = 0, r = 0, a = 0;
            while (r < removedAt.Length || a < addedAt.Length)
            {
                // An added entry at the place of a removed one sorts before it, so goes first.
                var insert = a < addedAt.Length && (r == removedAt.Length || addedAt[a].At <= removedAt[r]);
                var at = insert ? addedAt[a].At : removedAt[r];
                sorted.AsSpan(from, at - from).CopyTo(result.AsSpan(to));
                to += at - from;
                from = at;
                if (insert) result[to++] = addedAt[a++].Item;
                else { from++; r++; }
            }
            sorted.AsSpan(from).CopyTo(result.AsSpan(to));
            return result;
        }
    }

    // Item[Offset..] is this entry's sort key (16 bytes: reference + two ints).
    // Repeat: the longest stretch from Offset that also starts at an earlier offset of Item (RepeatAt);
    // a hit is a duplicate when fragment.Length <= Repeat, since the fragment then also starts earlier.
    //   (US99, 3) "9":  "9" also starts at offset 2 -> Repeat = 1, so Filter("9") skips it
    //   (US99, 2) "99": no earlier offset starts with "9" -> Repeat = 0, never skipped
    private readonly record struct Substring(string Item, int Offset, int Repeat);
}
