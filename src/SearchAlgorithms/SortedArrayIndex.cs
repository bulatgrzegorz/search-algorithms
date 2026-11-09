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
    // Pending changes (Added + Removed) that trigger a merge.
    private const int MergeThreshold = 256;

    private readonly Lock _writeLock = new();
    // Writers only, under _writeLock: makes Add of a present item and Remove of a missing one no-ops.
    private readonly HashSet<string> _items;
    // Searches read it once (Volatile.Read) and use only that; writers publish a whole new one (Volatile.Write),
    // so a search that starts after a write returned sees it.
    private State _state;

    public SortedArrayIndex(string[] items)
    {
        _items = new HashSet<string>(items, StringComparer.Ordinal);
        _state = new State(Build(_items), Added: [], Removed: []);
    }

    // StartsWith("US"): block (1, 3) -> new ArraySegment<string>(ByPrefix, 1, 2) = US12, US99.
    public IReadOnlyList<string> GetByStartsWith(string prefix)
    {
        var state = Volatile.Read(ref _state);
        var sorted = state.Base.ByPrefix;
        var (start, end) = FindBlock(sorted, x => ComparePrefix(x, prefix));
        return WithDelta(new ArraySegment<string>(sorted, start, end - start), state, x => x.StartsWith(prefix, StringComparison.Ordinal), StringComparer.Ordinal);
    }

    // EndsWith("9"): BySuffix [US12, GB19, US99], block (1, 3) -> GB19, US99.
    public IReadOnlyList<string> GetByEndsWith(string suffix)
    {
        var state = Volatile.Read(ref _state);
        var sorted = state.Base.BySuffix;
        var (start, end) = FindBlock(sorted, x => CompareSuffix(x, suffix));
        return WithDelta(new ArraySegment<string>(sorted, start, end - start), state, x => x.EndsWith(suffix, StringComparison.Ordinal), BackwardsComparer.Instance);
    }

    // An item matching at several offsets is in the block several times: keep only its first one,
    // the entry whose Repeat (see Substring) is shorter than fragment.
    // Filter("9"): block = (GB19, 3, Repeat 0), (US99, 3, Repeat 1), (US99, 2, Repeat 0).
    //   1 > 0 -> keep (GB19, 3)
    //   1 > 1 is false -> skip (US99, 3): "9" also starts at offset 2
    //   1 > 0 -> keep (US99, 2)
    //   -> [GB19, US99]
    public IReadOnlyList<string> Filter(string fragment)
    {
        // Every item contains "", also "" itself, which has no Substrings entry.
        if (fragment.Length == 0) return GetByStartsWith(fragment);

        var state = Volatile.Read(ref _state);
        var substrings = state.Base.Substrings;
        var (start, end) = FindBlock(substrings, x => CompareSubstring(x, fragment));
        var hits = new List<string>(end - start);
        for (var i = start; i < end; i++)
        {
            var (item, _, repeat) = substrings[i];
            if (fragment.Length > repeat) hits.Add(item);
        }
        return WithDelta(hits, state, x => x.Contains(fragment, StringComparison.Ordinal));
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
            var i = Array.IndexOf(state.Removed, item);
            Publish(i >= 0 ? state with { Removed = Without(state.Removed, i) } : state with { Added = [.. state.Added, item] });
        }
    }

    public void Remove(string item)
    {
        lock (_writeLock)
        {
            if (!_items.Remove(item)) return;
            var state = _state;
            var i = Array.IndexOf(state.Added, item);
            Publish(i >= 0 ? state with { Added = Without(state.Added, i) } : state with { Removed = [.. state.Removed, item] });
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // Under _writeLock.
    private void Publish(State state) =>
        Volatile.Write(ref _state, state.Added.Length + state.Removed.Length >= MergeThreshold ? Merge(state) : state);

    private static string[] Without(string[] array, int index) => [.. array.AsSpan(0, index), .. array.AsSpan(index + 1)];

    private static Snapshot Build(IReadOnlyCollection<string> items)
    {
        string[] prefixArray = [..items];
        Array.Sort(prefixArray, StringComparer.Ordinal);

        string[] suffixArray = [..items];
        Array.Sort(suffixArray, BackwardsComparer.Instance);

        var substrings = new Substring[items.Select(x => x.Length).Sum()];

        var s = 0;
        foreach (var item in items)
        {
            for (var i = 0; i < item.Length; i++)
            {
                substrings[s] = new Substring(item, i, RepeatAt(item, i));

                s++;
            }
        }

        Array.Sort(substrings, SubstringsComparer.Instance);
        return new Snapshot(prefixArray, suffixArray, substrings);
    }

    // Base plus the delta, without re-sorting: see MergeSorted.
    // ByPrefix [GB19, US12, US99] + Added [US55] + Removed [US12]:
    //   copy [GB19], put US55, skip US12, copy [US99] -> [GB19, US55, US99]
    // BySuffix the same way in its own order; Substrings drops US12's 4 entries and puts US55's 4 at their places.
    private static State Merge(State state)
    {
        var (b, added, removed) = (state.Base, state.Added, state.Removed);
        var next = new Snapshot(
            MergeSorted(b.ByPrefix, removed, added, StringComparer.Ordinal),
            MergeSorted(b.BySuffix, removed, added, BackwardsComparer.Instance),
            MergeSorted(b.Substrings, removed.SelectMany(SubstringsOf), added.SelectMany(SubstringsOf), SubstringsComparer.Instance));
        return new State(next, Added: [], Removed: []);
    }

    // sorted without `removed`, plus `added`. Every removed entry is in sorted and no added one is, so each one's place
    // is one binary search (comparer must be a total order), and the stretches between those places are copied whole.
    private static T[] MergeSorted<T>(ArraySegment<T> sorted, IEnumerable<T> removed, IEnumerable<T> added, IComparer<T> comparer)
    {
        var removedAt = removed.Select(x => sorted.AsSpan().BinarySearch(x, comparer)).Order().ToArray();
        // Sorted, so items that go to the same place go there in order.
        var addedAt = added.Order(comparer).Select(x => (At: ~sorted.AsSpan().BinarySearch(x, comparer), Item: x)).ToArray();

        var result = new T[sorted.Count - removedAt.Length + addedAt.Length];
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

    private static IEnumerable<Substring> SubstringsOf(string item) => Enumerable.Range(0, item.Length).Select(i => new Substring(item, i, RepeatAt(item, i)));

    // Longest stretch from offset that also starts at an earlier offset of item.
    //   ("US99", 3) -> 1   "9" vs "US99", "S99", "99": common prefixes 0, 0, 1
    //   ("US99", 2) -> 0
    // shortcut: compares against every earlier offset, so a long repetitive item (10k x 'A') builds slowly; fine for ISINs (~170 ms per 1M).
    private static int RepeatAt(string item, int offset)
    {
        var repeat = 0;
        for (var j = 0; j < offset; j++) 
            repeat = Math.Max(repeat, item.AsSpan(j).CommonPrefixLength(item.AsSpan(offset)));
        return repeat;
    }

    // The block (sorted by comparer) with the delta applied; `matches` is the query's own check (StartsWith / EndsWith).
    // No removed item matches and no added one matches: return the block itself, uncopied.
    // Otherwise MergeSorted: the matching removed items are in the block, so each is one binary search, not a hash lookup per hit.
    // Added [US55], Removed [US12]:
    //   StartsWith("US"): block [US12, US99] -> skip US12, put US55 -> [US55, US99]
    //   StartsWith("GB"): block [GB19], nothing in the delta starts with "GB" -> the block itself
    private static IReadOnlyList<string> WithDelta(ArraySegment<string> block, State state, Func<string, bool> matches, IComparer<string> comparer) =>
        !state.Removed.Any(matches) && !state.Added.Any(matches)
            ? block
            : MergeSorted(block, state.Removed.Where(matches), state.Added.Where(matches), comparer);

    // Filter's hits with the delta applied; `matches` is Contains(fragment).
    // No removed item matches and no added one matches: return baseHits unchanged.
    // Otherwise copy baseHits without the matching removed items, plus the matching added ones.
    // Added [US55], Removed [US12]:
    //   Filter("1"): baseHits [US12, GB19] -> US12 removed -> new list [GB19]
    //   Filter("9"): baseHits [GB19, US99], nothing in the delta contains "9" -> baseHits itself
    private static IReadOnlyList<string> WithDelta(IReadOnlyList<string> baseHits, State state, Func<string, bool> matches)
    {
        var anyRemoved = state.Removed.Any(matches);
        if (!anyRemoved && !state.Added.Any(matches)) return baseHits;

        var hits = new List<string>(baseHits.Count + state.Added.Length);
        if (anyRemoved)
        {
            // shortcut: one hash lookup per hit, so a big block costs a full pass while removes are pending; binary-search their places if that shows up.
            var removed = state.Removed.Where(matches).ToHashSet(StringComparer.Ordinal);
            hits.AddRange(baseHits.Where(x => !removed.Contains(x)));
        }
        else hits.AddRange(baseHits);

        hits.AddRange(state.Added.Where(matches));
        return hits;
    }

    // [Start, End) of the entries where compare returns 0. compare is < 0 before that block and > 0 after it,
    // so Start = first entry with compare >= 0, End = first entry with compare > 0.
    // ByPrefix [GB19, US12, US99] with ComparePrefix(_, "US"): [< 0, 0, 0] -> (1, 3)
    // A miss is an empty block:  ComparePrefix(_, "FR"): [> 0, > 0, > 0] -> (0, 0)
    private static (int Start, int End) FindBlock<T>(T[] sorted, Func<T, int> compare)
    {
        // A Boundary never equals an entry, so BinarySearch always misses and returns ~(index where the boundary would go).
        var start = ~sorted.AsSpan().BinarySearch(new Boundary<T>(compare, PastBlock: false));
        var end = start + ~sorted.AsSpan(start).BinarySearch(new Boundary<T>(compare, PastBlock: true));
        return (start, end);
    }

    // Sorts after the entries before the block (PastBlock: after the block's own entries too) and before all others.
    private readonly record struct Boundary<T>(Func<T, int> Compare, bool PastBlock) : IComparable<T>
    {
        public int CompareTo(T? entry)
        {
            var c = Compare(entry!);
            return c < 0 || (PastBlock && c == 0) ? 1 : -1;
        }
    }

    // 0 if item starts with prefix, else ordinal order of item's first prefix.Length chars against prefix.
    //   ("US12", "US")  -> 0
    //   ("GB19", "US")  -> < 0   'G' < 'U'
    //   ("US12", "USA") -> < 0   '1' < 'A'
    //   ("U",    "US")  -> < 0   item ran out first, so it sorts before (tests have 1-char items)
    private static int ComparePrefix(string item, string prefix) => string.CompareOrdinal(item, 0, prefix, 0, prefix.Length);

    // ComparePrefix walking both strings from their last char; the same walk as BackwardsComparer, stopped after suffix.Length chars.
    //   ("GB19", "9")   -> 0
    //   ("US12", "9")   -> < 0   '2' < '9'
    //   ("US12", "B12") -> > 0   '2' = '2', '1' = '1', then 'S' > 'B'
    //   ("12",   "312") -> < 0   item ran out first
    private static int CompareSuffix(string item, string suffix)
    {
        for (var i = 0; i < suffix.Length; i++)
        {
            if (i == item.Length) return -1;
            var c = item[item.Length - 1 - i] - suffix[suffix.Length - 1 - i];
            if (c != 0) return c;
        }
        return 0;
    }

    // ComparePrefix on the item's text starting at entry.Offset.
    //   ((US99, 2), "9") -> 0     "99" starts with "9"
    //   ((US12, 1), "9") -> > 0   "S12": 'S' > '9'
    private static int CompareSubstring(Substring entry, string fragment) =>
        string.CompareOrdinal(entry.Item, entry.Offset, fragment, 0, fragment.Length);

    private sealed record Snapshot(string[] ByPrefix, string[] BySuffix, Substring[] Substrings);

    // Delta arrays stay small (< MergeThreshold), so writers copy them instead of changing them.
    // State(Base: GB19, US12, US99, Added: [US55], Removed: [US12]) -> searches see GB19, US55, US99.
    private sealed record State(Snapshot Base, string[] Added, string[] Removed);

    // Item[Offset..] is this entry's sort key (16 bytes: reference + two ints).
    // Repeat: the longest stretch from Offset that also starts at an earlier offset of Item (RepeatAt);
    // a hit is a duplicate when fragment.Length <= Repeat, since the fragment then also starts earlier.
    //   (US99, 3) "9":  "9" also starts at offset 2 -> Repeat = 1, so Filter("9") skips it
    //   (US99, 2) "99": no earlier offset starts with "9" -> Repeat = 0, never skipped
    private readonly record struct Substring(string Item, int Offset, int Repeat);

    private class BackwardsComparer : StringComparer
    {
        internal static readonly BackwardsComparer Instance = new();

        public override int Compare(string? strA, string? strB)
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

        public override bool Equals(string? strA, string? strB) => string.Equals(strA, strB, StringComparison.Ordinal);

        public override int GetHashCode(string obj)
        {
            return obj.GetHashCode();
        }
    }

    private class SubstringsComparer : IComparer<Substring>
    {
        internal static readonly SubstringsComparer Instance = new();

        // Ties ("9" of GB19 and of US99) broken by item, so no two entries are equal and Merge's binary search finds the exact one.
        public int Compare(Substring x, Substring y)
        {
            var c = x.Item.AsSpan(x.Offset).SequenceCompareTo(y.Item.AsSpan(y.Offset));
            return c != 0 ? c : string.CompareOrdinal(x.Item, y.Item);
        }
    }
}
