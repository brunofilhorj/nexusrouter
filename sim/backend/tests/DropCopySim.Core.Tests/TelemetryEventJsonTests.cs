using System.Text.Json;
using DropCopySim.Core.Generation;
using DropCopySim.Core.Telemetry;

namespace DropCopySim.Core.Tests;

public class TelemetryEventJsonTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void RoundTrips_PolymorphicTelemetryEvents()
    {
        TelemetryEvent[] events =
        [
            new SessionStateChanged
            {
                TimestampUtc = DateTimeOffset.Parse("2026-11-04T12:00:00Z"),
                State = "Active",
                SenderCompId = "ENTRYPOINT-FIRM-0001",
                TargetCompId = "CLIENT1",
                MsgSeqNum = 42,
                LastHeartbeatUtc = DateTimeOffset.Parse("2026-11-04T12:00:00Z"),
            },
            new SimulationStateChanged
            {
                TimestampUtc = DateTimeOffset.Parse("2026-11-04T12:00:01Z"),
                State = SimulationState.Running,
                Reason = "start",
            },
            new CountersSnapshot
            {
                TimestampUtc = DateTimeOffset.Parse("2026-11-04T12:00:02Z"),
                TotalSent = 100,
                RatePerSec = 10.5,
                ByExecType = new Dictionary<string, long> { ["F"] = 70, ["1"] = 20 },
                Errors = 0,
                ElapsedMs = 5000,
            },
            new MessageSample
            {
                TimestampUtc = DateTimeOffset.Parse("2026-11-04T12:00:03Z"),
                MsgType = "8",
                ClOrdId = "CLO-1",
                OrderId = "ORD-1",
                ExecType = "F",
                OrdStatus = "2",
                Side = "1",
                Instrument = "PETR4",
                BusinessSeqNum = 10016,
                TsUtc = DateTimeOffset.Parse("2026-11-04T12:00:03Z"),
            },
        ];

        foreach (var original in events)
        {
            var json = JsonSerializer.Serialize(original, Options);
            var restored = JsonSerializer.Deserialize<TelemetryEvent>(json, Options);

            Assert.NotNull(restored);
            Assert.Equal(original.GetType(), restored!.GetType());
            Assert.Contains("\"type\":", json, StringComparison.Ordinal);
        }
    }
}
