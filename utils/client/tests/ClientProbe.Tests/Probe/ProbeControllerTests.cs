using ClientProbe.Probe;

namespace ClientProbe.Tests.Probe;

public sealed class ProbeControllerTests
{
    private sealed class FakeAdapter(ProbeTransport transport) : IProbeAdapter
    {
        public TaskCompletionSource? ConnectGate { get; set; }
        public int Connects { get; private set; }
        public int Disconnects { get; private set; }
        public ProbeTransport Transport { get; } = transport;
        public ProbeState State { get; private set; }
        public string Endpoint => "";
        public string? LastError => null;
        public ProbeCounters Counters { get; } = new();

        public async Task ConnectAsync(ProbeProfile profile, CancellationToken ct)
        {
            Connects++;
            State = ProbeState.Connecting;
            if (ConnectGate is not null)
            {
                await ConnectGate.Task;
            }

            State = ProbeState.Live;
        }

        public Task DisconnectAsync(CancellationToken ct)
        {
            Disconnects++;
            State = ProbeState.Disconnected;
            return Task.CompletedTask;
        }
    }

    private static (ProbeController Controller, FakeAdapter Kafka, FakeAdapter Rabbit, FakeAdapter Fix) Create()
    {
        var k = new FakeAdapter(ProbeTransport.Kafka);
        var r = new FakeAdapter(ProbeTransport.Rabbit);
        var f = new FakeAdapter(ProbeTransport.Fix);
        return (new ProbeController([k, r, f]), k, r, f);
    }

    private static readonly KafkaProfile Kafka = new("h:1", "t", "g", "Latest");

    [Fact]
    public async Task ConnectAsync_OneTransport_DoesNotTouchTheOthers()
    {
        var (controller, kafka, rabbit, fix) = Create();

        await controller.ConnectAsync(Kafka, CancellationToken.None);

        Assert.Equal(ProbeState.Live, kafka.State);
        Assert.Equal(0, rabbit.Connects + rabbit.Disconnects + fix.Connects + fix.Disconnects);
    }

    [Fact]
    public async Task ConnectAsync_SameTransportLive_ClosesPreviousBeforeReconnecting()
    {
        var (controller, kafka, _, _) = Create();
        await controller.ConnectAsync(Kafka, CancellationToken.None);

        await controller.ConnectAsync(Kafka, CancellationToken.None);

        Assert.Equal(2, kafka.Connects);
        Assert.Equal(1, kafka.Disconnects);
        Assert.Equal(ProbeState.Live, kafka.State);
    }

    [Fact]
    public async Task ConnectAsync_WhileConnecting_ThrowsConflict()
    {
        var (controller, kafka, _, _) = Create();
        kafka.ConnectGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = controller.ConnectAsync(Kafka, CancellationToken.None);

        await Assert.ThrowsAsync<ProbeConflictException>(() => controller.ConnectAsync(Kafka, CancellationToken.None));

        kafka.ConnectGate.SetResult();
        await first;
    }

    [Fact]
    public async Task StopAsync_ClosesAllThreeAdapters()
    {
        var (controller, kafka, rabbit, fix) = Create();

        await controller.StopAsync(CancellationToken.None);

        Assert.Equal(1, kafka.Disconnects);
        Assert.Equal(1, rabbit.Disconnects);
        Assert.Equal(1, fix.Disconnects);
    }

    [Fact]
    public async Task StopAsync_TokenAlreadyCancelled_DoesNotThrow()
    {
        var (controller, _, _, _) = Create();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await controller.StopAsync(cts.Token);
    }
}
