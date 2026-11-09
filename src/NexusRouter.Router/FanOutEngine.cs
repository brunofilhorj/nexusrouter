using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

using Microsoft.Extensions.Logging;

using NexusRouter.Domain.Abstractions;
using NexusRouter.Domain.Enums;
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
/// Cada <see cref="IMessageDispatcher.TryEnqueue"/> que retorna <see langword="false"/> libera o buffer e conta lag.
/// A mensagem já está no log de sessão. Isso não lança exceção.
/// Depois do handoff, o fan-out copia o frame para um <see cref="AuditRecord"/> e faz <c>TryWrite</c>.
/// Essa cópia aloca o registro e o array de <see cref="AuditRecord.RawFix"/>. Não há await nem lock.
/// </remarks>
public sealed class FanOutEngine
{
    private readonly ChannelWriter<AuditRecord> _auditWriter;
    private readonly DispatchPayloadPool _payloadPool;
    private readonly ILogger<FanOutEngine> _logger;
    private readonly NexusRouterMetrics _metrics;

    /// <summary>
    /// Cria o fan-out com o channel de auditoria, o pool de envelopes e a instrumentação.
    /// </summary>
    /// <param name="auditChannel">Channel não bloqueante do cold path.</param>
    /// <param name="payloadPool">Pool usado para os envelopes além do primeiro dispatcher.</param>
    /// <param name="logger">Log de channel cheio.</param>
    /// <param name="metrics">Instrumentação de métricas do hot path.</param>
    /// <exception cref="ArgumentNullException">Alguma dependência é nula.</exception>
    public FanOutEngine(
        Channel<AuditRecord> auditChannel,
        DispatchPayloadPool payloadPool,
        ILogger<FanOutEngine> logger,
        NexusRouterMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(auditChannel);
        ArgumentNullException.ThrowIfNull(payloadPool);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(metrics);

        _auditWriter = auditChannel.Writer;
        _payloadPool = payloadPool;
        _logger = logger;
        _metrics = metrics;
    }

    /// <summary>
    /// Enfileira <paramref name="payload"/> em cada dispatcher da rota.
    /// </summary>
    /// <param name="route">Rota já resolvida. Passada por referência para não copiar o array de dispatchers.</param>
    /// <param name="payload">Envelope preparado pelo router. No sucesso, o dispatcher passa a ser o dono.</param>
    /// <remarks>
    /// Sem await e sem lock.
    /// Rota vazia ou channel cheio libera o buffer, conta lag e segue. A mensagem permanece no log.
    /// O pool de payloads entra no construtor porque o fan-out aluga os envelopes extras.
    /// O snapshot de auditoria aloca um <see cref="AuditRecord"/> e a cópia de <see cref="AuditRecord.RawFix"/>
    /// depois que os dispatchers já receberam o buffer. O sink decide se persiste o bruto.
    /// </remarks>
    public void Dispatch(ref RouteContext route, DispatchPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (route.Dispatchers.IsDefault || route.Dispatchers.Length == 0)
        {
            ReleaseOwnedBuffer(payload);
            RecordLag(dispatcherId: string.Empty, reason: "no_dispatcher");
            _payloadPool.Return(payload);
            _logger.LogDebug(
                "Sessão {SessionId} não tem dispatcher. A mensagem ficou no log.",
                payload.SessionId.Value);
            return;
        }

        IMemoryOwner<byte>? rawBuffer = payload.RawBuffer;
        if (rawBuffer is null)
        {
            _payloadPool.Return(payload);
            _logger.LogWarning(
                "Sessão {SessionId} produziu um payload sem buffer. A mensagem não foi encaminhada.",
                payload.SessionId.Value);
            return;
        }

        RoutedSnapshot snapshot = Capture(payload, rawBuffer);
        ImmutableArray<IMessageDispatcher> dispatchers = route.Dispatchers;
        var accepted = new string[dispatchers.Length];
        int acceptedCount = 0;

        if (dispatchers.Length == 1)
        {
            if (Deliver(dispatchers[0], payload))
            {
                accepted[acceptedCount++] = dispatchers[0].DispatcherId;
            }
            else
            {
                _payloadPool.Return(payload);
            }
        }
        else
        {
            var shared = new RefCountedMemoryOwner(rawBuffer, initialRefCount: 1);
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

            if (Deliver(dispatchers[0], payload))
            {
                accepted[acceptedCount++] = dispatchers[0].DispatcherId;
            }
            else
            {
                _payloadPool.Return(payload);
            }

            for (int i = 0; i < extraCount; i++)
            {
                if (Deliver(dispatchers[i + 1], extras[i]))
                {
                    accepted[acceptedCount++] = dispatchers[i + 1].DispatcherId;
                }
                else
                {
                    _payloadPool.Return(extras[i]);
                }
            }

            ArrayPool<DispatchPayload>.Shared.Return(extras, clearArray: true);
        }

        EnqueueRouted(snapshot, accepted, acceptedCount);
    }

