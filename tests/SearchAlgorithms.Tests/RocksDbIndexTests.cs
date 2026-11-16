namespace SearchAlgorithms.Tests;

// Small, readable cases for RocksDbIndex, mostly what AlgorithmTests does not reach: its items are 1-12 chars of "AB01" and its queries never empty.
public class RocksDbIndexTests
{
    [Fact]
    public async Task StartsAndEndsWithAfterAddsAndRemoves()
    {
        await using var index = new RocksDbIndex(Items("ada", "jon", "jan"));

        index.Add(E("george"));
        index.Add(E("ada"));
        index.Remove(E("jan"));
        index.Add(E("sran"));

        AssertItems(index.GetByStartsWith("j"), "jon");
        AssertItems(index.GetByEndsWith("an"), "sran");
    }

    [Fact]
    public async Task IsCaseSensitive()
    {
        await using var index = new RocksDbIndex(Items("AB01", "ab02"));

        AssertItems(index.GetByStartsWith("AB"), "AB01");
        AssertItems(index.GetByEndsWith("B01"), "AB01");
        AssertItems(index.Filter("b0"), "ab02");
    }

    [Fact]
    public async Task ItemWithRepeatedFragmentComesBackOnce()
    {
        await using var index = new RocksDbIndex(Items("US99", "GB19", "AAAA"));

        AssertItems(index.Filter("9"), "US99", "GB19");
        AssertItems(index.Filter("AA"), "AAAA");
    }

    // ABAB's rotations sit in Repeat buckets 0, 0, 2, 1; Remove must find them there.
    [Fact]
    public async Task MultiCharRepeatsComeBackOnceAndRemoveFully()
    {
        await using var index = new RocksDbIndex(Items("ABAB", "XABX"));

        AssertItems(index.Filter("AB"), "ABAB", "XABX");
        AssertItems(index.Filter("BA"), "ABAB");
        AssertItems(index.Filter("ABA"), "ABAB");
        AssertItems(index.Filter("ABAB"), "ABAB");

        index.Remove(E("ABAB"));
        AssertItems(index.Filter("B"), "XABX");
        Assert.Empty(index.Filter("BA"));
    }

