using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using System.Threading.Channels;

using Microsoft.Extensions.Logging.Abstractions;

using NexusRouter.Domain.Abstractions;
using NexusRouter.Domain.Enums;
using NexusRouter.Domain.Models;
using NexusRouter.Domain.Pooling;

namespace NexusRouter.Router.Tests;

[Collection("Router engine metrics")]
public sealed class RouterEngineRoutingTests
{
    [Fact]
    public void Route_InPassThroughMode_FansOutSharedBufferUntilBothDispatchersRelease()
    {
        var sessionId = new SessionId("PASS-THROUGH");
        var firstDispatcher = new ChannelDispatcher("first");
        var secondDispatcher = new ChannelDispatcher("second");
        using var harness = CreateHarness(sessionId, DeliveryMode.PassThrough, firstDispatcher, secondDispatcher);
        byte[] frameBytes = FixMessageTestData.CreateValidFrame(sequenceNumber: 12);
        IngressFrame frame = CreateFrame(sessionId, frameBytes);

        harness.Router.Route(in frame);

        Assert.True(firstDispatcher.Reader.TryRead(out DispatchPayload? firstPayload));
        Assert.True(secondDispatcher.Reader.TryRead(out DispatchPayload? secondPayload));
        Assert.False(firstDispatcher.Reader.TryRead(out _));
        Assert.False(secondDispatcher.Reader.TryRead(out _));

        IMemoryOwner<byte> firstOwner = Assert.IsAssignableFrom<IMemoryOwner<byte>>(firstPayload!.RawBuffer);
        IMemoryOwner<byte> secondOwner = Assert.IsAssignableFrom<IMemoryOwner<byte>>(secondPayload!.RawBuffer);
        Assert.Same(firstOwner, secondOwner);
        Assert.Equal(frameBytes, firstOwner.Memory.ToArray());

        firstPayload.RawBuffer = null;
        firstOwner.Dispose();
        Assert.Equal(frameBytes, secondOwner.Memory.ToArray());

        secondPayload.RawBuffer = null;
        secondOwner.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = firstOwner.Memory);