    private void EnqueueRouted(in RoutedSnapshot snapshot, string[] accepted, int acceptedCount)
    {
        string[] dispatcherIds = acceptedCount == accepted.Length
            ? accepted
            : accepted.AsSpan(0, acceptedCount).ToArray();

        var record = new AuditRecord
        {
            SessionId = snapshot.SessionId,
            IngressTimestampTicks = snapshot.IngressTimestampTicks,
            MsgType = snapshot.MsgType,
            ClOrdId = snapshot.ClOrdId,
            MsgSeqNum = snapshot.MsgSeqNum,
            DeliveryMode = snapshot.DeliveryMode,
            DispatcherIds = dispatcherIds,
            EgressTimestampTicks = snapshot.EgressTimestampTicks,
            RawFix = snapshot.RawFix,
        };

        if (!_auditWriter.TryWrite(record))
        {
            _logger.LogWarning(
                "Registro de auditoria da sessão {SessionId} não entrou no channel.",
                snapshot.SessionId.Value);
        }
    }

    private static RoutedSnapshot Capture(DispatchPayload payload, IMemoryOwner<byte> owner)
    {
        ReadOnlySpan<byte> source = owner.Memory.Span;
        var rawFix = new byte[source.Length];
        source.CopyTo(rawFix);
        long? sequence = PassThroughRewriter.TryReadMsgSeqNum(rawFix, out long parsed) ? parsed : null;
        return new RoutedSnapshot(
            payload.SessionId,
            payload.IngressTimestampTicks,
            payload.EgressTimestampTicks,
            payload.DeliveryMode,
            ReadTag(rawFix, "35="u8),
            ReadTag(rawFix, "11="u8),
            sequence,
            rawFix);
    }

    private static string? ReadTag(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> tag)
    {
        int start = 0;
        while (start <= frame.Length)
        {
            int separator = frame[start..].IndexOf((byte)0x01);
            int fieldLength = separator < 0 ? frame.Length - start : separator;
            ReadOnlySpan<byte> field = frame.Slice(start, fieldLength);
            if (field.StartsWith(tag))
            {
                return Encoding.ASCII.GetString(field[tag.Length..]);
            }

            if (separator < 0)
            {
                return null;
            }

            start += fieldLength + 1;
        }

        return null;
    }

    private readonly record struct RoutedSnapshot(
        SessionId SessionId,
        long IngressTimestampTicks,
        long EgressTimestampTicks,
        DeliveryMode DeliveryMode,
        string? MsgType,
        string? ClOrdId,
        long? MsgSeqNum,
        byte[] RawFix);

    /// <summary>
    /// Tenta enfileirar. No fracasso, libera o buffer e conta lag. A mensagem permanece no log.
    /// </summary>
    /// <returns><see langword="true"/> quando o dispatcher aceitou o payload.</returns>
    private bool Deliver(IMessageDispatcher? dispatcher, DispatchPayload payload)
    {
        if (dispatcher is not null && dispatcher.TryEnqueue(payload))
        {
            // Contabiliza despacho com sucesso — TagList é struct, sem alocação.
            TagList tags = new TagList { { "dispatcher_id", dispatcher.DispatcherId } };
            _metrics.MessagesDispatched.Add(1, tags);
            return true;
        }

        ReleaseOwnedBuffer(payload);
        string dispatcherId = dispatcher?.DispatcherId ?? string.Empty;
        RecordLag(dispatcherId, reason: "channel_full");
        _logger.LogWarning(
            "Dispatcher {DispatcherId} recusou mensagem da sessão {SessionId}. O channel está cheio e a mensagem ficou no log.",
            dispatcherId,
            payload.SessionId.Value);
        return false;
    }

    private void RecordLag(string dispatcherId, string reason)
    {
        TagList tags = new TagList
        {
            { "dispatcher_id", dispatcherId },
            { "reason", reason },
        };
        _metrics.DispatcherLagSignaled.Add(1, tags);
    }

    private static void CopyPayload(DispatchPayload source, DispatchPayload destination, IMemoryOwner<byte> buffer)
    {
        destination.SessionId = source.SessionId;
        destination.DeliveryMode = source.DeliveryMode;
        destination.IngressTimestampTicks = source.IngressTimestampTicks;
        destination.RoutingSequence = source.RoutingSequence;
        destination.EgressTimestampTicks = source.EgressTimestampTicks;
        destination.LogRef = source.LogRef;
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
