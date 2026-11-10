using NexusRouter.Domain.Abstractions;
using NexusRouter.Domain.Enums;
using NexusRouter.Domain.Models;

namespace NexusRouter.Egress.Fix;

/// <summary>
/// Plano de reenvio de um <c>ResendRequest</c>: lê o <see cref="IClientIndex"/> entre <c>BeginSeqNo</c> e <c>EndSeqNo</c>.
/// </summary>
/// <remarks>
/// Uma instância atende um pedido. A sessão chama <see cref="TryNext"/> até ele devolver <see langword="false"/>.
/// Slot de aplicação vira <see cref="ResendActionKind.Application"/>. Slot administrativo, slot ausente e slot que ficou
/// sem publicação além do timeout viram <see cref="ResendActionKind.GapFill"/> agrupado. Slot reservado e ainda não
/// publicado vira <see cref="ResendActionKind.Wait"/>. Acima de <see cref="MaxMessagesPerRequest"/> mensagens o plano
/// termina com um gap-fill com <c>PossMissingApplMsg=Y</c> até o fim do pedido (Guidelines §14.6).
/// </remarks>
internal sealed class ResendPlanner
{
    /// <summary>
    /// Limite de mensagens por pedido de reenvio (Guidelines §14.6 da B3).
    /// </summary>
    internal const int MaxMessagesPerRequest = 10_000;

    private readonly IClientIndex _index;
    private readonly long _last;
    private readonly int _maxMessages;
    private readonly long _slotTimeoutTicks;
    private readonly TimeProvider _time;
    private long _cursor;
    private int _planned;
    private long _waitingSeq;
    private long _waitingSince;
    private bool _done;

    /// <summary>
    /// Cria o plano de um pedido.
    /// </summary>
    /// <param name="index">Índice do cliente.</param>
    /// <param name="beginSeqNo">Primeiro seq pedido, a partir de 1.</param>
    /// <param name="endSeqNo">Último seq pedido, inclusive. Valor acima do numerado é cortado em <c>NextOut - 1</c>.</param>
    /// <param name="maxMessages">Limite de mensagens por pedido.</param>
    /// <param name="slotTimeout">Quanto um slot reservado pode ficar sem publicação antes de virar gap.</param>
    /// <param name="time">Relógio. Nulo usa <see cref="TimeProvider.System"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="index"/> é nulo.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="beginSeqNo"/> ou <paramref name="maxMessages"/> não é positivo.</exception>
    internal ResendPlanner(
        IClientIndex index,
        long beginSeqNo,
        long endSeqNo,
        int maxMessages,
        TimeSpan slotTimeout,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(beginSeqNo);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxMessages);
        _index = index;
        _time = time ?? TimeProvider.System;
        _maxMessages = maxMessages;
        _slotTimeoutTicks = (long)(slotTimeout.TotalSeconds * _time.TimestampFrequency);
        _cursor = beginSeqNo;
        _last = Math.Min(endSeqNo, index.NextOut - 1);
    }

    /// <summary>
    /// Devolve a próxima ação.
    /// </summary>
    /// <param name="action">A ação quando o retorno é <see langword="true"/>.</param>
    /// <returns><see langword="false"/> quando o pedido terminou.</returns>
    /// <remarks>
    /// Depois de <see cref="ResendActionKind.Wait"/>, a sessão espera e chama de novo. A ação seguinte reavalia o mesmo slot.
    /// </remarks>
    internal bool TryNext(out ResendAction action)
    {
        action = default;
        if (_done)
        {
            return false;
        }

        long gapStart = 0;
        while (_cursor <= _last)
        {
            if (_planned >= _maxMessages)
            {
                _done = true;
                action = new ResendAction(ResendActionKind.GapFill, gapStart > 0 ? gapStart : _cursor, _last + 1, default, PossMissingApplMsg: true);
                return true;
            }

            IndexReadResult result = _index.TryGet(_cursor, out IndexEntry entry);
            if (result == IndexReadResult.Published && entry.Kind == 1)
            {
                if (gapStart > 0)
                {
                    action = new ResendAction(ResendActionKind.GapFill, gapStart, _cursor, default, PossMissingApplMsg: false);
                    return true;
                }

                action = new ResendAction(ResendActionKind.Application, _cursor, _cursor + 1, entry, PossMissingApplMsg: false);
                _cursor++;
                _planned++;
                return true;
            }

            if (result == IndexReadResult.NotYet && !SlotTimedOut(_cursor))
            {
                if (gapStart > 0)
                {
                    action = new ResendAction(ResendActionKind.GapFill, gapStart, _cursor, default, PossMissingApplMsg: false);
                    return true;
                }

                action = new ResendAction(ResendActionKind.Wait, _cursor, _cursor + 1, default, PossMissingApplMsg: false);
                return true;
            }

            // Administrativo, ausente ou slot que nunca foi publicado: tudo isso entra no mesmo gap.
            if (gapStart == 0)
            {
                gapStart = _cursor;
            }

            _cursor++;
            _planned++;
        }

        _done = true;
        if (gapStart > 0)
        {
            action = new ResendAction(ResendActionKind.GapFill, gapStart, _cursor, default, PossMissingApplMsg: false);
            return true;
        }

        return false;
    }

    private bool SlotTimedOut(long seq)
    {
        long now = _time.GetTimestamp();
        if (_waitingSeq != seq)
        {
            _waitingSeq = seq;
            _waitingSince = now;
            return false;
        }

        return now - _waitingSince >= _slotTimeoutTicks;
    }
}
