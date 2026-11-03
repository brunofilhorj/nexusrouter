using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Channels;

using Microsoft.Extensions.Logging.Abstractions;

using NexusRouter.Domain.Abstractions;
using NexusRouter.Domain.Enums;
using NexusRouter.Domain.Models;
using NexusRouter.Domain.Pooling;
using NexusRouter.Router;

namespace NexusRouter.Router.Tests;

[Collection("Router engine metrics")]
public sealed class RouterEngineTests
{
    private const int IterationCount = 32;

    private static readonly byte[] Fix44Frame = System.Text.Encoding.ASCII.GetBytes(
        "8=FIX.4.4\u00019=152\u000135=D\u000149=CLIENT1\u000156=B3ENTRYPOINT\u000134=1\u0001" +
        "52=20240101-12:00:00.000\u0001115=NEXUS\u000111=ORD001\u000121=1\u0001" +
        "55=PETR4\u000154=1\u000160=20240101-12:00:00.000\u000138=100\u000140=2\u000144=25.50\u000159=0\u0001" +
        "10=157\u0001");

    [Fact]
    public void Route_WhenDispatcherRecyclesPayload_RecordsSaneLatency()
    {
        List<double> measurements = RecordLatencies(accepted: true, iterations: IterationCount);

        Assert.Equal(IterationCount, measurements.Count);
        Assert.All(measurements, measurement => Assert.InRange(measurement, 0, 1_000_000));
    }

    [Fact]
    public void Route_WhenDispatcherRejectsPayload_RecordsSaneLatency()
    {
        List<double> measurements = RecordLatencies(accepted: false, iterations: IterationCount);

        Assert.Equal(IterationCount, measurements.Count);
        Assert.All(measurements, measurement => Assert.InRange(measurement, 0, 1_000_000));
    }

    [Fact]
    public void Route_RecordsLatencyInMicroseconds()
    {
        List<double> measurements = RecordLatencies(accepted: true, iterations: 1);
        double measurement = Assert.Single(measurements);

        Assert.True(measurement > 0, $"A latência deve ser positiva, mas foi {measurement} µs.");
        Assert.True(measurement < 100_000, $"A latência deve permanecer abaixo de 100 ms, mas foi {measurement} µs.");
        Assert.NotEqual(0, measurement % 1_000);
    }

    private static List<double> RecordLatencies(bool accepted, int iterations)
    {
        var measurements = new List<double>(iterations);
        using var listener = CreateListener(measurements);
        listener.Start();
        using var metrics = new NexusRouterMetrics();

        var sessionId = new SessionId("ROUTER-TEST");
        var dispatcher = new TestDispatcher(accepted);
        var route = new RouteContext(
            sessionId,
            DeliveryMode.PassThrough,
            ImmutableArray.Create<IMessageDispatcher>(dispatcher));
        var routeResolver = new RouteResolver([route], NullLogger<RouteResolver>.Instance);
        var payloadPool = new DispatchPayloadPool();
        var auditChannel = Channel.CreateUnbounded<AuditRecord>();
        var fanOutEngine = new FanOutEngine(
            auditChannel,
            payloadPool,
            NullLogger<FanOutEngine>.Instance,
            metrics);
        var router = new RouterEngine(
            routeResolver,
            fanOutEngine,
            payloadPool,
            NullLogger<RouterEngine>.Instance,
            metrics);
        var frameSequence = new ReadOnlySequence<byte>(Fix44Frame);

        for (int i = 0; i < iterations; i++)
        {
            long ingressTicks = Stopwatch.GetTimestamp() - Stopwatch.Frequency / 1_000;
            var frame = new IngressFrame(sessionId, frameSequence, ingressTicks, Fix44Frame.Length);
            router.Route(in frame);
        }

        Assert.Equal(iterations, dispatcher.EnqueueCount);
        return measurements;
    }

    private static MeterListener CreateListener(List<double> measurements)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == NexusRouterMetrics.MeterName
                    && instrument.Name == "hotpath_latency_microseconds")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>(
            (_, measurement, _, _) => measurements.Add(measurement));
        return listener;
    }

    private sealed class TestDispatcher(bool accepted) : IMessageDispatcher
    {
        public string DispatcherId => "router-test";
        public bool IsHealthy => true;
        public int EnqueueCount { get; private set; }

#pragma warning disable CS0067
        public event Action<string, Exception>? OnDispatchError;
#pragma warning restore CS0067

        public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public bool TryEnqueue(DispatchPayload payload)
        {
            EnqueueCount++;
            if (accepted)
            {
                IMemoryOwner<byte>? buffer = payload.RawBuffer;
                payload.RawBuffer = null;
                payload.Reset();
                buffer?.Dispose();
            }

            return accepted;
        }
    }
}
