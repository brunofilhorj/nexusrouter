using Microsoft.Extensions.Logging;

using NexusRouter.Domain.Models;
using NexusRouter.Domain.Pooling;
using NexusRouter.Egress.Fix;

namespace NexusRouter.Egress;

/// <summary>
/// Compartilha o filtro FIX e o avanço do cursor entre dispatchers de broker.
/// </summary>
/// <typeparam name="TOptions">Options da implementação do broker.</typeparam>
public abstract class BrokerDispatcherBase<TOptions> : DispatcherBase<TOptions>
    where TOptions : class
{
    private readonly CursorAckMode _cursorAckMode;
    private readonly BrokerCursorTracker? _tracker;

    /// <summary>
    /// Cria um dispatcher de broker com o modo de confirmação e o tracker do cursor.
    /// </summary>
    /// <param name="dispatcherId">Nome único do dispatcher.</param>
    /// <param name="options">Options já validadas pela implementação.</param>
    /// <param name="channelCapacity">Quantidade máxima de payloads no channel.</param>
    /// <param name="payloadPool">Pool compartilhado de envelopes.</param>
    /// <param name="logger">Log do loop de consumo.</param>
    /// <param name="cursorAckMode">Quando o cursor durável avança.</param>
    /// <param name="tracker">Tracker do cursor, ou nulo para não persistir confirmação.</param>
    /// <param name="catchUp">Cursor do log. Nulo desliga o catch-up.</param>
    protected BrokerDispatcherBase(
        string dispatcherId,
        TOptions options,
        int channelCapacity,
        DispatchPayloadPool payloadPool,
        ILogger logger,
        CursorAckMode cursorAckMode,
        BrokerCursorTracker? tracker,
        StreamCatchUp? catchUp = null)
        : base(dispatcherId, options, channelCapacity, payloadPool, logger, catchUp)
    {
        _cursorAckMode = cursorAckMode;
        _tracker = tracker;
    }

    /// <summary>
    /// Informa se o frame contém uma mensagem FIX administrativa.
    /// </summary>
    /// <param name="frame">Frame FIX completo.</param>
    /// <returns><see langword="true"/> quando a tag 35 identifica uma mensagem administrativa.</returns>
    protected static bool IsSessionAdmin(ReadOnlySpan<byte> frame) =>
        FixWireFrame.TryReadTag(frame, 35, out ReadOnlySpan<byte> msgType)
        && FixAdminMessageTypes.IsAdmin(msgType);

    /// <summary>
    /// Marca como concluído o payload que não foi publicado por ser administrativo.
    /// </summary>
    /// <param name="payload">Payload ignorado.</param>
    protected void AcknowledgeSkipped(DispatchPayload payload)
    {
        if (_cursorAckMode != CursorAckMode.OnBrokerAck)
        {
            return;
        }

        _tracker?.Track(payload.LogRef);
        _tracker?.Confirm(payload.LogRef);
    }

    /// <summary>
    /// Registra o início de uma publicação que aguarda confirmação do broker.
    /// </summary>
    /// <param name="payload">Payload enviado.</param>
    protected void TrackForBrokerAck(DispatchPayload payload)
    {
        if (_cursorAckMode == CursorAckMode.OnBrokerAck)
        {
            _tracker?.Track(payload.LogRef);
        }
    }

    /// <summary>
    /// Confirma uma publicação concluída pelo broker.
    /// </summary>
    /// <param name="payload">Payload confirmado.</param>
    protected void ConfirmBrokerAck(DispatchPayload payload)
    {
        if (_cursorAckMode == CursorAckMode.OnBrokerAck)
        {
            _tracker?.Confirm(payload.LogRef);
        }
    }
}
