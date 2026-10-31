using NexusRouter.Egress.Fix;

namespace NexusRouter.Egress.Tests;

public sealed class FixOutboundDispatcherOptionsTests
{
    [Fact]
    public void EnsureValid_LoopbackListener_Accepts()
    {
        Listener().EnsureValid();
    }

    [Fact]
    public void EnsureValid_WhenListenAddressIsHostname_Throws()
    {
        FixOutboundDispatcherOptions options = Listener();
        options = new FixOutboundDispatcherOptions
        {
            ListenAddress = "downstream-oms.internal",
            ListenPort = options.ListenPort,
            SenderCompId = options.SenderCompId,
            BeginString = options.BeginString,
            SequenceStoreDirectory = options.SequenceStoreDirectory,
        };

        Assert.Throws<ArgumentException>(() => options.EnsureValid());
    }

    private static FixOutboundDispatcherOptions Listener() =>
        new()
        {
            ListenAddress = "127.0.0.1",
            ListenPort = 6001,
            SenderCompId = "NEXUS",
            BeginString = "FIX.4.4",
            SequenceStoreDirectory = "seq",
        };
}
