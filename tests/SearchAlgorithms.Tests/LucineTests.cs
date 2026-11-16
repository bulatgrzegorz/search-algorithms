namespace SearchAlgorithms.Tests;

// Small, readable cases for LuceneIndex, one rule of ISearchAlgorithm each. AlgorithmTests covers the broad randomized check.
public class LucineTests
{
    [Fact]
    public async Task EndsWithAfterAddsAndRemoves1()
    {
        await using var luc1 = new RocksDbIndex(Items("US99"));
        luc1.Filter("S9");
        
        await using var luc = new RocksDbIndex(Items("ada", "jon", "jan", "kurawa"));

        luc.Add(E("george"));
        luc.Add(E("ada"));
        luc.Remove(E("jan"));
        luc.Add(E("sran"));

        AssertItems(luc.Filter("ra"), "sran");
    }
    
    [Fact]
    public async Task EndsWithAfterAddsAndRemoves()
    {
        await using var luc = new LuceneIndex(Items("ada", "jon", "jan"));

        luc.Add(E("george"));
        luc.Add(E("ada"));
        luc.Remove(E("jan"));
        luc.Add(E("sran"));

        AssertItems(luc.GetByEndsWith("an"), "sran");
    }
    
    [Fact]
    public async Task StartsWithAfterAddsAndRemoves()
    {
        await using var luc = new LuceneIndex(Items("ada", "jon", "jan"));

        luc.Add(E("george"));
        luc.Add(E("ada"));
        luc.Remove(E("jan"));
        luc.Add(E("SRAN"));

        AssertItems(luc.GetByStartsWith("SR"), "SRAN");
    }

    [Fact]
    public async Task IsCaseSensitive()
    {
        await using var luc = new LuceneIndex(Items("AB01", "ab02"));

        AssertItems(luc.GetByStartsWith("AB"), "AB01");
        AssertItems(luc.GetByStartsWith("ab"), "ab02");
        AssertItems(luc.GetByEndsWith("B01"), "AB01");
        AssertItems(luc.Filter("b0"), "ab02");
    }

    [Fact]
    public async Task FindsStopWordsAndSingleLetters()
    {
        await using var luc = new LuceneIndex(Items("A", "AT", "the", "IT"));

        AssertItems(luc.GetByStartsWith("A"), "A", "AT");
        AssertItems(luc.GetByEndsWith("he"), "the");
        AssertItems(luc.Filter("T"), "AT", "IT");
    }

    [Fact]
    public async Task EmptyPrefixOrSuffixFindsEverythingAndWholeValueMatchesItself()
    {
        await using var luc = new LuceneIndex(Items("AB01", "CD02", ""));
        luc.Remove(E("CD02"));

        AssertItems(luc.GetByStartsWith(""), "AB01", "");
        AssertItems(luc.GetByEndsWith(""), "AB01", "");
        AssertItems(luc.GetByStartsWith("AB01"), "AB01");
        AssertItems(luc.GetByEndsWith("AB01"), "AB01");
        Assert.Empty(luc.GetByStartsWith("AB012"));
    }

    [Fact]
    public async Task VerifiesPrefixesAndSuffixesLongerThanIndexedEdges()
    {
        var common = new string('A', 100); // longer than the indexed edge length
        await using var luc = new LuceneIndex(Items(common + "1", common + "2", "1" + common, "2" + common));

        AssertItems(luc.GetByStartsWith(common + "1"), common + "1");
        AssertItems(luc.GetByEndsWith("1" + common), "1" + common);
    }

    [Fact]
    public async Task TreatsWholeValueAsOneTerm()
    {
        await using var luc = new LuceneIndex(Items("AB-01 X", "AB"));

        AssertItems(luc.GetByStartsWith("AB-"), "AB-01 X");
        AssertItems(luc.GetByEndsWith("1 X"), "AB-01 X");
        AssertItems(luc.Filter("-01 "), "AB-01 X");
    }

    [Fact]
    public async Task TreatsWildcardCharactersInQueryLiterally()
    {
        await using var luc = new LuceneIndex(Items("A?B", "AxB", "A*B"));

        AssertItems(luc.GetByStartsWith("A?"), "A?B");
        AssertItems(luc.GetByEndsWith("*B"), "A*B");
        AssertItems(luc.Filter("?"), "A?B");
    }

