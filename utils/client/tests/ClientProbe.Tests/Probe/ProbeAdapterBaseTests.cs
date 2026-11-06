using ClientProbe.Probe;
using ClientProbe.Telemetry;
using Microsoft.Extensions.Options;

namespace ClientProbe.Tests.Probe;

public sealed class ProbeAdapterBaseTests
{
    private sealed class NullPublisher : ITelemetryPublisher
    {
        public bool TryPublish(ProbeEvent probeEvent, ProbeCounters counters) => true;
    }

    private sealed class TestAdapter()
        : ProbeAdapterBase(ProbeTransport.Kafka, new NullPublisher(), Options.Create(new ClientProbeOptions()), TimeProvider.System)
    {
        public override Task ConnectAsync(ProbeProfile profile, CancellationToken ct) => Task.CompletedTask;

        public override Task DisconnectAsync(CancellationToken ct) => Task.CompletedTask;

        public void Move(ProbeState state, string? error = null) => SetState(state, error);

        public void Warn(string error) => SetLastError(error);
    }

    [Fact]
    public void Disconnected_ClearsTheLastError()
    {
        var adapter = new TestAdapter();
        adapter.Move(ProbeState.Faulted, "1/1 brokers are down");

        adapter.Move(ProbeState.Disconnected);

        Assert.Null(adapter.LastError);
    }

    [Fact]
    public void Disconnected_WithAnError_KeepsThatError()
    {
        var adapter = new TestAdapter();

        adapter.Move(ProbeState.Disconnected, "broker unreachable");

        Assert.Equal("broker unreachable", adapter.LastError);
    }

    [Fact]
    public void Faulted_KeepsTheError_AndLiveDoesNotClearIt()
    {
        var adapter = new TestAdapter();
        adapter.Move(ProbeState.Faulted, "boom");

        adapter.Move(ProbeState.Live);

        Assert.Equal("boom", adapter.LastError);
    }

    [Fact]
    public void SetLastError_DoesNotChangeTheState()
    {
        var adapter = new TestAdapter();
        adapter.Move(ProbeState.Live);

        adapter.Warn("1/1 brokers are down");

        Assert.Equal(ProbeState.Live, adapter.State);
        Assert.Equal("1/1 brokers are down", adapter.LastError);
    }
}
