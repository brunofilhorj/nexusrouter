using NexusRouter.Audit;
using NexusRouter.Domain.Models;

namespace NexusRouter.Audit.Tests;

public sealed class AuditChannelTests
{
    [Fact]
    public void TryWrite_WhenModeIsDropWriteAndChannelIsFull_DropsTheNewRecord()
    {
        var channel = new AuditChannelSingleton(new AuditOptions
        {
            ChannelCapacity = 1,
            OverflowMode = AuditOverflowMode.DropWrite,
        });
        var writer = new AuditChannelWriter(channel);

        Assert.True(writer.TryWrite(new AuditRecord { MsgType = "D" }));
        Assert.True(writer.TryWrite(new AuditRecord { MsgType = "8" }));

        Assert.True(channel.Instance.Reader.TryRead(out AuditRecord? stored));
        Assert.Equal("D", stored!.MsgType);
        Assert.False(channel.Instance.Reader.TryRead(out _));
    }

    [Fact]
    public void TryWrite_WhenModeIsDropOldestAndChannelIsFull_KeepsTheNewestRecord()
    {
        var channel = new AuditChannelSingleton(new AuditOptions
        {
            ChannelCapacity = 1,
            OverflowMode = AuditOverflowMode.DropOldest,
        });
        var writer = new AuditChannelWriter(channel);

        Assert.True(writer.TryWrite(new AuditRecord { MsgType = "D" }));
        Assert.True(writer.TryWrite(new AuditRecord { MsgType = "8" }));

        Assert.True(channel.Instance.Reader.TryRead(out AuditRecord? stored));
        Assert.Equal("8", stored!.MsgType);
        Assert.False(channel.Instance.Reader.TryRead(out _));
    }
}
