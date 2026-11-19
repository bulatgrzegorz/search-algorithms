using System.Collections;
using System.Collections.Concurrent;

namespace SearchAlgorithms;

// Every query is "the block of a sorted array whose entries start with the query", found by two binary searches.
// Published arrays are never changed (callers may still hold slices of them): writes collect in a small delta,
// and a merge folds it into new arrays.
//
// Example used in the comments below, items GB19, US12, US99 (their Keys; entries with one Key sort by Id):
//   ByPrefix   = [GB19, US12, US99]   StartsWith("US") -> block [1, 3) = US12, US99
//   BySuffix   = [US12, GB19, US99]   EndsWith("9")    -> block [1, 3) = GB19, US99
//   Substrings = 12 entries           Filter("9")      -> block [3, 6) = GB19, US99, US99 -> GB19, US99
//     (US12, 2) "12", (GB19, 2) "19", (US12, 3) "2", (GB19, 3) "9", (US99, 3) "9", (US99, 2) "99", (GB19, 1) "B19", ...
//     (an entry names its item by its index in ByPrefix: (US12, 2) is (1, 2))
public sealed class SortedArrayIndex : ISearchAlgorithm
{
    // Pending changes (Added + Removed) that trigger a merge. A merge copies every item (128 B per ISIN), so the threshold grows
    // with them, 1 per 128 items, and each write's share of a merge stays ~16 KB (Huge: 3,906 pending instead of 256 cut it from 814 KB).
    // Swept n/64 to n/1024: n/128 had the fastest updates (vs n/256: ISIN 1M -9%, names 1M -26%, ISIN 100k -29%); merging more
    // often cost more than the smaller deltas saved, n/64's deltas were too big to copy per write. Searches under writes pay for the
    // bigger delta: StartsWith at 100k 1.65 -> 2.82 us.
    private int MergeThreshold => Math.Max(256, _items.Count / 128);

    private readonly Lock _writeLock = new();
    // Writers only, under _writeLock: makes Add of a present item and Remove of a missing one no-ops.
    private readonly HashSet<Entry> _items;
    // Searches read it once (Volatile.Read) and use only that; writers publish a whole new one (Volatile.Write),
    // so a search that starts after a write returned sees it.
    private State _state;

    public SortedArrayIndex(Entry[] items)
    {
        _items = new HashSet<Entry>(items);
        _state = new State(Build(_items), Added: Delta.Empty, Removed: Delta.Empty);
    }

    // StartsWith("US"): block (1, 3) -> new ArraySegment<Entry>(ByPrefix, 1, 2) = US12, US99.
    public IReadOnlyList<Entry> GetByStartsWith(string prefix)
    {
        var state = Volatile.Read(ref _state);
        return WithDelta<PrefixOrder>(state.Base.ByPrefix, state.Removed.ByPrefix, state.Added.ByPrefix, prefix);
    }

    // EndsWith("9"): BySuffix [US12, GB19, US99], block (1, 3) -> GB19, US99.
    public IReadOnlyList<Entry> GetByEndsWith(string suffix)
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
    // The walk itself happens only as far as the hits are read (see Hits).
    public IReadOnlyList<Entry> Filter(string fragment)
    {
        // Every item contains "", also "" itself, which has no Substrings entry.
        if (fragment.Length == 0) return GetByStartsWith(fragment);

        var state = Volatile.Read(ref _state);
        var items = state.Base.ByPrefix;
        var order = new SubstringOrder(items, []);
        var block = FindBlock<Substring, SubstringOrder>(state.Base.Substrings, fragment, order);
        List<int> removedAt = [];
        foreach (var x in state.Removed.ByPrefix)
        {
            var offset = x.Key.IndexOf(fragment, StringComparison.Ordinal);
            // Its entry starts with fragment, so it is in the block.
            if (offset >= 0) removedAt.Add(block.AsSpan().BinarySearch(new Substring(items.AsSpan().BinarySearch(x, default(PrefixOrder)), (ushort)offset, 0), order));
        }
        removedAt.Sort();
        // Added items are never in Base, so they can't repeat a hit.
        List<Entry> added = [];
        foreach (var x in state.Added.ByPrefix)
            if (x.Key.Contains(fragment, StringComparison.Ordinal)) added.Add(x);
        return new Hits(items, block, fragment.Length, removedAt, added);
    }

