using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using DropCopySim.Core.Acceptor;
using DropCopySim.Core.Fix;
using DropCopySim.Core.Generation;
using DropCopySim.Core.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;

namespace DropCopySim.Core.Tests;

public class OrderFlowGeneratorTests
{
    [Fact]
    public async Task Start_FiniteLimit_SendsFramesAtAboutTheTargetRateAndReturnsToIdle()
    {
        await using var fixture = await ActiveSessionFixture.ConnectAsync();
        var telemetry = Channel.CreateUnbounded<TelemetryEvent>();
        var config = CreateConfig(totalOrders: 10, ratePerSecond: 100);
        await using var generator = CreateGenerator(config, telemetry.Writer, messageSampleMaxPerSecond: 50);

        generator.AttachSession(fixture.Session);
        var started = Stopwatch.StartNew();
        generator.Start();
        await WaitUntilAsync(() => generator.State == SimulationState.Idle, TimeSpan.FromSeconds(5));
        var elapsed = started.Elapsed;

        Assert.Equal(20, generator.Counters.TotalSent);
        Assert.Equal(0, generator.Counters.Errors);
        Assert.True(elapsed > TimeSpan.FromMilliseconds(100), $"Round finished too quickly: {elapsed}.");
        Assert.Contains("0", generator.Counters.SnapshotByExecType().Keys);
        Assert.Equal(10, generator.Counters.SnapshotByExecType()["2"]);

        var frames = await fixture.ReadFramesAsync();
        Assert.Equal(20, frames.Count);
        for (var i = 0; i < frames.Count; i++)
        {
            Assert.Equal((i + 1).ToString(), Field(frames[i], FixTags.MsgSeqNum));
            Assert.Equal((i + 1).ToString(), Field(frames[i], FixTags.BusinessSeqNum));
            Assert.Equal("8", Field(frames[i], FixTags.MsgType));
        }

        var events = ReadAll(telemetry.Reader);
        Assert.Equal(20, events.OfType<MessageSample>().Count());
        Assert.Contains(events, static e => e is SimulationStateChanged { State: SimulationState.Idle, Reason: "limit" });
    }

    [Fact]
    public async Task PublishConfig_WhileOrderInFlight_AppliesOutcomeOnTheNextOrder()
    {
        await using var fixture = await ActiveSessionFixture.ConnectAsync();
        var telemetry = Channel.CreateUnbounded<TelemetryEvent>();
        var config = CreateConfig(totalOrders: 2, ratePerSecond: 2);
        await using var generator = CreateGenerator(config, telemetry.Writer);

        generator.AttachSession(fixture.Session);
        generator.Start();
        await WaitUntilAsync(() => generator.Counters.TotalSent >= 1, TimeSpan.FromSeconds(5));
        Assert.True(generator.Counters.TotalSent < 3);

        generator.PublishConfig(config with { Outcome = new OutcomeMix { RejectedPct = 100 } });
        await WaitUntilAsync(() => generator.State == SimulationState.Idle, TimeSpan.FromSeconds(10));

        var counts = generator.Counters.SnapshotByExecType();
        Assert.Equal(2, counts["0"]);
        Assert.Equal(1, counts["2"]);
        Assert.Equal(1, counts["8"]);
    }

    [Fact]
    public async Task Pause_WhileRunning_StopsSendingUntilResume()
    {
        await using var fixture = await ActiveSessionFixture.ConnectAsync();
        var telemetry = Channel.CreateUnbounded<TelemetryEvent>();
        await using var generator = CreateGenerator(
            CreateConfig(totalOrders: 0, ratePerSecond: 10),
            telemetry.Writer,
            messageSampleMaxPerSecond: 0);

        generator.AttachSession(fixture.Session);
        generator.Start();
        await WaitUntilAsync(() => generator.Counters.TotalSent >= 1, TimeSpan.FromSeconds(5));

        generator.Pause();
        await Task.Delay(50);
        var pausedAt = generator.Counters.TotalSent;
        await Task.Delay(400);
        Assert.Equal(pausedAt, generator.Counters.TotalSent);
        Assert.Equal(SimulationState.Paused, generator.State);

        generator.Resume();
        await WaitUntilAsync(() => generator.Counters.TotalSent > pausedAt, TimeSpan.FromSeconds(5));
        await generator.StopAsync();
        Assert.Equal(SimulationState.Idle, generator.State);
    }

