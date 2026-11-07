namespace SearchAlgorithms.Tests;

// Every registered algorithm must return exactly what a plain LINQ scan returns (order ignored).
public class AlgorithmTests
{
    static readonly string[] Items = GenerateItems(1);
    static readonly string[] Others = GenerateItems(2).Except(Items).ToArray();

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

    static void AssertMatches(IEnumerable<string> items, ISearchAlgorithm algorithm)
    {
        foreach (var q in Queries())
        {
            Assert.Equal(items.Where(x => x.StartsWith(q, StringComparison.Ordinal)).Order(), algorithm.GetByStartsWith(q).Order());
            Assert.Equal(items.Where(x => x.EndsWith(q, StringComparison.Ordinal)).Order(), algorithm.GetByEndsWith(q).Order());
            Assert.Equal(items.Where(x => x.Contains(q, StringComparison.Ordinal)).Order(), algorithm.Filter(q).Order());
        }
    }

    // Small alphabet and mixed lengths so queries overlap a lot and hit edge cases (whole item, 1 char, misses).
    static string[] GenerateItems(int seed)
    {
        var rnd = new Random(seed);
        return Enumerable.Range(0, 2000)
            .Select(_ => new string(Enumerable.Range(0, rnd.Next(1, 13)).Select(_ => "AB01"[rnd.Next(4)]).ToArray()))
            .Distinct()
            .ToArray();
    }

    static IEnumerable<string> Queries()
    {
        var rnd = new Random(2);
        for (var i = 0; i < 200; i++)
        {
            var item = Items[rnd.Next(Items.Length)];
            var start = rnd.Next(item.Length);
            yield return item[start..rnd.Next(start + 1, item.Length + 1)];
        }
        yield return "ZZZ";
        yield return "AB01AB01AB01AB01";
    }
}