    [Fact]
    public async Task NaiveIndexTreatsWildcardCharactersInQueryLiterally()
    {
        await using var luc = new NaiveLuceneIndex(Items(@"A?B", "AxB", "A*B", @"A\B"));

        AssertItems(luc.GetByStartsWith("A?"), "A?B");
        AssertItems(luc.GetByEndsWith("*B"), "A*B");
        AssertItems(luc.Filter(@"\"), @"A\B");
    }

    [Fact]
    public async Task AddedItemIsFoundRightAway()
    {
        await using var luc = new LuceneIndex(Items("AB01"));

        luc.Add(E("CD01"));

        AssertItems(luc.GetByStartsWith("CD"), "CD01");
    }

    [Fact]
    public async Task AddingExistingItemDoesNothing()
    {
        await using var luc = new LuceneIndex(Items("AB01"));

        luc.Add(E("AB01"));

        AssertItems(luc.GetByStartsWith("AB"), "AB01");
    }

    [Fact]
    public async Task RemovedItemIsGone()
    {
        await using var luc = new LuceneIndex(Items("AB01", "AB02"));

        luc.Remove(E("AB01"));
        luc.Remove(E("ZZ99")); // missing: does nothing

        AssertItems(luc.GetByStartsWith("AB"), "AB02");
    }

    [Fact]
    public async Task RemovedAndReaddedItemIsBack()
    {
        await using var luc = new LuceneIndex(Items("AB01"));

        luc.Remove(E("AB01"));
        luc.Add(E("AB01"));

        AssertItems(luc.Filter("B0"), "AB01");
    }

    [Fact]
    public async Task SearchesStayRightAsSegmentsChange()
    {
        await using var luc = new LuceneIndex(Items("AB00"));
        List<string> expected = ["AB00"];

        // Each search reopens onto the round's new segment: earlier segments' cached id tables get reused
        // (some now with deleted docs), and 50 one-doc segments are enough for Lucene to start merging them.
        for (var i = 1; i <= 50; i++)
        {
            AssertItems(luc.GetByStartsWith("AB"), [.. expected]);
            luc.Add(E($"AB{i:D2}"));
            expected.Add($"AB{i:D2}");
            if (i % 3 == 0) { luc.Remove(E(expected[0])); expected.RemoveAt(0); }
        }

        AssertItems(luc.GetByStartsWith("AB"), [.. expected]);
    }

    [Fact]
    public async Task EmptyIndexFindsNothing()
    {
        await using var luc = new LuceneIndex(Items());

        Assert.Empty(luc.GetByStartsWith("A"));
        Assert.Empty(luc.GetByEndsWith("A"));
        Assert.Empty(luc.Filter("A"));
    }

    // xUnit runs test classes in parallel, and benchmarks build several indexes in one process.
    [Fact]
    public async Task InstancesAliveAtTheSameTimeDoNotShareData()
    {
        await using var first = new LuceneIndex(Items("AB01"));
        await using var second = new LuceneIndex(Items("CD01"));

        AssertItems(first.Filter("01"), "AB01");
        AssertItems(second.Filter("01"), "CD01");
    }

    [Fact]
    public async Task NewInstanceDoesNotSeeDisposedInstanceData()
    {
        await using (var first = new LuceneIndex(Items("AB01")))
            first.Add(E("AB02"));

        await using var second = new LuceneIndex(Items("CD01"));

        Assert.Empty(second.GetByStartsWith("AB"));
        AssertItems(second.GetByStartsWith("CD"), "CD01");
    }

    // Ids from the Key, so cases read as Keys; hash codes vary in sign and size, so every Id byte gets round-tripped.
    static Entry E(string key) => new(key, key.GetHashCode());

    static Entry[] Items(params string[] keys) => [.. keys.Select(k => E(k))];

    static void AssertItems(IReadOnlyList<Entry> actual, params string[] expected) =>
        Assert.Equal(Items(expected).OrderBy(x => x.Key, StringComparer.Ordinal), actual.OrderBy(x => x.Key, StringComparer.Ordinal));
}
