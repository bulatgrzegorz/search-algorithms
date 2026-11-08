using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using System.Text.Json;
using SearchAlgorithms;

// CI reads this list to start one job per algorithm.
if (args is ["--algorithms"]) { Console.WriteLine(JsonSerializer.Serialize(Algorithms.All.Keys)); return; }

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args,
    DefaultConfig.Instance.AddDiagnoser(MemoryDiagnoser.Default).AddColumn(new RequestsPerSecondColumn()));

sealed class RequestsPerSecondColumn : IColumn
{
    public string Id => nameof(RequestsPerSecondColumn);
    public string ColumnName => "Req/s";
    public bool AlwaysShow => true;
    public ColumnCategory Category => ColumnCategory.Statistics;
    public int PriorityInCategory => 100;
    public bool IsNumeric => true;
    public UnitType UnitType => UnitType.Dimensionless;
    public string Legend => "Requests completed per second (Requests / Mean)";
    public bool IsAvailable(Summary summary) => true;
    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;
    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style) => GetValue(summary, benchmarkCase);

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase) =>
        benchmarkCase.Parameters.Items.FirstOrDefault(p => p.Name == "Requests")?.Value is int requests
        && summary[benchmarkCase]?.ResultStatistics is { } stats
            ? (requests * 1e9 / stats.Mean).ToString("N0")
            : "-";
}