    // Delta, starting empty (Base = GB19, US12, US99):
    //   Add("FR77")    -> Added = [FR77]
    //   Remove("FR77") -> Added = []        it was only pending, Base never had it
    //   Remove("US12") -> Removed = [US12]
    //   Add("US12")    -> Removed = []      Base still has it
    // So Removed items are always in Base and Added ones never are.
    public void Add(Entry item)
    {
        lock (_writeLock)
        {
            if (!_items.Add(item)) return;
            var state = _state;
            Publish(state.Removed.Contains(item) ? state with { Removed = state.Removed.Without(item) } : state with { Added = state.Added.With(item) });
        }
    }

    public void Remove(Entry item)
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

    // The three sorts are independent, so they run at once. Substrings is built naming items by their index in `all`,
    // then renumbered to their index in ByPrefix once that is sorted (SubstringOrder doesn't depend on the numbering).
    private static Snapshot Build(IReadOnlyCollection<Entry> items)
    {
        Entry[] all = [..items];
        Entry[] prefixArray = [..all];
        Entry[] suffixArray = [..all];
        // Sorted along with prefixArray: ByPrefix[p] is all[from[p]].
        var from = Enumerable.Range(0, all.Length).ToArray();
        Substring[] substrings = [];
        Parallel.Invoke(
            () => Array.Sort(prefixArray, from, default(PrefixOrder)),
            () => Array.Sort(suffixArray, default(SuffixOrder)),
            () => substrings = SortedSubstrings(all));
        var at = new int[all.Length];
        for (var p = 0; p < from.Length; p++) at[from[p]] = p;
        Parallel.ForEach(Partitioner.Create(0, substrings.Length), range =>
        {
            for (var i = range.Item1; i < range.Item2; i++) substrings[i] = substrings[i] with { Item = at[substrings[i].Item] };
        });
        return new Snapshot(prefixArray, suffixArray, substrings);
    }

    // One entry per char of every item, sorted. The entries are first put in buckets by their first char (a counting sort),
    // so the buckets sort in parallel. Repeat is filled in after sorting, in parallel too: SubstringOrder ignores it.
    //   GB19, US12, US99 -> bucket '1': (GB19, 2), (US12, 2)   bucket '9': (GB19, 3), (US99, 2), (US99, 3)   ...
    private static Substring[] SortedSubstrings(Entry[] items)
    {
        // Bucket c is [start[c], start[c + 1]).
        var start = new int[char.MaxValue + 2];

        foreach (var item in items)
            foreach (var c in item.Key)
                start[c + 1]++;

        for (var c = 1; c < start.Length; c++) start[c] += start[c - 1];

        // Uninitialized: the loop below fills every slot.
        var substrings = GC.AllocateUninitializedArray<Substring>(start[^1]);
        var next = (int[])start.Clone();
        for (var n = 0; n < items.Length; n++)
        {
            var key = items[n].Key;
            for (var i = 0; i < key.Length; i++) substrings[next[key[i]]++] = new Substring(n, checked((ushort)i), 0);
        }

        // Biggest first and one at a time (NoBuffering), so a big bucket doesn't start last and leave the other cores idle.
        var buckets = Enumerable.Range(0, char.MaxValue + 1).Where(c => start[c + 1] - start[c] > 1).OrderByDescending(c => start[c + 1] - start[c]);
        var order = new SubstringOrder(items, []);
        Parallel.ForEach(Partitioner.Create(buckets, EnumerablePartitionerOptions.NoBuffering), c =>Array.Sort(substrings, start[c], start[c + 1] - start[c], order));
        Parallel.ForEach(Partitioner.Create(0, substrings.Length), range =>
        {
            for (var i = range.Item1; i < range.Item2; i++)
                substrings[i] = substrings[i] with { Repeat = (ushort)RepeatAt(items[substrings[i].Item].Key, substrings[i].Offset) };
        });
        return substrings;
    }

