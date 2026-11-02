using NexusRouter.Audit;
using NexusRouter.Audit.Sinks;
using NexusRouter.Domain.Models;

using Serilog;

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
}
