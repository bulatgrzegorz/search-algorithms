namespace SearchAlgorithms.Tests;

// Every registered algorithm must return exactly what a plain LINQ scan returns (order ignored).
public class AlgorithmTests
{
    static readonly Entry[] Items = GenerateItems(1, firstId: 0);
    // Other Ids, so where a Key repeats one of Items', adding it is another entity and removing it must keep Items' one.
    static readonly Entry[] Others = GenerateItems(2, firstId: 10_000);

    public static TheoryData<string> Names => new(Algorithms.All.Keys);

    [Theory, MemberData(nameof(Names))]
    public async Task MatchesLinq(string name)
    {
        await using var algorithm = Algorithms.All[name](Items);
        AssertMatches(Items, algorithm);
    }

    [Theory, MemberData(nameof(Names))]
    public async Task MatchesLinqAfterAddsAndRemoves(string name)
    {
        await using var algorithm = Algorithms.All[name](Items);
        var expected = Items.ToHashSet();
        var candidates = Items.Concat(Others).ToArray(); // so some adds are duplicates and some removes miss
        var rnd = new Random(3);
        for (var i = 0; i < 1000; i++)
        {
            var item = candidates[rnd.Next(candidates.Length)];
            if (rnd.Next(2) == 0) { algorithm.Add(item); expected.Add(item); }
            else { algorithm.Remove(item); expected.Remove(item); }
        }
        AssertMatches(expected, algorithm);
    }

    // Ids at the int edges, so every byte of an encoded Id round-trips.
    [Theory, MemberData(nameof(Names))]
    public async Task EntriesSharingAKeyStaySeparate(string name)
    {
        await using var algorithm = Algorithms.All[name]([new("AB01", -1), new("AB01", int.MaxValue), new("XAB0", int.MinValue)]);

        algorithm.Remove(new("AB01", 5)); // missing: same Key, other Id
        Assert.Equal(2, algorithm.GetByStartsWith("AB").Count);

        algorithm.Remove(new("AB01", -1));
        Assert.Equal(new[] { new Entry("AB01", int.MaxValue) }, algorithm.GetByStartsWith("AB"));
        Assert.Equal(new[] { new Entry("AB01", int.MaxValue) }, algorithm.GetByEndsWith("01"));
        Assert.Equal(new[] { new Entry("XAB0", int.MinValue), new Entry("AB01", int.MaxValue) }, Sorted(algorithm.Filter("AB0")));
    }

    [Theory, MemberData(nameof(Names))]
    public async Task SurvivesSearchesDuringWrites(string name)
    {
        await using var algorithm = Algorithms.All[name](Items);
        using var stop = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                foreach (var x in Others) algorithm.Add(x);
                foreach (var x in Others) algorithm.Remove(x);
            }
        });
        // Bounded by reader work, not writes, so a lock-heavy implementation can't make this slow.
        Parallel.For(0, 1000, i =>
        {
            var q = new[] { "A", "0", "AB" }[i % 3];
            algorithm.GetByStartsWith(q);
            algorithm.GetByEndsWith(q);
            algorithm.Filter(q);
        });
        stop.Cancel();
        await writer;
        AssertMatches(Items, algorithm); // every writer round ends with Others removed again
    }

    static void AssertMatches(IEnumerable<Entry> items, ISearchAlgorithm algorithm)
    {
        foreach (var q in Queries())
        {
            Assert.Equal(Sorted(items.Where(x => x.Key.StartsWith(q, StringComparison.Ordinal))), Sorted(algorithm.GetByStartsWith(q)));
            Assert.Equal(Sorted(items.Where(x => x.Key.EndsWith(q, StringComparison.Ordinal))), Sorted(algorithm.GetByEndsWith(q)));
            Assert.Equal(Sorted(items.Where(x => x.Key.Contains(q, StringComparison.Ordinal))), Sorted(algorithm.Filter(q)));
            AssertReadable(() => algorithm.Filter(q));
        }
    }

    // A result may find its entries only as they are read (SortedArrayIndex's Filter): reading by index, front to back
    // or back to front, must give what enumerating gives.
    static void AssertReadable(Func<IReadOnlyList<Entry>> search)
    {
        var expected = search().ToList();
        var forward = search();
        Assert.Equal(expected, Enumerable.Range(0, forward.Count).Select(i => forward[i]));
        var backward = search();
        Assert.Equal(Enumerable.Reverse(expected), Enumerable.Range(0, backward.Count).Reverse().Select(i => backward[i]));
    }

    static IEnumerable<Entry> Sorted(IEnumerable<Entry> entries) => entries.OrderBy(x => x.Id).ThenBy(x => x.Key, StringComparer.Ordinal);

    // Small alphabet and mixed lengths so queries overlap a lot and hit edge cases (whole item, 1 char, misses).
    // Keys repeat (there are only 4 + 16 + 64 of up to 3 chars), each with its own Id.
    static Entry[] GenerateItems(int seed, int firstId)
    {
        var rnd = new Random(seed);
        return Enumerable.Range(firstId, 2000)
            .Select(id => new Entry(new string(Enumerable.Range(0, rnd.Next(1, 13)).Select(_ => "AB01"[rnd.Next(4)]).ToArray()), id))
            .ToArray();
    }

    static IEnumerable<string> Queries()
    {
        var rnd = new Random(2);
        for (var i = 0; i < 200; i++)
        {
            var item = Items[rnd.Next(Items.Length)].Key;
            var start = rnd.Next(item.Length);
            yield return item[start..rnd.Next(start + 1, item.Length + 1)];
        }
        yield return "ZZZ";
        yield return "AB01AB01AB01AB01";
    }
}
