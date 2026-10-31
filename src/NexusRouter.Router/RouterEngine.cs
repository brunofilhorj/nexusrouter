using System.Buffers;

using Microsoft.Extensions.Logging;

using NexusRouter.Domain.Abstractions;
using NexusRouter.Domain.Enums;
using NexusRouter.Domain.Models;
using NexusRouter.Domain.Pooling;

namespace NexusRouter.Router;

/// <summary>
/// Ponto de entrada síncrono do roteamento: resolve a rota, prepara o payload e faz o fan-out.
/// </summary>
public sealed class RouterEngine : IMessageRouter
{
    private const int StackFieldCapacity = 64;
    private const int FallbackFieldCapacity = 128;

    private readonly RouteResolver _routeResolver;
    private readonly FanOutEngine _fanOutEngine;
    private readonly DispatchPayloadPool _payloadPool;
    private readonly ILogger<RouterEngine> _logger;
    private long _routingSequence;

    /// <summary>
    /// Monta o pipeline do hot path.
    /// </summary>
    /// <param name="routeResolver">Tabela congelada de rotas.</param>
    /// <param name="fanOutEngine">Entrega aos dispatchers.</param>
    /// <param name="payloadPool">Pool de envelopes.</param>
    /// <param name="logger">Avisos de rota ausente e de reescrita recusada.</param>
    /// <exception cref="ArgumentNullException">Alguma dependência é nula.</exception>
    public RouterEngine(
        RouteResolver routeResolver,
        FanOutEngine fanOutEngine,
        DispatchPayloadPool payloadPool,
        ILogger<RouterEngine> logger)
    {
        ArgumentNullException.ThrowIfNull(routeResolver);
        ArgumentNullException.ThrowIfNull(fanOutEngine);
        ArgumentNullException.ThrowIfNull(payloadPool);
        ArgumentNullException.ThrowIfNull(logger);

        _routeResolver = routeResolver;
        _fanOutEngine = fanOutEngine;
        _payloadPool = payloadPool;
        _logger = logger;
    }

    /// <summary>
    /// Copia o frame, aplica o modo de entrega da rota e entrega o payload ao fan-out.
    /// </summary>
    /// <param name="frame">Frame apoiado no pipe. Só é válido durante esta chamada.</param>
    /// <remarks>
    /// Sem await e sem lock.
    /// O frame é copiado para um array do <see cref="ArrayPool{T}"/> antes do retorno.
    /// Em regime estacionário esse array e o envelope saem de pools.
    /// O dono do buffer (<see cref="ArrayPoolMemoryOwner{T}"/>) é um objeto por mensagem.
    /// No modo <see cref="DeliveryMode.Parse"/>, os campos saem de um <see cref="ArrayPool{T}"/> de <see cref="FixField"/>:
    /// o tipo contém <see cref="ReadOnlyMemory{T}"/> e não pode ser alocado na stack.
    /// No modo <see cref="DeliveryMode.PassThrough"/>, a tag 34 é reescrita com o MsgSeqNum inbound
    /// já presente no frame. O dispatcher FIX outbound aplica o sequence number da própria sessão depois.
    /// </remarks>
    public void Route(in IngressFrame frame)
    {
        if (!_routeResolver.TryResolve(frame.SessionId, out RouteContext route))
        {
            _logger.LogWarning("Sessão {SessionId} não tem rota.", frame.SessionId.Value);
            return;
        }

        ArrayPoolMemoryOwner<byte>? owner = RentFrame(in frame);
        if (owner is null)
        {
            return;
        }

        DispatchPayload payload = _payloadPool.Get();
        payload.SessionId = frame.SessionId;
        payload.DeliveryMode = route.DeliveryMode;
        payload.RawBuffer = owner;
        payload.IngressTimestampTicks = frame.IngressTimestampTicks;
        payload.RoutingSequence = Interlocked.Increment(ref _routingSequence);

        switch (route.DeliveryMode)
        {
            case DeliveryMode.PassThrough:
                if (!RewriteInboundSequence(payload, owner.Memory.Span))
                {
                    Discard(payload);
                    return;
                }

                break;

            case DeliveryMode.Parse:
                PopulateParsedMessage(payload);
                break;

            default:
                _logger.LogWarning(
                    "Sessão {SessionId} usa o modo de entrega {DeliveryMode}, que não é suportado.",
                    frame.SessionId.Value,
                    route.DeliveryMode);
                Discard(payload);
                return;
        }

        payload.EgressTimestampTicks = Environment.TickCount64;
        _fanOutEngine.Dispatch(ref route, payload);
    }