        harness.PayloadPool.Return(firstPayload);
        harness.PayloadPool.Return(secondPayload);
    }

    [Fact]
    public void Route_InParseMode_ProvidesParsedFieldsToEachDispatcher()
    {
        var sessionId = new SessionId("PARSE");
        var firstDispatcher = new ChannelDispatcher("first");
        var secondDispatcher = new ChannelDispatcher("second");
        using var harness = CreateHarness(sessionId, DeliveryMode.Parse, firstDispatcher, secondDispatcher);
        byte[] frameBytes = FixMessageTestData.CreateValidFrame(sequenceNumber: 12);
        IngressFrame frame = CreateFrame(sessionId, frameBytes);

        harness.Router.Route(in frame);

        Assert.True(firstDispatcher.Reader.TryRead(out DispatchPayload? firstPayload));
        Assert.True(secondDispatcher.Reader.TryRead(out DispatchPayload? secondPayload));
        AssertParsedFields(firstPayload!);
        AssertParsedFields(secondPayload!);

        ReleasePayload(harness.PayloadPool, firstPayload!);
        ReleasePayload(harness.PayloadPool, secondPayload!);
    }

    [Fact]
    public void Route_WhenOneDispatcherChannelIsFull_DeliversToOtherAndAuditsDrop()
    {
        var sessionId = new SessionId("FULL-CHANNEL");
        var fullDispatcher = new ChannelDispatcher("full");
        var availableDispatcher = new ChannelDispatcher("available");
        var placeholder = new DispatchPayload();
        Assert.True(fullDispatcher.Writer.TryWrite(placeholder));
        using var harness = CreateHarness(
            sessionId,
            DeliveryMode.PassThrough,
            fullDispatcher,
            availableDispatcher);
        byte[] frameBytes = FixMessageTestData.CreateValidFrame(sequenceNumber: 12);
        IngressFrame frame = CreateFrame(sessionId, frameBytes);

        harness.Router.Route(in frame);

        Assert.True(availableDispatcher.Reader.TryRead(out DispatchPayload? acceptedPayload));
        Assert.Equal(frameBytes, acceptedPayload!.RawBuffer!.Memory.ToArray());
        Assert.True(fullDispatcher.Reader.TryRead(out DispatchPayload? stillQueued));
        Assert.Same(placeholder, stillQueued);
        Assert.True(harness.AuditChannel.Reader.TryRead(out AuditRecord? auditRecord));
        Assert.True(auditRecord!.WasDropped);
        Assert.Equal("Channel do dispatcher cheio", auditRecord.DropReason);
        Assert.Equal(new[] { "full" }, auditRecord.DispatcherIds);

        ReleasePayload(harness.PayloadPool, acceptedPayload);
    }

    [Fact]
    public void Route_WhenSessionHasNoRoute_DropsWithoutDispatchAndRecordsMetric()
    {
        var sessionId = new SessionId("NO-ROUTE");
        var dispatcher = new ChannelDispatcher("unused");
        using var harness = CreateHarness(
            sessionId,
            DeliveryMode.PassThrough,
            registerRoute: false,
            dispatchers: [dispatcher]);
        var dropReasons = new List<string>();
        using MeterListener listener = CreateDropListener(dropReasons);
        listener.Start();
        byte[] frameBytes = FixMessageTestData.CreateValidFrame(sequenceNumber: 12);
        IngressFrame frame = CreateFrame(sessionId, frameBytes);

        harness.Router.Route(in frame);

        Assert.False(dispatcher.Reader.TryRead(out _));
        Assert.Equal(new[] { "no_route" }, dropReasons);
    }

    [Fact]
    public void Route_WhenPassThroughFrameHasNoSequence_DoesNotDispatchAndReturnsPayloadToPool()
    {
        var sessionId = new SessionId("INVALID-FRAME");
        var dispatcher = new ChannelDispatcher("unused");
        using var harness = CreateHarness(sessionId, DeliveryMode.PassThrough, dispatcher);
        harness.PayloadPool.Prewarm(1);
        DispatchPayload cachedPayload = harness.PayloadPool.Get();
        harness.PayloadPool.Return(cachedPayload);
        byte[] frameBytes = FixMessageTestData.CreateValidFrame(sequenceNumber: null);
        IngressFrame frame = CreateFrame(sessionId, frameBytes);

        harness.Router.Route(in frame);

        Assert.False(dispatcher.Reader.TryRead(out _));
        Assert.Same(cachedPayload, harness.PayloadPool.Get());
        harness.PayloadPool.Return(cachedPayload);
    }

    private static void AssertParsedFields(DispatchPayload payload)
    {
        Assert.True(payload.TryGetParsedMessage(out FixMessage parsed));
        Assert.Equal("D", Encoding.ASCII.GetString(parsed.MsgType));
        Assert.True(parsed.TryGetField(11, out ReadOnlySpan<byte> clientOrderId));
        Assert.Equal("ORDER-1", Encoding.ASCII.GetString(clientOrderId));
        Assert.True(parsed.TryGetField(55, out ReadOnlySpan<byte> symbol));
        Assert.Equal("IBM", Encoding.ASCII.GetString(symbol));
    }

    private static void ReleasePayload(DispatchPayloadPool payloadPool, DispatchPayload payload)
    {
        IMemoryOwner<byte>? buffer = payload.RawBuffer;
        payload.RawBuffer = null;
        buffer?.Dispose();
        payloadPool.Return(payload);
    }

    private static IngressFrame CreateFrame(SessionId sessionId, byte[] bytes) =>
        new(sessionId, new ReadOnlySequence<byte>(bytes), Stopwatch.GetTimestamp(), bytes.Length);

    private static RouterHarness CreateHarness(
        SessionId sessionId,
        DeliveryMode deliveryMode,
        params ChannelDispatcher[] dispatchers) =>
        CreateHarness(sessionId, deliveryMode, registerRoute: true, dispatchers);

    private static RouterHarness CreateHarness(
        SessionId sessionId,
        DeliveryMode deliveryMode,
        bool registerRoute,
        params ChannelDispatcher[] dispatchers)
    {
        var routes = new List<RouteContext>();
        if (registerRoute)
        {
            routes.Add(new RouteContext(
                sessionId,
                deliveryMode,
                ImmutableArray.Create<IMessageDispatcher>(dispatchers)));
        }

        var payloadPool = new DispatchPayloadPool();
        var auditChannel = Channel.CreateBounded<AuditRecord>(new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
        var metrics = new NexusRouterMetrics();
        var routeResolver = new RouteResolver(routes, NullLogger<RouteResolver>.Instance);
        var fanOut = new FanOutEngine(
            auditChannel,
            payloadPool,
            NullLogger<FanOutEngine>.Instance,
            metrics);
        var router = new RouterEngine(
            routeResolver,
            fanOut,
            payloadPool,
            NullLogger<RouterEngine>.Instance,
            metrics);

        return new RouterHarness(router, payloadPool, auditChannel, metrics);
    }

    private static MeterListener CreateDropListener(List<string> dropReasons)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == NexusRouterMetrics.MeterName
                    && instrument.Name == "messages_dropped_total")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            if (instrument.Name != "messages_dropped_total")
            {
                return;
            }

            for (int i = 0; i < tags.Length; i++)
            {
                if (tags[i].Key == "reason" && tags[i].Value is string reason)
                {
                    dropReasons.Add(reason);
                }
            }
        });
        return listener;
    }

    private sealed class RouterHarness(
        RouterEngine router,
        DispatchPayloadPool payloadPool,
        Channel<AuditRecord> auditChannel,
        NexusRouterMetrics metrics) : IDisposable
    {
        public RouterEngine Router { get; } = router;

        public DispatchPayloadPool PayloadPool { get; } = payloadPool;

        public Channel<AuditRecord> AuditChannel { get; } = auditChannel;

        public void Dispose() => metrics.Dispose();
    }

    private sealed class ChannelDispatcher(string dispatcherId) : IMessageDispatcher
    {
        private readonly Channel<DispatchPayload> _channel = Channel.CreateBounded<DispatchPayload>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
            });

        public string DispatcherId { get; } = dispatcherId;

        public bool IsHealthy => true;

        public ChannelReader<DispatchPayload> Reader => _channel.Reader;

        public ChannelWriter<DispatchPayload> Writer => _channel.Writer;

        public event Action<string, Exception>? OnDispatchError
        {
            add { }
            remove { }
        }

        public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public bool TryEnqueue(DispatchPayload payload) => _channel.Writer.TryWrite(payload);
    }
}
