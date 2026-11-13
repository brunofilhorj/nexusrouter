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
        };

        Assert.Throws<ArgumentException>(() => options.EnsureValid());
    }

    [Fact]
    public void Validate_WhenReplayModeIsUnknown_Fails()
    {
        FixOutboundDispatcherOptions options = new()
        {
            ListenAddress = "127.0.0.1",
            ListenPort = 6001,
            SenderCompId = "NEXUS",
            BeginString = "FIX.4.4",
            Recovery = new AcceptorRecoveryOptions { ReplayMode = (ReplayMode)99 },
        };

        Assert.Throws<InvalidOperationException>(() => options.EnsureValid());
    }

    [Fact]
    public void Validate_WhenClientOverrideReplayModeIsUnknown_Fails()
    {
        FixOutboundDispatcherOptions options = new()
        {
            ListenAddress = "127.0.0.1",
            ListenPort = 6001,
            SenderCompId = "NEXUS",
            BeginString = "FIX.4.4",
            ClientOverrides = [new AcceptorClientOverride { SenderCompId = "OMS1", ReplayMode = (ReplayMode)99 }],
        };

        Assert.Throws<InvalidOperationException>(() => options.EnsureValid());
    }

    [Fact]
    public void RecoveryMerge_ClientReplayModeOverridesDispatcher()
    {
        AcceptorRecoveryOptions dispatcher = new() { ReplayMode = ReplayMode.StartOfDay };

        AcceptorRecoveryOptions merged = dispatcher.Merge(
            new AcceptorClientOverride { SenderCompId = "OMS1", ReplayMode = ReplayMode.FromLogon });

        Assert.Equal(ReplayMode.FromLogon, merged.ReplayMode);
    }

    private static FixOutboundDispatcherOptions Listener() =>
        new()
        {
            ListenAddress = "127.0.0.1",
            ListenPort = 6001,
            SenderCompId = "NEXUS",
            BeginString = "FIX.4.4",
        };
}