    // Base plus the delta, without re-sorting: see MergeSorted.
    // ByPrefix [GB19, US12, US99] + Added [US55] + Removed [US12]:
    //   copy [GB19], put US55, skip US12, copy [US99] -> [GB19, US55, US99]
    // BySuffix the same way in its own order; Substrings drops US12's 4 entries and puts US55's 4 at their places.
    // Substrings name items by index: Base's in b.ByPrefix, added ones past its end (see SubstringOrder), and the copy
    // renumbers them all to the new ByPrefix (Merged.NewIndexes): GB19 0 -> 0, US99 2 -> 2, US55 (added, so 3) -> 1.
    private static State Merge(State state)
    {
        var (b, added, removed) = (state.Base, state.Added, state.Removed);
        var byPrefix = MergeSorted<Entry, PrefixOrder>(b.ByPrefix, removed.ByPrefix, added.ByPrefix, default, parallel: true);
        var newIndex = byPrefix.NewIndexes();
        var order = new SubstringOrder(b.ByPrefix, added.ByPrefix);
        var next = new Snapshot(
            byPrefix.ToArray(),
            MergeSorted<Entry, SuffixOrder>(b.BySuffix, removed.BySuffix, added.BySuffix, default, parallel: true).ToArray(),
            MergeSorted<Substring, SubstringOrder>(b.Substrings,
                removed.ByPrefix.SelectMany(x => SubstringsOf(b.ByPrefix.AsSpan().BinarySearch(x, default(PrefixOrder)), x.Key)).ToArray(),
                added.ByPrefix.SelectMany((x, i) => SubstringsOf(b.ByPrefix.Length + i, x.Key)).Order(order).ToArray(),
                order, parallel: true, renumber: x => x with { Item = newIndex[x.Item] }).ToArray());
        return new State(next, Added: Delta.Empty, Removed: Delta.Empty);
    }

    // sorted without `removed`, plus `added`, both of them in TOrder (a total order). Every removed entry is in sorted and no added
    // one is, so each one's place is one binary search, and Merged copies the stretches between those places whole.
    // `removed` may be in any order: sorting its places (ints) is cheaper than sorting it (Merge's removed substrings).
    // parallel: the searches and copies are independent; on Huge Merge's (~47k substring searches over 12M entries, a 96 MB copy)
    // took most of its time on one core.
    // renumber: applied to every entry of the result, as it is copied (Merge's Substrings).
    private static Merged<T> MergeSorted<T, TOrder>(ArraySegment<T> sorted, ArraySegment<T> removed, ArraySegment<T> added, TOrder order,
        bool parallel = false, Func<T, T>? renumber = null) where TOrder : struct, IOrder<T>
    {
        var removedAt = new int[removed.Count];
        // In order, since added is, so items that go to the same place go there in order.
        var addedAt = new (int At, T Item)[added.Count];

        // i < removed.Count: a removed entry's index, else an added one's insertion point.
        void Place(int i)
        {
            if (i < removed.Count) removedAt[i] = sorted.AsSpan().BinarySearch(removed[i], order);
            else addedAt[i - removed.Count] = (~sorted.AsSpan().BinarySearch(added[i - removed.Count], order), added[i - removed.Count]);
        }

        if (parallel) Parallel.For(0, removed.Count + added.Count, Place);
        else for (var i = 0; i < removed.Count + added.Count; i++) Place(i);

        Array.Sort(removedAt);
        return new Merged<T>(sorted, removedAt, addedAt, parallel, renumber);
    }

    private static IEnumerable<Substring> SubstringsOf(int item, string key) =>
        Enumerable.Range(0, key.Length).Select(i => new Substring(item, checked((ushort)i), (ushort)RepeatAt(key, i)));

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
    private static IReadOnlyList<Entry> WithDelta<TOrder>(Entry[] sorted, Entry[] removed, Entry[] added, string query) where TOrder : struct, IOrder<Entry>
    {
        var block = FindBlock<Entry, TOrder>(sorted, query, default);
        var (removedBlock, addedBlock) = (FindBlock<Entry, TOrder>(removed, query, default), FindBlock<Entry, TOrder>(added, query, default));
        return removedBlock.Count == 0 && addedBlock.Count == 0 ? block : MergeSorted<Entry, TOrder>(block, removedBlock, addedBlock, default);
    }

