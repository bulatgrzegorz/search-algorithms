namespace SearchAlgorithms.Tests;

public class SortedArrayIndexTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndexedResultsRemainConsistentAcrossWritesAndConcurrentMaterialization(bool suffix)
    {
        var items = Enumerable.Range(0, 320).Select(i => $"M{i:D4}M").ToArray();
        await using var index = new SortedArrayIndex(items);
        var expected = items.ToHashSet(StringComparer.Ordinal);
        foreach (var item in items.Where((_, i) => i % 17 == 0))
        {
            index.Remove(item);
            expected.Remove(item);
        }
        foreach (var item in new[] { "M0000AM", "M0000BM", "M0100AM", "M9999M" })
        {
            index.Add(item);
            expected.Add(item);
        }

        var ordered = expected.OrderBy(
            x => suffix ? new string(x.Reverse().ToArray()) : x, StringComparer.Ordinal).ToArray();
        var hits = suffix ? index.GetByEndsWith("M") : index.GetByStartsWith("M");
        Assert.Equal(ordered.Length, hits.Count);
        // Exercise direct indexing before enumeration or a large index materializes the result.
        for (var i = 0; i < 64; i++) Assert.Equal(ordered[i], hits[i]);
        Assert.Throws<ArgumentOutOfRangeException>(() => hits[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => hits[hits.Count]);

        // Keep this result while writes publish new snapshots and cross merge thresholds.
        var writer = Task.Run(() =>
        {
            for (var i = 0; i < 600; i++)
            {
                var item = $"Z{i:D4}Z";
                index.Add(item);
                Assert.Single(index.GetByStartsWith(item));
                index.Remove(item);
                Assert.Empty(index.GetByStartsWith(item));
                index.Add(item);
            }
        });
        Parallel.For(0, 32, reader =>
        {
            // Racing large-index reads initialize the cache; other readers enumerate it.
            if (reader % 2 == 0)
                for (var i = hits.Count - 1; i >= 0; i--) Assert.Equal(ordered[i], hits[i]);
            else
                Assert.Equal(ordered, hits.ToArray());
        });
        await writer;
        Assert.Equal(ordered, hits.ToArray());
    }
}
