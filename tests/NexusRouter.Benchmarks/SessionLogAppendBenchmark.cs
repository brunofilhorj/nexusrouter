using System.Diagnostics;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Mathematics;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

using Microsoft.Extensions.Logging.Abstractions;

using NexusRouter.Domain.Enums;
using NexusRouter.Domain.Models;
using NexusRouter.SessionLog;

namespace NexusRouter.Benchmarks;

/// <summary>
/// Measures <see cref="SessionLogStream.Append"/> at the NFR-PERF-01 load shape: 300-byte frames.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(AppendConfig))]
public class SessionLogAppendBenchmark
{
    private readonly byte[] _frame = new byte[300];
    private SessionLogManager _manager = null!;
    private SessionLogStream _stream = null!;
    private long _seq = 1;
    private string _directory = "";
    private readonly DispatchPayload _payload = new();

    /// <summary>
    /// Opens a touched segment before the measured loop.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "nexusrouter-bench-" + Guid.NewGuid().ToString("N"));
        _manager = new SessionLogManager(
            new SessionLogOptions
            {
                Directory = _directory,
                SegmentSizeMB = 256,
                IndexSlotsPerSegment = 1_000_000,
                FlushIntervalMs = 60_000,
                UncleanRestartSeqMargin = 0,
                FlushMode = SessionLogFlushMode.PerMessage,
            },
            NullLogger<SessionLogManager>.Instance);
        _manager.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        _stream = (SessionLogStream)_manager.OpenStream("BENCH");
        const int samples = 200_000;
        var ticks = new long[samples];
        for (int i = 0; i < samples; i++)
        {
            long start = Stopwatch.GetTimestamp();
            if (!_stream.Append(_frame, _seq++, 1).IsOk)
            {
                throw new InvalidOperationException("Append failed during the latency sample.");
            }

            ticks[i] = Stopwatch.GetTimestamp() - start;
        }

        Array.Sort(ticks);
        Console.WriteLine(
            "per_op_us p50={0:0.###} p99={1:0.###} p99.9={2:0.###} sync_rotations={3}",
            ToMicroseconds(ticks[(int)(samples * 0.50)]),
            ToMicroseconds(ticks[(int)(samples * 0.99)]),
            ToMicroseconds(ticks[(int)(samples * 0.999)]),
            _stream.SyncRotationCount);
    }

    private static double ToMicroseconds(long ticks) => ticks * 1_000_000d / Stopwatch.Frequency;

    /// <summary>
    /// Appends one 300-byte frame and stores the resulting <see cref="LogRef"/> on the payload.
    /// </summary>
    /// <returns>The published reference.</returns>
    [Benchmark]
    public LogRef Append()
    {
        AppendResult result = _stream.Append(_frame, _seq++, 1);
        if (!result.IsOk)
        {
            throw new InvalidOperationException("Append failed.");
        }

        _payload.LogRef = result.Ref;
        return _payload.LogRef;
    }

    /// <summary>
    /// Stops the log and prints how many rotations ran on the writer thread.
    /// </summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        Console.WriteLine("sync_rotations=" + _stream.SyncRotationCount);
        _manager.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class PercentileColumn : IColumn
    {
        private readonly string _name;
        private readonly double _percentile;

        public PercentileColumn(string name, double percentile)
        {
            _name = name;
            _percentile = percentile;
        }

        public string Id => _name;

        public string ColumnName => _name;

        public bool AlwaysShow => true;

        public ColumnCategory Category => ColumnCategory.Statistics;

        public int PriorityInCategory => 0;

        public bool IsNumeric => true;

        public UnitType UnitType => UnitType.Time;

        public string Legend => _name;

        public string GetValue(Summary summary, BenchmarkCase benchmarkCase) => Format(summary, benchmarkCase);

        public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style) => Format(summary, benchmarkCase);

        public bool IsAvailable(Summary summary) => true;

        public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

        private string Format(Summary summary, BenchmarkCase benchmarkCase)
        {
            BenchmarkReport? report = summary[benchmarkCase];
            Statistics? stats = report?.ResultStatistics;
            double value = stats is null ? double.NaN : Percentile(stats.OriginalValues, _percentile);
            return double.IsNaN(value) ? "?" : (value / 1000d).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " us";
        }

        private static double Percentile(IReadOnlyList<double> values, double percentile)
        {
            if (values.Count == 0)
            {
                return double.NaN;
            }

            double[] sorted = new double[values.Count];
            for (int i = 0; i < values.Count; i++)
            {
                sorted[i] = values[i];
            }

            Array.Sort(sorted);
            double rank = (percentile / 100d) * (sorted.Length - 1);
            int low = (int)Math.Floor(rank);
            int high = (int)Math.Ceiling(rank);
            if (low == high)
            {
                return sorted[low];
            }

            return sorted[low] + ((sorted[high] - sorted[low]) * (rank - low));
        }
    }

    private sealed class AppendConfig : ManualConfig
    {
        public AppendConfig()
        {
            AddColumn(StatisticColumn.P50);
            AddColumn(StatisticColumn.P95);
            AddColumn(new PercentileColumn("P99", 99));
            AddColumn(new PercentileColumn("P99.9", 99.9));
        }
    }
}