    // The entries where TOrder.CompareToQuery returns 0. It is < 0 before that block and > 0 after it,
    // so Start = first entry with compare >= 0, End = first entry with compare > 0.
    // ByPrefix [GB19, US12, US99] with PrefixOrder, "US": [< 0, 0, 0] -> [1, 3)
    // A miss is an empty block:                     "FR": [> 0, > 0, > 0] -> [0, 0)
    private static ArraySegment<T> FindBlock<T, TOrder>(T[] sorted, string query, TOrder order) where TOrder : IOrder<T>
    {
        // A Boundary never equals an entry, so BinarySearch always misses and returns ~(index where the boundary would go).
        var start = ~sorted.AsSpan().BinarySearch(new Boundary<T, TOrder>(order, query, PastBlock: false));
        var end = start + ~sorted.AsSpan(start).BinarySearch(new Boundary<T, TOrder>(order, query, PastBlock: true));
        return new ArraySegment<T>(sorted, start, end - start);
    }

    // Sorts after the entries before the block (PastBlock: after the block's own entries too) and before all others.
    private readonly record struct Boundary<T, TOrder>(TOrder Order, string Query, bool PastBlock) : IComparable<T> where TOrder : IOrder<T>
    {
        public int CompareTo(T? entry)
        {
            var c = Order.CompareToQuery(entry!, Query);
            return c < 0 || (PastBlock && c == 0) ? 1 : -1;
        }
    }

    // An order the arrays are sorted in (Compare), and its queries' blocks: CompareToQuery is 0 for the entries that match,
    // < 0 before them and > 0 after them. Structs, so the generic methods above get a copy each with the comparison inlined.
    // PrefixOrder and SuffixOrder have no state (default); SubstringOrder carries the items its entries name.
    private interface IOrder<in T> : IComparer<T>
    {
        int CompareToQuery(T entry, string query);
    }

    // Entries with one Key sort by Id, so no two entries are equal and a binary search finds the exact one.
    private readonly struct PrefixOrder : IOrder<Entry>
    {
        public int Compare(Entry x, Entry y)
        {
            var c = string.CompareOrdinal(x.Key, y.Key);
            return c != 0 ? c : x.Id.CompareTo(y.Id);
        }

        // 0 if item starts with prefix, else ordinal order of item's first prefix.Length chars against prefix.
        //   ("US12", "US")  -> 0
        //   ("GB19", "US")  -> < 0   'G' < 'U'
        //   ("US12", "USA") -> < 0   '1' < 'A'
        //   ("U",    "US")  -> < 0   item ran out first, so it sorts before (tests have 1-char items)
        public int CompareToQuery(Entry item, string prefix) => string.CompareOrdinal(item.Key, 0, prefix, 0, prefix.Length);
    }

    private readonly struct SuffixOrder : IOrder<Entry>
    {
        public int Compare(Entry x, Entry y)
        {
            var c = CompareReversed(x.Key, y.Key);
            return c != 0 ? c : x.Id.CompareTo(y.Id);
        }

        private static int CompareReversed(string? strA, string? strB)
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
        public int CompareToQuery(Entry entry, string suffix)
        {
            var item = entry.Key;
            for (var i = 0; i < suffix.Length; i++)
            {
                if (i == item.Length) return -1;
                var c = item[item.Length - 1 - i] - suffix[suffix.Length - 1 - i];
                if (c != 0) return c;
            }
            return 0;
        }
    }

    // An entry's Item is an index into items (a Snapshot's ByPrefix) or, past its end, into added (Merge's added items, not in it yet).
    private readonly struct SubstringOrder(Entry[] items, Entry[] added) : IOrder<Substring>
    {
        private Entry ItemOf(Substring x) => x.Item < items.Length ? items[x.Item] : added[x.Item - items.Length];

        // Ties ("9" of GB19 and of US99) broken by Key, then Id, so no two entries are equal and Merge's binary search finds the exact one.
        // Not by Item: items and added are numbered apart.
        public int Compare(Substring x, Substring y)
        {
            var (a, b) = (ItemOf(x), ItemOf(y));
            var c = a.Key.AsSpan(x.Offset).SequenceCompareTo(b.Key.AsSpan(y.Offset));
            if (c == 0) c = string.CompareOrdinal(a.Key, b.Key);
            return c != 0 ? c : a.Id.CompareTo(b.Id);
        }

        // PrefixOrder.CompareToQuery on the item's text starting at entry.Offset.
        //   ((US99, 2), "9") -> 0     "99" starts with "9"
        //   ((US12, 1), "9") -> > 0   "S12": 'S' > '9'
        public int CompareToQuery(Substring entry, string fragment) =>
            string.CompareOrdinal(ItemOf(entry).Key, entry.Offset, fragment, 0, fragment.Length);
    }

