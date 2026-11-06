namespace SearchAlgorithms.Benchmarks;

// Item counts. data/isin.txt holds ~9.4M ISINs; Huge can go up to that minus the few thousand kept unused for Add.
public enum Scale { Small = 10_000, Medium = 100_000, Huge = 1_000_000 }

public static class Dataset
{
    // First `count` of a seeded shuffle, so Small ⊂ Medium ⊂ Huge and items past a size are real ISINs not yet in the set.
    public static string[] Load(int count)
    {
        var items = File.ReadAllLines(Find("isin.txt"));
        new Random(42).Shuffle(items); // source is grouped by issuer; shuffle so each size is a fair sample
        return items[..count];
    }

    // Cut from random items so queries hit; fixed seed so every algorithm gets the same queries.
    public static (string[] Prefixes, string[] Suffixes, string[] Fragments) Queries(string[] items, int count = 1000)
    {
        var rnd = new Random(7);
        var (prefixes, suffixes, fragments) = (new string[count], new string[count], new string[count]);
        for (var i = 0; i < count; i++)
        {
            var item = items[rnd.Next(items.Length)];
            var len = Math.Min(rnd.Next(3, 7), item.Length);
            prefixes[i] = item[..len];
            suffixes[i] = item[^len..];
            fragments[i] = item.Substring(rnd.Next(item.Length - len + 1), len);
        }
        return (prefixes, suffixes, fragments);
    }

    // BenchmarkDotNet runs from a generated bin folder, so walk up to the repo's data/.
    static string Find(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "data", name);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException($"data/{name} not found, run data/download.sh first.");
    }
}
