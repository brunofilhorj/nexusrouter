using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Channels;

using BenchmarkDotNet.Attributes;

using Microsoft.Extensions.Logging.Abstractions;

using NexusRouter.Domain.Abstractions;
using NexusRouter.Domain.Enums;
using NexusRouter.Domain.Models;
using NexusRouter.Domain.Pooling;
using NexusRouter.Router;

namespace NexusRouter.Benchmarks;

[MemoryDiagnoser]
public class AllocationBreakdownBenchmark
{
    private static readonly SessionId SessionId = new("BENCH-SESSION");

    private readonly DispatchPayloadPool _payloadPool = new();
    private readonly NexusRouterMetrics _metrics = new();
    private TagList _tags;
    private TagList _dispatcherTags;
    private RouteResolver _routeResolver = null!;
    private RouteContext _route;
    private FanOutEngine _fanOutEngine = null!;
    private byte[] _fixFrame = null!;

    [GlobalSetup]
    public void Setup()
    {
        _tags = new TagList { { "session_id", "BENCH-SESSION" } };
        _dispatcherTags = new TagList { { "dispatcher_id", "bench-dispatcher" } };
        DispatchPayload payload = _payloadPool.Get();
        _payloadPool.Return(payload);

        var dispatcher = new ReturningDispatcher(_payloadPool);
        _route = new RouteContext(
            SessionId,
            DeliveryMode.PassThrough,
            ImmutableArray.Create<IMessageDispatcher>(dispatcher));
        _routeResolver = new RouteResolver([_route], NullLogger<RouteResolver>.Instance);
        _fanOutEngine = new FanOutEngine(
            Channel.CreateUnbounded<AuditRecord>(),
            _payloadPool,
            NullLogger<FanOutEngine>.Instance,
            _metrics);
        _fixFrame = System.Text.Encoding.ASCII.GetBytes(
            "8=FIX.4.4\u00019=152\u000135=D\u000149=CLIENT1\u000156=B3ENTRYPOINT\u000134=1\u0001" +
            "52=20240101-12:00:00.000\u0001115=NEXUS\u000111=ORD001\u000121=1\u0001" +
            "55=PETR4\u000154=1\u000160=20240101-12:00:00.000\u000138=100\u000140=2\u000144=25.50\u000159=0\u0001" +
            "10=157\u0001");
    }

    [Benchmark]
    public void OwnerConstructionAndDispose()
    {
        byte[] array = ArrayPool<byte>.Shared.Rent(256);
        new ArrayPoolMemoryOwner<byte>(array, 256).Dispose();
    }

    [Benchmark]
    public void PayloadPoolGetAndReturn()
    {
        DispatchPayload payload = _payloadPool.Get();
        _payloadPool.Return(payload);
    }

    [Benchmark]
    public void MetricsAddAndRecord()
    {
        _metrics.MessagesReceived.Add(1, _tags);
        _metrics.HotPathLatencyMicroseconds.Record(1, _tags);
    }

    [Benchmark]
    public void MetricsWithLocalTagList()
    {
        TagList tags = new TagList { { "session_id", "BENCH-SESSION" } };
        _metrics.MessagesReceived.Add(1, tags);
        _metrics.HotPathLatencyMicroseconds.Record(1, tags);
    }

    [Benchmark]
    public void DispatcherMetricAdd()
    {
        _metrics.MessagesDispatched.Add(1, _dispatcherTags);
    }

    [Benchmark]
    public void RouteActivityStartAndDispose()
    {
        using Activity? activity = NexusRouterTracing.TryStartRouteActivity("BENCH-SESSION");
    }

    [Benchmark]
    public bool RouteResolverLookup()
    {
        return _routeResolver.TryResolve(SessionId, out _);
    }

    [Benchmark]
    public void PassThroughRewrite()
    {
        PassThroughRewriter.Rewrite(_fixFrame, 1);
    }

    [Benchmark]
    public void FrameCopyAndOwner()
    {
        byte[] array = ArrayPool<byte>.Shared.Rent(_fixFrame.Length);
        _fixFrame.AsSpan().CopyTo(array);
        new ArrayPoolMemoryOwner<byte>(array, _fixFrame.Length).Dispose();
    }

    [Benchmark]
    public void FanOutWithOwner()
    {
        DispatchPayload payload = _payloadPool.Get();
        byte[] array = ArrayPool<byte>.Shared.Rent(256);
        payload.RawBuffer = new ArrayPoolMemoryOwner<byte>(array, 256);
        _fanOutEngine.Dispatch(ref _route, payload);
    }

    private sealed class ReturningDispatcher(DispatchPayloadPool payloadPool) : IMessageDispatcher
    {
        public string DispatcherId => "bench-dispatcher";
        public bool IsHealthy => true;

#pragma warning disable CS0067
        public event Action<string, Exception>? OnDispatchError;
#pragma warning restore CS0067

        public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public bool TryEnqueue(DispatchPayload payload)
        {
            payload.RawBuffer?.Dispose();
            payload.RawBuffer = null;
            payloadPool.Return(payload);
            return true;
        }
    }
}