    private sealed record Snapshot(Entry[] ByPrefix, Entry[] BySuffix, Substring[] Substrings);

    // State(Base: GB19, US12, US99, Added: [US55], Removed: [US12]) -> searches see GB19, US55, US99.
    private sealed record State(Snapshot Base, Delta Added, Delta Removed);

    // Pending items, sorted like Base's ByPrefix and BySuffix, so a query finds its part of them with FindBlock too.
    // They stay small (< MergeThreshold), so writers copy them instead of changing them.
    private sealed record Delta(Entry[] ByPrefix, Entry[] BySuffix)
    {
        public static readonly Delta Empty = new([], []);

        public int Count => ByPrefix.Length;

        public bool Contains(Entry item) => ByPrefix.AsSpan().BinarySearch(item, default(PrefixOrder)) >= 0;

        public Delta With(Entry item) => new(Insert<PrefixOrder>(ByPrefix, item), Insert<SuffixOrder>(BySuffix, item));

        public Delta Without(Entry item) => new(Remove<PrefixOrder>(ByPrefix, item), Remove<SuffixOrder>(BySuffix, item));

        private static Entry[] Insert<TOrder>(Entry[] sorted, Entry item) where TOrder : struct, IOrder<Entry>
        {
            var at = ~sorted.AsSpan().BinarySearch(item, default(TOrder));
            return [.. sorted.AsSpan(0, at), item, .. sorted.AsSpan(at)];
        }

        private static Entry[] Remove<TOrder>(Entry[] sorted, Entry item) where TOrder : struct, IOrder<Entry>
        {
            var at = sorted.AsSpan().BinarySearch(item, default(TOrder));
            return [.. sorted.AsSpan(0, at), .. sorted.AsSpan(at + 1)];
        }
    }

    // sorted without the entries at removedAt, plus each addedAt item put before sorted[At]; both in order.
    // Only the positions until something reads it (indexer, enumeration), which copies the stretches between them once
    // (on all cores if parallel), passing each entry through renumber if given.
    private sealed class Merged<T>(ArraySegment<T> sorted, int[] removedAt, (int At, T Item)[] addedAt, bool parallel, Func<T, T>? renumber) : IReadOnlyList<T>
    {
        // Racing readers may both copy; either result is the same.
        private T[]? _array;

        public int Count => sorted.Count - removedAt.Length + addedAt.Length;

