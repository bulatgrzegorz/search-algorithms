namespace SearchAlgorithms.Tests;

// Small, readable cases for RocksDbIndex, mostly what AlgorithmTests does not reach: its items are 1-12 chars of "AB01" and its queries never empty.
public class RocksDbIndexTests
{
    [Fact]
    public async Task StartsAndEndsWithAfterAddsAndRemoves()
    {
        await using var index = new RocksDbIndex(["ada", "jon", "jan"]);

        index.Add("george");
        index.Add("ada");
        index.Remove("jan");
        index.Add("sran");

        AssertItems(index.GetByStartsWith("j"), "jon");
        AssertItems(index.GetByEndsWith("an"), "sran");
    }

    [Fact]
    public async Task IsCaseSensitive()
    {
        await using var index = new RocksDbIndex(["AB01", "ab02"]);

        AssertItems(index.GetByStartsWith("AB"), "AB01");
        AssertItems(index.GetByEndsWith("B01"), "AB01");
        AssertItems(index.Filter("b0"), "ab02");
    }

    [Fact]
    public async Task ItemWithRepeatedFragmentComesBackOnce()
    {
        await using var index = new RocksDbIndex(["US99", "GB19", "AAAA"]);

        AssertItems(index.Filter("9"), "US99", "GB19");
        AssertItems(index.Filter("AA"), "AAAA");
    }

    // Results are decoded on read: by index, by enumeration, and past the end.
    [Fact]
    public async Task ResultsReadByIndexMatchEnumeration()
    {
        await using var index = new RocksDbIndex(["", "A", "AB01", "Zażółć"]);

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
        await using var index = new RocksDbIndex(["AB01"]);

        index.Add("CD01");

        AssertItems(index.GetByStartsWith("CD"), "CD01");
        AssertItems(index.Filter("D0"), "CD01");
    }

    [Fact]
    public async Task AddingExistingItemDoesNothing()
    {
        await using var index = new RocksDbIndex(["AB01"]);

        index.Add("AB01");

        AssertItems(index.Filter("B"), "AB01");
    }

    [Fact]
    public async Task RemovedItemIsGoneAndReaddedItemIsBack()
    {
        await using var index = new RocksDbIndex(["AB01", "AB02"]);

        index.Remove("AB01");
        index.Remove("ZZ99"); // missing: does nothing
        AssertItems(index.Filter("AB"), "AB02");

        index.Add("AB01");
        AssertItems(index.Filter("B0"), "AB01", "AB02");
    }

    [Fact]
    public async Task EmptyIndexFindsNothing()
    {
        await using var index = new RocksDbIndex([]);

        Assert.Empty(index.GetByStartsWith("A"));
        Assert.Empty(index.GetByEndsWith("A"));
        Assert.Empty(index.Filter("A"));
        Assert.Empty(index.Filter(""));
    }

    [Fact]
    public async Task EmptyQueryFindsEverythingIncludingEmptyItem()
    {
        await using var index = new RocksDbIndex(["AB01", "CD02", ""]);
        index.Remove("CD02");

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
        await using var index = new RocksDbIndex(items);

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
        await using var index = new NaiveRocksDbIndex(["Zażółć", "zółw", ""]);

        AssertItems(index.GetByStartsWith(""), "Zażółć", "zółw", "");
        AssertItems(index.GetByStartsWith("Za"), "Zażółć");
        AssertItems(index.GetByEndsWith("ć"), "Zażółć");
        AssertItems(index.Filter("ół"), "Zażółć", "zółw");
    }

    [Fact]
    public async Task InstancesAliveAtTheSameTimeDoNotShareData()
    {
        await using var first = new RocksDbIndex(["AB01"]);
        await using var second = new RocksDbIndex(["CD01"]);

        AssertItems(first.Filter("01"), "AB01");
        AssertItems(second.Filter("01"), "CD01");
    }

    [Fact]
    public async Task NewInstanceDoesNotSeeDisposedInstanceData()
    {
        await using (var first = new RocksDbIndex(["AB01"]))
            first.Add("AB02");

        await using var second = new RocksDbIndex(["CD01"]);

        Assert.Empty(second.GetByStartsWith("AB"));
        AssertItems(second.GetByStartsWith("CD"), "CD01");
    }

    static void AssertItems(IReadOnlyList<string> actual, params string[] expected) =>
        Assert.Equal(expected.Order(), actual.Order());
}
