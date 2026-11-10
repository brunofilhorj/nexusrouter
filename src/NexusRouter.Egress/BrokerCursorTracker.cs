using NexusRouter.Domain.Abstractions;
using NexusRouter.Domain.Models;

namespace NexusRouter.Egress;

/// <summary>
/// Marca-d'água contígua dos envios em voo de um dispatcher de broker.
/// </summary>
/// <remarks>
/// Confirmação fora de ordem fica pendente. O cursor durável só avança até o maior seq
/// com todos os anteriores confirmados. Roda na task de consumo.
/// </remarks>
public sealed class BrokerCursorTracker : IConsumerState
{
    private readonly IConsumerState _inner;
    private readonly CursorAckMode _mode;
    private readonly Lane?[] _lanes = new Lane?[256];

    /// <summary>
    /// Cria o rastreador sobre o cursor já persistido.
    /// </summary>
    /// <param name="inner">Estado durável deste dispatcher.</param>
    /// <param name="mode">Quando a confirmação conta para a marca-d'água.</param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> é nulo.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> não foi declarado.</exception>
    public BrokerCursorTracker(IConsumerState inner, CursorAckMode mode)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "CursorAckMode é desconhecido.");
        }

        _inner = inner;
        _mode = mode;
    }

    /// <inheritdoc />
    public long NextOut => _inner.NextOut;

    /// <inheritdoc />
    public long NextInExpected
    {
        get => _inner.NextInExpected;
        set => _inner.NextInExpected = value;
    }

    /// <inheritdoc />
    public long Epoch
    {
        get => _inner.Epoch;
        set => _inner.Epoch = value;
    }

    /// <inheritdoc />
    public long GetExpected(ushort stream) => _inner.GetExpected(stream);

    /// <inheritdoc />
    public void SetExpected(ushort stream, long inSeq)
    {
        if (stream is 0 or >= 256)
        {
            _inner.SetExpected(stream, inSeq);
            return;
        }

        if (inSeq <= 1)
        {
            ResetLane(stream, (int)Epoch);
            _inner.SetCursor(stream, 0);
            _inner.SetExpected(stream, inSeq < 1 ? 1 : inSeq);
            return;
        }

        if (_mode == CursorAckMode.OnEnqueue)
        {
            var sent = new LogRef(stream, (int)Epoch, inSeq - 1);
            Track(sent);
            Confirm(sent);
            return;
        }

        PublishResume(stream, LaneFor(stream, (int)Epoch));
    }

    /// <inheritdoc />
    public long Cursor(ushort stream) => _inner.Cursor(stream);

    /// <inheritdoc />
    public void SetCursor(ushort stream, long value)
    {
        _inner.SetCursor(stream, value);
        Lane? lane = stream is > 0 and < 256 ? _lanes[stream] : null;
        if (lane is not null && lane.Epoch == (int)_inner.Epoch && value > lane.Watermark)
        {
            lane.Watermark = value;
        }
    }

    /// <summary>
    /// Registra um envio em voo. Em <see cref="CursorAckMode.OnBrokerAck"/> publica o ponto de retomada
    /// sem atravessar este seq, para um restart reentregar o que ainda não foi confirmado.
    /// </summary>
    /// <param name="logRef">Posição enviada ao cliente do broker.</param>
    public void Track(LogRef logRef)
    {
        if (!CanTrack(logRef))
        {
            return;
        }

        Lane lane = LaneFor(logRef.Stream, logRef.Epoch);
        (lane.InFlight ??= []).Add(logRef.InSeq);
        if (_mode == CursorAckMode.OnBrokerAck)
        {
            PublishResume(logRef.Stream, lane);
        }
    }

    /// <summary>
    /// Confirma um seq. A marca-d'água só anda enquanto o próximo seq também já foi confirmado.
    /// </summary>
    /// <param name="logRef">Posição confirmada. Precisa ter passado por <see cref="Track"/>.</param>
    /// <returns>O maior seq contíguo confirmado depois desta chamada.</returns>
    public long Confirm(LogRef logRef)
    {
        if (!CanTrack(logRef))
        {
            return 0;
        }

        Lane lane = LaneFor(logRef.Stream, logRef.Epoch);
        if (lane.InFlight is null || !lane.InFlight.Remove(logRef.InSeq))
        {
            return lane.Watermark;
        }

        if (logRef.InSeq <= lane.Watermark)
        {
            return lane.Watermark;
        }

        (lane.Pending ??= []).Add(logRef.InSeq);
        long before = lane.Watermark;
        while (lane.Pending.Remove(lane.Watermark + 1))
        {
            lane.Watermark++;
        }

        if (lane.Watermark != before)
        {
            _inner.SetCursor(logRef.Stream, lane.Watermark);
            _inner.SetExpected(logRef.Stream, lane.Watermark + 1);
        }

        return lane.Watermark;
    }

    private void PublishResume(ushort stream, Lane lane)
    {
        long resume = lane.Watermark + 1;
        if (resume < 1)
        {
            resume = 1;
        }

        if (_inner.GetExpected(stream) != resume)
        {
            _inner.SetExpected(stream, resume);
        }
    }

    private void ResetLane(ushort stream, int epoch)
    {
        Lane lane = _lanes[stream] ??= new Lane();
        lane.Epoch = epoch;
        lane.Watermark = 0;
        lane.InFlight?.Clear();
        lane.Pending?.Clear();
    }

    private Lane LaneFor(ushort stream, int epoch)
    {
        Lane lane = _lanes[stream] ??= new Lane { Epoch = int.MinValue };
        if (lane.Epoch == epoch)
        {
            return lane;
        }

        lane.Epoch = epoch;
        lane.InFlight?.Clear();
        lane.Pending?.Clear();
        lane.Watermark = _inner.Epoch == epoch ? _inner.Cursor(stream) : 0;
        return lane;
    }

    private static bool CanTrack(LogRef logRef) =>
        logRef.InSeq > 0 && logRef.Stream is > 0 and < 256;

    private sealed class Lane
    {
        public int Epoch;
        public long Watermark;
        public HashSet<long>? InFlight;
        public HashSet<long>? Pending;
    }
}
