using System.Diagnostics;

namespace SearchAlgorithms.Benchmarks;

// What an index costs to hold and to start: build time, each query kind's first (cold, JIT included) search,
// then the managed heap it keeps, the process's RSS (mapped file pages it touched count too) and its files on disk.
// Each algorithm runs in its own process in an empty working directory, so one's leftovers can't count toward the next.
// Args: [item count], default 1,000,000; past the file's end loads all of it. ALGORITHM and DATASET as for the benchmarks.
// HEAP_LIMIT_MB=<n> runs each under that GC heap limit, like a container's; an index that doesn't fit fails.
internal static class FootprintRun
{
    private const string ChildFlag = "FOOTPRINT_CHILD";

    public static void Run(string[] args)
    {
        var count = args.Length > 0 ? int.Parse(args[0]) : (int)Scale.Huge;
        if (Environment.GetEnvironmentVariable(ChildFlag) is null) RunEach(count);
        else Measure(Selected.AlgorithmNames.Single(), count);
    }

    private static void RunEach(int count)
    {
        var limit = Environment.GetEnvironmentVariable("HEAP_LIMIT_MB");
        Console.WriteLine($"Dataset {Dataset.Name}, up to {count:N0} items, heap limit {limit ?? "none"} MB. First queries in μs.");
        Console.WriteLine();
        Console.WriteLine("| Algorithm | Items | Build ms | First StartsWith | First EndsWith | First Filter | Heap MB | RSS MB | Disk MB |");
        Console.WriteLine("|-----------|------:|---------:|-----------------:|---------------:|-------------:|--------:|-------:|--------:|");
        foreach (var name in Selected.AlgorithmNames)
        {
            var dir = Directory.CreateTempSubdirectory("footprint-");
            // Started via `dotnet run`, ProcessPath is the app itself; its row goes straight to this console.
            var start = new ProcessStartInfo(Environment.ProcessPath!, ["footprint", count.ToString()]) { WorkingDirectory = dir.FullName };
            start.Environment["ALGORITHM"] = name;
            start.Environment[ChildFlag] = "1";
            // Only the child: dotnet run's own build would run under it too if the script set it.
            if (limit is not null) start.Environment["DOTNET_GCHeapHardLimit"] = $"0x{long.Parse(limit) << 20:x}";
            using var child = Process.Start(start)!;
            child.WaitForExit();
            if (child.ExitCode != 0) Console.WriteLine($"| {name} | failed: exit code {child.ExitCode} | | | | | | | |");
            dir.Delete(recursive: true);
        }
    }

    private static void Measure(string name, int count)
    {
        var baseline = GC.GetTotalMemory(forceFullCollection: true);
        string[]? items = Dataset.Load(count);
        var (prefixes, suffixes, fragments) = Dataset.Queries(items, count: 1);
        var itemCount = items.Length;

        var clock = Stopwatch.StartNew();
        var algorithm = Algorithms.All[name](items);
        var build = clock.Elapsed;
        // Nulled, not just unused: unoptimized (tier 0) code keeps locals alive. What the index keeps of the items still counts.
        items = null;

        string First(Func<string, IReadOnlyList<string>> search, string query)
        {
            clock.Restart();
            _ = search(query).Count;
            return clock.Elapsed.TotalMicroseconds.ToString("N0");
        }
        var firsts = $"{First(algorithm.GetByStartsWith, prefixes[0])} | {First(algorithm.GetByEndsWith, suffixes[0])} | {First(algorithm.Filter, fragments[0])}";

        var heap = GC.GetTotalMemory(forceFullCollection: true) - baseline;
        var disk = new DirectoryInfo(".").EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        Console.WriteLine($"| {name} | {itemCount:N0} | {build.TotalMilliseconds:N0} | {firsts} | {heap >> 20:N0} | {Environment.WorkingSet >> 20:N0} | {disk >> 20:N0} |");
        GC.KeepAlive(algorithm);
    }
}
