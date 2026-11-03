using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading.Channels;

using BenchmarkDotNet.Attributes;

using Microsoft.Extensions.Logging.Abstractions;

using NexusRouter.Domain.Abstractions;
using NexusRouter.Domain.Enums;
using NexusRouter.Domain.Models;
using NexusRouter.Domain.Pooling;
using NexusRouter.Router;

namespace NexusRouter.Benchmarks;

/// <summary>
/// Mede o ciclo completo de <see cref="RouterEngine.Route"/> do ingress ao fan-out,
/// usando um dispatcher em memória que descarta payloads.
/// </summary>
/// <remarks>
/// Alocação esperada: 0 B por iteração em regime estacionário — o pool de payloads e
/// o <see cref="ArrayPool{T}"/> fornecem todos os buffers necessários.
/// </remarks>
[MemoryDiagnoser]
public class RouterEngineBenchmark
{
    // Frame FIX 4.4 mínimo em modo PassThrough.
    private static readonly byte[] Fix44Frame = System.Text.Encoding.ASCII.GetBytes(
        "8=FIX.4.4\x019=152\x0135=D\x0149=CLIENT1\x0156=B3ENTRYPOINT\x0134=1\x01" +
        "52=20240101-12:00:00.000\x01115=NEXUS\x0111=ORD001\x0121=1\x01" +
        "55=PETR4\x0154=1\x0160=20240101-12:00:00.000\x0138=100\x0140=2\x0144=25.50\x0159=0\x01" +
        "10=157\x01");

    private RouterEngine _engine = null!;
    private SessionId _sessionId;
    private ReadOnlySequence<byte> _frameSequence;
    private int _frameLength;

    /// <summary>
    /// Constrói o pipeline completo com um dispatcher stub e aquece o pool.
    /// </summary>
    [GlobalSetup]
    public void GlobalSetup()
    {
        _sessionId = new SessionId("BENCH-SESSION");
        var payloadPool = new DispatchPayloadPool();
        var dispatcher = new DiscardingDispatcher("bench-dispatcher", payloadPool);

        var routes = new List<RouteContext>
        {
            new RouteContext(
                _sessionId,
                DeliveryMode.PassThrough,
                ImmutableArray.Create<IMessageDispatcher>(dispatcher)),
        };

        var resolver = new RouteResolver(routes, NullLogger<RouteResolver>.Instance);
        // Channel de auditoria com capacidade mínima (descarta overflow silenciosamente).
        var auditChannel = Channel.CreateBounded<AuditRecord>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
        });

        var metrics = new NexusRouterMetrics();
        var fanOut = new FanOutEngine(
            auditChannel,
            payloadPool,
            NullLogger<FanOutEngine>.Instance,
            metrics);

        _engine = new RouterEngine(
            resolver,
            fanOut,
            payloadPool,
            NullLogger<RouterEngine>.Instance,
            metrics);

        // Pré-aquece o pool com chamadas de Route antes das medições.
        _frameSequence = new ReadOnlySequence<byte>(Fix44Frame);
        _frameLength = Fix44Frame.Length;
        for (int i = 0; i < 8; i++)
        {
            var warmupFrame = new IngressFrame(_sessionId, _frameSequence, Stopwatch.GetTimestamp(), _frameLength);
            _engine.Route(in warmupFrame);
        }
    }

    /// <summary>
    /// Ciclo completo de roteamento: resolve rota, copia buffer, reescreve tag 34 e faz fan-out.
    /// </summary>
    [Benchmark]
    public void Route()
    {
        // IngressFrame é ref struct: deve ser criado na stack, não pode ser campo da classe.
        var frame = new IngressFrame(_sessionId, _frameSequence, Stopwatch.GetTimestamp(), _frameLength);
        _engine.Route(in frame);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Stub interno: dispatcher que descarta payloads sem alocação.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Dispatcher em memória que aceita todos os payloads e os descarta imediatamente.
    /// Usado para medir somente o custo do RouterEngine, sem overhead de I/O externo.
    /// </summary>
    private sealed class DiscardingDispatcher : IMessageDispatcher
    {
        private readonly DispatchPayloadPool _payloadPool;

        public DiscardingDispatcher(string id, DispatchPayloadPool payloadPool)
        {
            DispatcherId = id;
            _payloadPool = payloadPool;
        }

        public string DispatcherId { get; }
        public bool IsHealthy => true;

#pragma warning disable CS0067 // O evento nunca é usado — stub intencional para benchmark
        public event Action<string, Exception>? OnDispatchError;
#pragma warning restore CS0067

        public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public bool TryEnqueue(DispatchPayload payload)
        {
            // Libera o buffer imediatamente para manter o pool operante durante o benchmark.
            payload.RawBuffer?.Dispose();
            payload.RawBuffer = null;
            _payloadPool.Return(payload);
            return true;
        }
    }
}