    [Fact]
    public async Task Start_WhenTelemetryWriterRejects_DoesNotBlockFixSend()
    {
        await using var fixture = await ActiveSessionFixture.ConnectAsync();
        var telemetry = Channel.CreateBounded<TelemetryEvent>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
        await using var generator = CreateGenerator(
            CreateConfig(totalOrders: 4, ratePerSecond: 200),
            telemetry.Writer,
            messageSampleMaxPerSecond: 1_000);

        generator.AttachSession(fixture.Session);
        generator.Start();
        await WaitUntilAsync(() => generator.State == SimulationState.Idle, TimeSpan.FromSeconds(5));

        Assert.Equal(8, generator.Counters.TotalSent);
    }

    [Fact]
    public async Task Pause_WhenIdle_ThrowsInvalidOperationException()
    {
        var telemetry = Channel.CreateUnbounded<TelemetryEvent>();
        await using var generator = CreateGenerator(CreateConfig(totalOrders: 1, ratePerSecond: 10), telemetry.Writer);

        var error = Assert.Throws<InvalidOperationException>(generator.Pause);
        Assert.Contains("Idle", error.Message, StringComparison.Ordinal);
    }

    private static OrderFlowGenerator CreateGenerator(
        SimulationConfig config,
        ChannelWriter<TelemetryEvent> telemetry,
        int messageSampleMaxPerSecond = 50) =>
        new(
            config,
            new SimulationCounters(),
            telemetry,
            NullLogger<OrderFlowGenerator>.Instance,
            messageSampleMaxPerSecond);

    private static SimulationConfig CreateConfig(long totalOrders, int ratePerSecond) => new()
    {
        TotalOrders = totalOrders,
        RatePerSecond = ratePerSecond,
        Accounts = ["ACC1"],
        Instruments =
        [
            new Instrument
            {
                SecurityId = 200001,
                Symbol = "PETR4",
                SecurityExchange = "BVMF",
            },
        ],
        Outcome = new OutcomeMix { FillPct = 100 },
        EnabledMessageTypes = ["8"],
    };

    private static List<TelemetryEvent> ReadAll(ChannelReader<TelemetryEvent> reader)
    {
        var events = new List<TelemetryEvent>();
        while (reader.TryRead(out var telemetryEvent))
        {
            events.Add(telemetryEvent);
        }

        return events;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > timeout)
            {
                throw new TimeoutException("Condition was not reached before the timeout.");
            }

            await Task.Delay(10);
        }
    }

    private static string Field(string message, int tag)
    {
        var prefix = tag.ToString() + "=";
        foreach (var field in message.Split('\u0001'))
        {
            if (field.StartsWith(prefix, StringComparison.Ordinal))
            {
                return field[prefix.Length..];
            }
        }

        throw new InvalidOperationException($"FIX tag {tag} was not present.");
    }

    private sealed class ActiveSessionFixture : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly TcpClient _client;

        private ActiveSessionFixture(TcpListener listener, TcpClient client, AcceptorSession session)
        {
            _listener = listener;
            _client = client;
            Session = session;
        }

        public AcceptorSession Session { get; }

        public static async Task<ActiveSessionFixture> ConnectAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var server = await listener.AcceptSocketAsync();
            var session = new AcceptorSession(server, new AcceptorOptions(), NullLogger<AcceptorSession>.Instance);
            session.Fsm.TransitionToConnected();
            session.Fsm.TransitionToLogonReceived();
            session.Fsm.TransitionToActive();
            return new ActiveSessionFixture(listener, client, session);
        }

        public async Task<List<string>> ReadFramesAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var buffer = new byte[256 * 1024];
            var total = 0;
            NetworkStream stream = _client.GetStream();
            while (total < buffer.Length && !timeout.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                idle.CancelAfter(TimeSpan.FromMilliseconds(200));
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer.AsMemory(total), idle.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            var frames = new List<string>();
            ReadOnlySequence<byte> remaining = new(buffer.AsMemory(0, total));
            while (Fix44FrameScanner.TryDecode(ref remaining, out ReadOnlySequence<byte> frame))
            {
                frames.Add(Encoding.ASCII.GetString(frame.ToArray()));
            }

            return frames;
        }

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            _client.Dispose();
            _listener.Stop();
        }
    }
}
