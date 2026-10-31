using NexusRouter.Domain.Models;
using NexusRouter.Egress.Kafka;

namespace NexusRouter.Egress.Tests;

public sealed class ByClOrdIdPartitionStrategyTests
{
    [Fact]
    public void GetPartition_SameClOrdId_StaysOnTheSamePartition()
    {
        var strategy = new ByClOrdIdPartitionStrategy(8);
        byte[] clOrdId = "ORD-100"u8.ToArray();

        int? first = strategy.GetPartition(Payload(clOrdId));
        int? second = strategy.GetPartition(Payload(clOrdId));

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.InRange(first.Value, 0, 7);
    }

    [Fact]
    public void GetPartition_WithoutClOrdId_ReturnsNull()
    {
        var strategy = new ByClOrdIdPartitionStrategy(4);
        var payload = new DispatchPayload();
        payload.SetParsedMessage("D"u8, ReadOnlySpan<FixField>.Empty);

        Assert.Null(strategy.GetPartition(payload));
        payload.Reset();
    }

    private static DispatchPayload Payload(byte[] clOrdId)
    {
        var payload = new DispatchPayload();
        FixField[] fields = [new FixField(11, clOrdId)];
        payload.SetParsedMessage("D"u8, fields);
        return payload;
    }
}