    private ArrayPoolMemoryOwner<byte>? RentFrame(in IngressFrame frame)
    {
        long available = frame.RawBuffer.Length;
        if (available <= 0 || available > int.MaxValue)
        {
            _logger.LogWarning(
                "Frame da sessão {SessionId} não tem bytes encaminháveis.",
                frame.SessionId.Value);
            return null;
        }

        int length;
        if (frame.FrameLength > 0 && frame.FrameLength <= available)
        {
            length = frame.FrameLength;
        }
        else
        {
            _logger.LogWarning(
                "Frame da sessão {SessionId} tem comprimento {FrameLength} incompatível com o buffer de {BufferLength} bytes.",
                frame.SessionId.Value,
                frame.FrameLength,
                available);
            length = (int)available;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(length);
        ReadOnlySequence<byte> source = frame.RawBuffer;
        if (source.Length != length)
        {
            source = source.Slice(0, length);
        }

        source.CopyTo(rented.AsSpan(0, length));
        return new ArrayPoolMemoryOwner<byte>(rented, length);
    }

    private bool RewriteInboundSequence(DispatchPayload payload, Span<byte> bytes)
    {
        if (!PassThroughRewriter.TryReadMsgSeqNum(bytes, out long inboundSequence)
            || !PassThroughRewriter.Rewrite(bytes, inboundSequence))
        {
            _logger.LogWarning(
                "Sessão {SessionId} não teve MsgSeqNum reescrito. O frame não foi alterado nem encaminhado.",
                payload.SessionId.Value);
            return false;
        }

        return true;
    }

    private void PopulateParsedMessage(DispatchPayload payload)
    {
        IMemoryOwner<byte>? raw = payload.RawBuffer;
        if (raw is null)
        {
            return;
        }

        // FixField contém ReadOnlyMemory e não pode ser stackalloc. O array sai do pool.
        Memory<byte> memory = raw.Memory;
        FixField[] fields = ArrayPool<FixField>.Shared.Rent(StackFieldCapacity);
        try
        {
            FixMessage message = FixTokenizer.Tokenize(memory, fields.AsSpan(0, StackFieldCapacity), out bool truncated);
            if (truncated)
            {
                FixField[] larger = ArrayPool<FixField>.Shared.Rent(FallbackFieldCapacity);
                FixField[] smaller = fields;
                fields = larger;
                ArrayPool<FixField>.Shared.Return(smaller, clearArray: true);
                message = FixTokenizer.Tokenize(memory, fields.AsSpan(0, FallbackFieldCapacity), out truncated);
                if (truncated)
                {
                    _logger.LogWarning(
                        "Mensagem da sessão {SessionId} tem mais de {FieldLimit} campos FIX. Os demais não foram tokenizados.",
                        payload.SessionId.Value,
                        FallbackFieldCapacity);
                }
            }

            payload.SetParsedMessage(message.MsgType, message.Fields);
        }
        finally
        {
            ArrayPool<FixField>.Shared.Return(fields, clearArray: true);
        }
    }

    private void Discard(DispatchPayload payload)
    {
        payload.RawBuffer?.Dispose();
        payload.RawBuffer = null;
        _payloadPool.Return(payload);
    }
}