        public T this[int index] => ToArray()[index];

        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)ToArray()).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public T[] ToArray() => _array ??= Copy();

        // Where each entry ends up: sorted's at their own index (removed ones -1), then added's.
        //   [GB19, US12, US99] without US12, plus US55 at 2 -> [0, -1, 2, 1]
        public int[] NewIndexes()
        {
            var at = new int[sorted.Count + addedAt.Length];
            int r = 0, a = 0, to = 0;
            for (var from = 0; from <= sorted.Count; from++)
            {
                // Added items at this place go before sorted[from], as in Copy.
                while (a < addedAt.Length && addedAt[a].At == from) at[sorted.Count + a++] = to++;
                if (from == sorted.Count) break;
                if (r < removedAt.Length && removedAt[r] == from) { at[from] = -1; r++; }
                else at[from] = to++;
            }
            return at;
        }

        // The walk only finds the stretches (and puts the added items); copying them is the work, so that part may go parallel.
        private T[] Copy()
        {
            var stretches = new (int From, int To, int Length)[removedAt.Length + addedAt.Length + 1];
            // Allocated last: on Huge, a large allocation after it (stretches, 564 KB) started a background GC during the copy, +12 us per update.
            // Uninitialized: every slot is written below, so zeroing it (96 MB of Substrings on Huge) is wasted; T with references is zeroed anyway.
            var result = GC.AllocateUninitializedArray<T>(Count);
            int from = 0, to = 0, r = 0, a = 0, s = 0;
            while (r < removedAt.Length || a < addedAt.Length)
            {
                // An added entry at the place of a removed one sorts before it, so goes first.
                var insert = a < addedAt.Length && (r == removedAt.Length || addedAt[a].At <= removedAt[r]);
                var at = insert ? addedAt[a].At : removedAt[r];
                stretches[s++] = (from, to, at - from);
                to += at - from;
                from = at;
                if (insert) result[to++] = renumber is null ? addedAt[a++].Item : renumber(addedAt[a++].Item);
                else { from++; r++; }
            }
            stretches[s++] = (from, to, sorted.Count - from);

            void CopyStretch(int i)
            {
                var source = sorted.AsSpan(stretches[i].From, stretches[i].Length);
                var target = result.AsSpan(stretches[i].To, stretches[i].Length);
                if (renumber is null) source.CopyTo(target);
                else for (var k = 0; k < source.Length; k++) target[k] = renumber(source[k]);
            }
            if (parallel) Parallel.For(0, s, CopyStretch);
            else for (var i = 0; i < s; i++) CopyStretch(i);
            return result;
        }
    }

    // Filter's hits: the block's kept entries (except removedAt), then added; found only as far as something reads them.
    // Count scans the block's Repeat values only (no item reads). The first read finds 16 hits; a read past them finds all
    // the rest, as one walk. Typeahead reads Count and the first 10 of up to hundreds of thousands (Huge, "U"): as a list
    // that was ~22 MB per typed query. (Doubling instead, a reader of every hit at Huge was ~1.5x slower than the list.)
    //   Filter("9") with Added [FR99]: Count = 2 kept in the block + 1 added = 3; this[0] finds GB19, US99, FR99.
    private sealed class Hits(Entry[] items, ArraySegment<Substring> block, int length, List<int> removedAt, List<Entry> added) : IReadOnlyList<Entry>
    {
        private readonly Lock _lock = new();
        // Replaced whole, so the indexer reads it without the lock; where the walk stopped (_next, _r) is under the lock.
        private Found _found = new([], 0);
        private int _next, _r;
        private int _count = -1;

        // removedAt entries are kept ones (each is its item's first match), so they come off the kept count.
        public int Count => _count >= 0 ? _count : _count = CountKept() - removedAt.Count + added.Count;

        public Entry this[int index]
        {
            get
            {
                var found = Volatile.Read(ref _found);
                if ((uint)index >= (uint)found.Count)
                {
                    ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)Count, nameof(index));
                    found = FindUpTo(index);
                }
                return found.Hits[index];
            }
        }

        public IEnumerator<Entry> GetEnumerator()
        {
            for (var i = 0; i < Count; i++) yield return this[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private int CountKept()
        {
            var kept = 0;
            foreach (var x in block.AsSpan())
                if (length > x.Repeat) kept++;
            return kept;
        }

        // Every Found is full (Count == Hits.Length), so a bigger one is a new array and readers of the old one are unaffected.
        private Found FindUpTo(int index)
        {
            lock (_lock)
            {
                var found = _found;
                if (index < found.Count) return found;
                var hits = found.Hits;
                Array.Resize(ref hits, index < 16 ? Math.Min(Count, 16) : Count);
                var n = found.Count;
                var span = block.AsSpan();
                for (; n < hits.Length && _next < span.Length; _next++)
                {
                    var (item, _, repeat) = span[_next];
                    if (_r < removedAt.Count && removedAt[_r] == _next) _r++;
                    else if (length > repeat) hits[n++] = items[item];
                }
                // Past the block's last kept entry, the rest are added ones.
                for (var inBlock = Count - added.Count; n < hits.Length; n++) hits[n] = added[n - inBlock];
                Volatile.Write(ref _found, found = new Found(hits, n));
                return found;
            }
        }

        private sealed record Found(Entry[] Hits, int Count);
    }

    // Item's Key[Offset..] is this entry's sort key; Item is the item's index in the same Snapshot's ByPrefix.
    // 8 bytes and no reference, so the GC never scans the array: as (string Key, int Id, ...) every merge's fresh copy
    // (192 MB on Huge) had all its references scanned by the next GC, a ~26 ms pause after each merge.
    // shortcut: ushort Offset limits keys to 65,535 chars (Build throws past it); widen to int (12 bytes per entry) if longer keys appear.
    // Repeat: the longest stretch from Offset that also starts at an earlier offset of Key (RepeatAt);
    // a hit is a duplicate when fragment.Length <= Repeat, since the fragment then also starts earlier.
    //   (US99, 3) "9":  "9" also starts at offset 2 -> Repeat = 1, so Filter("9") skips it
    //   (US99, 2) "99": no earlier offset starts with "9" -> Repeat = 0, never skipped
    private readonly record struct Substring(int Item, ushort Offset, ushort Repeat);
}