    // Filter("AC") reads two Repeat buckets: ACXX's match is in bucket 0, ABAC's in bucket 1. The writer never lets both exist
    // at once, so a search that sees both read its buckets at different points in time.
    [Fact]
    public async Task FilterSeesOnePointInTime()
    {
        await using var index = new RocksDbIndex(Items("ACXX"));
        using var stop = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                index.Remove(E("ACXX"));
                index.Add(E("ABAC"));
                index.Remove(E("ABAC"));
                index.Add(E("ACXX"));
            }
        });

        try
        {
            Parallel.For(0, 20_000, _ => Assert.True(index.Filter("AC").Count <= 1));
        }
        finally
        {
            stop.Cancel();
            await writer;
        }
    }

    // Results are decoded on read: by index, by enumeration, and past the end.
    [Fact]
    public async Task ResultsReadByIndexMatchEnumeration()
    {
        await using var index = new RocksDbIndex(Items("", "A", "AB01", "Zażółć"));

        foreach (var hits in new[] { index.GetByStartsWith(""), index.GetByEndsWith(""), index.Filter("") })
        {
            Assert.Equal(4, hits.Count);
            Assert.Equal(hits, Enumerable.Range(0, hits.Count).Select(i => hits[i]));
            Assert.Throws<ArgumentOutOfRangeException>(() => hits[hits.Count]);
            Assert.Throws<ArgumentOutOfRangeException>(() => hits[-1]);
        }
    }

    [Fact]
    public async Task AddedItemIsFoundRightAway()
    {
        await using var index = new RocksDbIndex(Items("AB01"));

        index.Add(E("CD01"));

        AssertItems(index.GetByStartsWith("CD"), "CD01");
        AssertItems(index.Filter("D0"), "CD01");
    }

    [Fact]
    public async Task AddingExistingItemDoesNothing()
    {
        await using var index = new RocksDbIndex(Items("AB01"));

        index.Add(E("AB01"));

        AssertItems(index.Filter("B"), "AB01");
    }

    [Fact]
    public async Task RemovedItemIsGoneAndReaddedItemIsBack()
    {
        await using var index = new RocksDbIndex(Items("AB01", "AB02"));

        index.Remove(E("AB01"));
        index.Remove(E("ZZ99")); // missing: does nothing
        AssertItems(index.Filter("AB"), "AB02");

        index.Add(E("AB01"));
        AssertItems(index.Filter("B0"), "AB01", "AB02");
    }

    [Fact]
    public async Task EmptyIndexFindsNothing()
    {
        await using var index = new RocksDbIndex(Items());

        Assert.Empty(index.GetByStartsWith("A"));
        Assert.Empty(index.GetByEndsWith("A"));
        Assert.Empty(index.Filter("A"));
        Assert.Empty(index.Filter(""));
    }

    [Fact]
    public async Task EmptyQueryFindsEverythingIncludingEmptyItem()
    {
        await using var index = new RocksDbIndex(Items("AB01", "CD02", ""));
        index.Remove(E("CD02"));

        AssertItems(index.GetByStartsWith(""), "AB01", "");
        AssertItems(index.GetByEndsWith(""), "AB01", "");
        AssertItems(index.Filter(""), "AB01", "");
        AssertItems(index.GetByStartsWith("AB01"), "AB01");
        Assert.Empty(index.GetByStartsWith("AB012"));
    }

    [Fact]
    public async Task MatchesNonAsciiItemsByChars()
    {
        string[] items = ["Zażółć", "zółw", "a😀b", "😀", "x�"];
        await using var index = new RocksDbIndex(Items(items));

        foreach (var q in new[] { "ż", "ół", "ć", "😀", "😀b", "a😀", "�", "Z", "w" })
        {
            AssertItems(index.GetByStartsWith(q), items.Where(x => x.StartsWith(q, StringComparison.Ordinal)).ToArray());
            AssertItems(index.GetByEndsWith(q), items.Where(x => x.EndsWith(q, StringComparison.Ordinal)).ToArray());
            AssertItems(index.Filter(q), items.Where(x => x.Contains(q, StringComparison.Ordinal)).ToArray());
        }
    }

    [Fact]
    public async Task NaiveIndexHandlesEmptyQueriesAndNonAscii()
    {
        await using var index = new NaiveRocksDbIndex(Items("Zażółć", "zółw", ""));

        AssertItems(index.GetByStartsWith(""), "Zażółć", "zółw", "");
        AssertItems(index.GetByStartsWith("Za"), "Zażółć");
        AssertItems(index.GetByEndsWith("ć"), "Zażółć");
        AssertItems(index.Filter("ół"), "Zażółć", "zółw");
    }

    [Fact]
    public async Task InstancesAliveAtTheSameTimeDoNotShareData()
    {
        await using var first = new RocksDbIndex(Items("AB01"));
        await using var second = new RocksDbIndex(Items("CD01"));

        AssertItems(first.Filter("01"), "AB01");
        AssertItems(second.Filter("01"), "CD01");
    }

    [Fact]
    public async Task NewInstanceDoesNotSeeDisposedInstanceData()
    {
        await using (var first = new RocksDbIndex(Items("AB01")))
            first.Add(E("AB02"));

        await using var second = new RocksDbIndex(Items("CD01"));

        Assert.Empty(second.GetByStartsWith("AB"));
        AssertItems(second.GetByStartsWith("CD"), "CD01");
    }

    // Ids from the Key, so cases read as Keys; hash codes vary in sign and size, so every Id byte gets round-tripped.
    static Entry E(string key) => new(key, key.GetHashCode());

    static Entry[] Items(params string[] keys) => [.. keys.Select(k => E(k))];

    static void AssertItems(IReadOnlyList<Entry> actual, params string[] expected) =>
        Assert.Equal(Items(expected).OrderBy(x => x.Key, StringComparer.Ordinal), actual.OrderBy(x => x.Key, StringComparer.Ordinal));
}
