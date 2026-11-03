using System.Diagnostics;

using NexusRouter.Audit;
using NexusRouter.Audit.Sinks;
using NexusRouter.Domain.Models;

using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace NexusRouter.Audit.Tests;

public sealed class SerilogAuditSinkTests
{
    [Fact]
    public async Task WriteBatchAsync_WritesMetadataAndOmitsRawFix()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nexus-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "audit.log");
        var sink = new SerilogAuditSink(new SerilogAuditSinkOptions
        {
            FilePath = path,
            RollingInterval = RollingInterval.Infinite,
        });

        try
        {
            await sink.StartAsync(CancellationToken.None);
            AuditRecord[] batch =
            [
                new AuditRecord
                {
                    SessionId = new SessionId("sess-audit-1"),
                    MsgType = "8",
                    WasDropped = true,
                    DropReason = "channel cheio",
                    RawFix = "RAWFIX_SECRET_PAYLOAD"u8.ToArray(),
                },
            ];
            await sink.WriteBatchAsync(batch, CancellationToken.None);
            await sink.StopAsync(CancellationToken.None);

            string written = await File.ReadAllTextAsync(path);
            Assert.Contains("sess-audit-1", written, StringComparison.Ordinal);
            Assert.Contains("channel cheio", written, StringComparison.Ordinal);
            Assert.DoesNotContain("RAWFIX_SECRET_PAYLOAD", written, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task WriteBatchAsync_ConvertsStopwatchTimestampsToMicroseconds()
    {
        var capturingSink = new CapturingSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(capturingSink).CreateLogger();
        var sink = new SerilogAuditSink(logger);
        AuditRecord[] batch =
        [
            new AuditRecord
            {
                IngressTimestampTicks = Stopwatch.Frequency,
                EgressTimestampTicks = Stopwatch.Frequency * 2,
            },
        ];

        await sink.WriteBatchAsync(batch, CancellationToken.None);

        LogEvent logEvent = Assert.IsType<LogEvent>(capturingSink.LogEvent);
        Assert.Equal(1_000_000L, Assert.IsType<long>(Assert.IsType<ScalarValue>(logEvent.Properties["IngressTimestampMicroseconds"]).Value));
        Assert.Equal(2_000_000L, Assert.IsType<long>(Assert.IsType<ScalarValue>(logEvent.Properties["EgressTimestampMicroseconds"]).Value));
    }

    private sealed class CapturingSink : ILogEventSink
    {
        public LogEvent? LogEvent { get; private set; }

        public void Emit(LogEvent logEvent) => LogEvent = logEvent;
    }
}
