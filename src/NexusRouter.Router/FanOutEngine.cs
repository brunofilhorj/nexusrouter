using System.Buffers;
using System.Collections.Immutable;
using System.Threading.Channels;

using Microsoft.Extensions.Logging;

using NexusRouter.Domain.Abstractions;
using NexusRouter.Domain.Models;
using NexusRouter.Domain.Pooling;

namespace NexusRouter.Router;

/// <summary>
/// Entrega um payload a todos os dispatchers da rota sem bloquear a thread chamadora.
/// </summary>
/// <remarks>
/// Com um dispatcher, o payload segue direto e o buffer não é compartilhado.
/// Com N dispatchers, o buffer entra em um <see cref="RefCountedMemoryOwner"/> com uma referência.
/// Cada payload extra chama <see cref="RefCountedMemoryOwner.AddRef"/> antes de compartilhar o mesmo owner,
/// então a contagem fica igual a N. O constructor não recebe N de uma vez: somar AddRef em cima de N
/// deixaria o array preso no pool.
/// Cada <see cref="IMessageDispatcher.TryEnqueue"/> que retorna <see langword="false"/> libera uma referência
/// e grava um <see cref="AuditRecord"/> de overflow. Isso não lança exceção.
/// </remarks>
public sealed class FanOutEngine
{
    private const string ChannelFullDropReason = "Channel do dispatcher cheio";

    private readonly ChannelWriter<AuditRecord> _auditWriter;
    private readonly DispatchPayloadPool _payloadPool;
    private readonly ILogger<FanOutEngine> _logger;

    /// <summary>
    /// Cria o fan-out com o channel de auditoria e o pool de envelopes.
    /// </summary>
    /// <param name="auditChannel">Channel não bloqueante do cold path.</param>
    /// <param name="payloadPool">Pool usado para os envelopes além do primeiro dispatcher.</param>
    /// <param name="logger">Log de channel cheio.</param>
    /// <exception cref="ArgumentNullException">Alguma dependência é nula.</exception>
    public FanOutEngine(
        Channel<AuditRecord> auditChannel,
        DispatchPayloadPool payloadPool,
        ILogger<FanOutEngine> logger)
    {
        ArgumentNullException.ThrowIfNull(auditChannel);
        ArgumentNullException.ThrowIfNull(payloadPool);
        ArgumentNullException.ThrowIfNull(logger);

        _auditWriter = auditChannel.Writer;
        _payloadPool = payloadPool;
        _logger = logger;
    }

    /// <summary>
    /// Enfileira <paramref name="payload"/> em cada dispatcher da rota.
    /// </summary>
    /// <param name="route">Rota já resolvida. Passada por referência para não copiar o array de dispatchers.</param>
    /// <param name="payload">Envelope preparado pelo router. No sucesso, o dispatcher passa a ser o dono.</param>
    /// <remarks>
    /// Sem await e sem lock. Seguro para chamada no hot path.
    /// Channel cheio libera o buffer daquele dispatcher e segue para os demais.
    /// O pool de payloads entra no construtor porque o fan-out aluga os envelopes extras.
    /// </remarks>
    public void Dispatch(ref RouteContext route, DispatchPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (route.Dispatchers.IsDefault || route.Dispatchers.Length == 0)
        {
            ReleaseOwnedBuffer(payload);
            _payloadPool.Return(payload);
            _logger.LogDebug(
                "Sessão {SessionId} não tem dispatcher. A mensagem não foi encaminhada.",
                payload.SessionId.Value);
            return;
        }

        if (payload.RawBuffer is null)
        {
            _payloadPool.Return(payload);
            _logger.LogWarning(
                "Sessão {SessionId} produziu um payload sem buffer. A mensagem não foi encaminhada.",
                payload.SessionId.Value);
            return;
        }

        ImmutableArray<IMessageDispatcher> dispatchers = route.Dispatchers;
        if (dispatchers.Length == 1)
        {
            if (!Deliver(dispatchers[0], payload))
            {
                _payloadPool.Return(payload);
            }

            return;
        }

        var shared = new RefCountedMemoryOwner(payload.RawBuffer, initialRefCount: 1);
        payload.RawBuffer = shared;

        int extraCount = dispatchers.Length - 1;
        DispatchPayload[] extras = ArrayPool<DispatchPayload>.Shared.Rent(extraCount);
        for (int i = 0; i < extraCount; i++)
        {
            shared.AddRef();
            DispatchPayload extra = _payloadPool.Get();
            extras[i] = extra;
            CopyPayload(payload, extra, shared);
        }

        bool firstRejected = !Deliver(dispatchers[0], payload);
        for (int i = 0; i < extraCount; i++)
        {
            if (!Deliver(dispatchers[i + 1], extras[i]))
            {
                _payloadPool.Return(extras[i]);
            }
        }

        ArrayPool<DispatchPayload>.Shared.Return(extras, clearArray: true);
        if (firstRejected)
        {
            _payloadPool.Return(payload);
        }
    }

    /// <summary>
    /// Tenta enfileirar. No fracasso, libera o buffer e avisa a auditoria.
    /// </summary>
    /// <returns><see langword="true"/> quando o dispatcher aceitou o payload.</returns>
    private bool Deliver(IMessageDispatcher? dispatcher, DispatchPayload payload)
    {
        if (dispatcher is not null && dispatcher.TryEnqueue(payload))
        {
            return true;
        }

        ReleaseOwnedBuffer(payload);
        WriteOverflow(payload, dispatcher);
        return false;
    }

    private void WriteOverflow(DispatchPayload payload, IMessageDispatcher? dispatcher)
    {
        string dispatcherId = dispatcher?.DispatcherId ?? string.Empty;
        _logger.LogWarning(
            "Dispatcher {DispatcherId} recusou mensagem da sessão {SessionId}. O channel está cheio.",
            dispatcherId,
            payload.SessionId.Value);

        var record = new AuditRecord
        {
            SessionId = payload.SessionId,
            IngressTimestampTicks = payload.IngressTimestampTicks,
            DeliveryMode = payload.DeliveryMode,
            EgressTimestampTicks = payload.EgressTimestampTicks,
            WasDropped = true,
            DropReason = ChannelFullDropReason,
            DispatcherIds = dispatcherId.Length == 0 ? [] : [dispatcherId],
        };

        if (!_auditWriter.TryWrite(record))
        {
            _logger.LogWarning(
                "Registro de overflow do dispatcher {DispatcherId} não entrou no channel de auditoria.",
                dispatcherId);
        }
    }

    private static void CopyPayload(DispatchPayload source, DispatchPayload destination, IMemoryOwner<byte> buffer)
    {
        destination.SessionId = source.SessionId;
        destination.DeliveryMode = source.DeliveryMode;
        destination.IngressTimestampTicks = source.IngressTimestampTicks;
        destination.RoutingSequence = source.RoutingSequence;
        destination.EgressTimestampTicks = source.EgressTimestampTicks;
        destination.RawBuffer = buffer;
        if (source.TryGetParsedMessage(out FixMessage parsed))
        {
            destination.SetParsedMessage(parsed.MsgType, parsed.Fields);
        }
    }

    private static void ReleaseOwnedBuffer(DispatchPayload payload)
    {
        IMemoryOwner<byte>? buffer = payload.RawBuffer;
        payload.RawBuffer = null;
        if (buffer is RefCountedMemoryOwner counted)
        {
            counted.Release();
            return;
        }

        buffer?.Dispose();
    }
}
