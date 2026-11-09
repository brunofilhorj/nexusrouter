using System.Buffers;

using NexusRouter.Domain.Abstractions;
using NexusRouter.Domain.Enums;
using NexusRouter.Domain.Models;
using NexusRouter.Domain.Pooling;

namespace NexusRouter.Egress;

/// <summary>
/// Cursor por stream na task de consumo. Compara o seq do payload com o próximo ainda não entregue.
/// </summary>
/// <remarks>
/// Roda só na task de consumo. Lê o log até o <c>committed</c>. Não bloqueia a thread da sessão.
/// </remarks>
public sealed class StreamCatchUp
{
    private const int MaxStreams = 256;

    private readonly ISessionLogReader _reader;
    private readonly IConsumerState _state;
    private readonly DispatchPayloadPool _pool;
    private readonly RentedBufferWriter _writer = new();
    private readonly long[] _expected = new long[MaxStreams];
    private readonly int[] _epochs = new int[MaxStreams];
    private readonly SessionId[] _sessionIds = new SessionId[MaxStreams];
    private readonly byte[] _tracked = new byte[MaxStreams];
    private readonly ushort[] _seen = new ushort[MaxStreams];
    private int _seenCount;

    /// <summary>
    /// Cria o cursor e recarrega os streams já persistidos.
    /// </summary>
    /// <param name="reader">Leitor do log de ingress.</param>
    /// <param name="state">Estado durável deste dispatcher.</param>
    /// <param name="pool">Pool dos envelopes usados no replay.</param>
    /// <param name="catchUpIdleMs">Espera ociosa, em milissegundos, antes de comparar o cursor com o <c>committed</c>.</param>
    /// <exception cref="ArgumentNullException">Alguma dependência é nula.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="catchUpIdleMs"/> não é positivo.</exception>
    public StreamCatchUp(ISessionLogReader reader, IConsumerState state, DispatchPayloadPool pool, int catchUpIdleMs)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(catchUpIdleMs);

        _reader = reader;
        _state = state;
        _pool = pool;
        IdleMilliseconds = catchUpIdleMs;
        Array.Fill(_epochs, -1);
        int epoch = (int)state.Epoch;
        for (ushort stream = 1; stream < MaxStreams; stream++)
        {
            long expected = state.GetExpected(stream);
            if (expected <= 0)
            {
                continue;
            }

            Volatile.Write(ref _expected[stream], expected);
            Volatile.Write(ref _epochs[stream], epoch);
            Track(stream);
        }
    }

    /// <summary>
    /// Intervalo ocioso configurado, em milissegundos.
    /// </summary>
    public int IdleMilliseconds { get; }

    /// <summary>
    /// Mensagens já publicadas no log e ainda não entregues por este dispatcher.
    /// </summary>
    /// <remarks>Zero allocation. A leitura do gauge pode chamar da thread de métricas.</remarks>
    public long Lag
    {
        get
        {
            long lag = 0;
            int count = Volatile.Read(ref _seenCount);
            for (int i = 0; i < count; i++)
            {
                ushort stream = _seen[i];
                int epoch = Volatile.Read(ref _epochs[stream]);
                if (epoch < 0)
                {
                    continue;
                }

                long expected = Volatile.Read(ref _expected[stream]);
                if (expected <= 0)
                {
                    expected = 1;
                }

                long committed = _reader.GetCommitted(stream, epoch);
                if (committed >= expected)
                {
                    lag += committed - expected + 1;
                }
            }

            return lag;
        }
    }

    /// <summary>
    /// Entrega o que o channel pulou e, em seguida, o payload atual quando o cursor ainda não passou por ele.
    /// </summary>
    /// <param name="payload">Mensagem tirada do channel. O chamador continua dono do buffer.</param>
    /// <param name="send">Envio da task de consumo.</param>
    /// <param name="cancellationToken">Cancela o replay quando o dispatcher está parando.</param>
    /// <returns>Tarefa concluída quando o cursor passou deste payload ou quando ele foi ignorado por já ter sido entregue.</returns>
    public async ValueTask AcceptAsync(
        DispatchPayload payload,
        Func<DispatchPayload, CancellationToken, ValueTask> send,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(send);
        LogRef logRef = payload.LogRef;
        if (logRef.InSeq <= 0 || logRef.Stream is 0 or >= MaxStreams)
        {
            await send(payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        ushort stream = logRef.Stream;
        Track(stream);
        _sessionIds[stream] = payload.SessionId;
        int epoch = logRef.Epoch;
        int current = Volatile.Read(ref _epochs[stream]);
        if (current >= 0 && epoch > current)
        {
            await DrainEpochAsync(stream, current, _sessionIds[stream], send, cancellationToken).ConfigureAwait(false);
            SetCursor(stream, epoch, 1);
        }
        else if (current < 0)
        {
            Volatile.Write(ref _epochs[stream], epoch);
        }
        else if (epoch < current)
        {
            return;
        }

        long expected = Volatile.Read(ref _expected[stream]);
        if (expected <= 0)
        {
            expected = 1;
        }

        long sequence = logRef.InSeq;
        if (sequence < expected)
        {
            return;
        }

        if (sequence > expected)
        {
            await ReplayAsync(stream, epoch, expected, sequence, _sessionIds[stream], send, cancellationToken).ConfigureAwait(false);
        }

        await send(payload, cancellationToken).ConfigureAwait(false);
        SetCursor(stream, epoch, sequence + 1);
    }

    /// <summary>
    /// Entrega o intervalo já publicado no log e ainda não coberto pelo cursor.
    /// </summary>
    /// <param name="send">Envio da task de consumo.</param>
    /// <param name="cancellationToken">Cancela o replay quando o dispatcher está parando.</param>
    /// <returns>Tarefa concluída quando cada stream visto alcançou o <c>committed</c> da época corrente.</returns>
    public async ValueTask DrainIdleAsync(
        Func<DispatchPayload, CancellationToken, ValueTask> send,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(send);
        int count = Volatile.Read(ref _seenCount);
        for (int i = 0; i < count; i++)
        {
            ushort stream = _seen[i];
            int epoch = Volatile.Read(ref _epochs[stream]);
            if (epoch < 0)
            {
                continue;
            }

            long expected = Volatile.Read(ref _expected[stream]);
            if (expected <= 0)
            {
                expected = 1;
            }

            long committed = _reader.GetCommitted(stream, epoch);
            if (committed < expected)
            {
                continue;
            }

            await ReplayAsync(stream, epoch, expected, committed + 1, _sessionIds[stream], send, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask DrainEpochAsync(
        ushort stream,
        int epoch,
        SessionId sessionId,
        Func<DispatchPayload, CancellationToken, ValueTask> send,
        CancellationToken cancellationToken)
    {
        long expected = Volatile.Read(ref _expected[stream]);
        if (expected <= 0)
        {
            expected = 1;
        }

        long committed = _reader.GetCommitted(stream, epoch);
        if (committed < expected)
        {
            return;
        }

        await ReplayAsync(stream, epoch, expected, committed + 1, sessionId, send, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ReplayAsync(
        ushort stream,
        int epoch,
        long from,
        long toExclusive,
        SessionId sessionId,
        Func<DispatchPayload, CancellationToken, ValueTask> send,
        CancellationToken cancellationToken)
    {
        long committed = _reader.GetCommitted(stream, epoch);
        long end = toExclusive;
        if (end > committed + 1)
        {
            end = committed + 1;
        }

        for (long sequence = from; sequence < end; sequence++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DeliverStoredAsync(stream, epoch, sequence, sessionId, send, cancellationToken).ConfigureAwait(false);
            SetCursor(stream, epoch, sequence + 1);
        }
    }

    private async ValueTask DeliverStoredAsync(
        ushort stream,
        int epoch,
        long sequence,
        SessionId sessionId,
        Func<DispatchPayload, CancellationToken, ValueTask> send,
        CancellationToken cancellationToken)
    {
        _writer.Reset();
        if (!_reader.TryRead(stream, epoch, sequence, _writer))
        {
            _writer.Reset();
            return;
        }

        ArrayPoolMemoryOwner<byte> owner = _writer.Detach();
        DispatchPayload replay = _pool.Get();
        try
        {
            replay.SessionId = sessionId;
            replay.DeliveryMode = DeliveryMode.PassThrough;
            replay.RawBuffer = owner;
            replay.LogRef = new LogRef(stream, epoch, sequence);
            await send(replay, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            replay.RawBuffer = null;
            owner.Dispose();
            _pool.Return(replay);
        }
    }

    private void SetCursor(ushort stream, int epoch, long next)
    {
        Volatile.Write(ref _expected[stream], next);
        Volatile.Write(ref _epochs[stream], epoch);
        if (_state.Epoch != epoch)
        {
            _state.Epoch = epoch;
        }

        _state.SetExpected(stream, next);
    }

    private void Track(ushort stream)
    {
        if (_tracked[stream] != 0)
        {
            return;
        }

        _tracked[stream] = 1;
        int index = _seenCount;
        _seen[index] = stream;
        Volatile.Write(ref _seenCount, index + 1);
    }

    private sealed class RentedBufferWriter : IBufferWriter<byte>
    {
        private byte[]? _buffer;
        private int _written;

        public void Advance(int count)
        {
            if (count < 0 || _buffer is null || _written > _buffer.Length - count)
            {
                throw new ArgumentOutOfRangeException(nameof(count), count, "O avanço passa do buffer alugado.");
            }

            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            int start = _written;
            Span<byte> span = GetSpan(sizeHint);
            byte[] buffer = _buffer ?? throw new InvalidOperationException("O buffer de replay não foi alugado.");
            return buffer.AsMemory(start, span.Length);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            int needed = _written + Math.Max(sizeHint, 1);
            if (_buffer is null || _buffer.Length < needed)
            {
                byte[] rented = ArrayPool<byte>.Shared.Rent(needed);
                if (_buffer is not null)
                {
                    _buffer.AsSpan(0, _written).CopyTo(rented);
                    ArrayPool<byte>.Shared.Return(_buffer);
                }

                _buffer = rented;
            }

            return _buffer.AsSpan(_written);
        }

        public ArrayPoolMemoryOwner<byte> Detach()
        {
            byte[] buffer = _buffer ?? ArrayPool<byte>.Shared.Rent(1);
            int written = _written;
            _buffer = null;
            _written = 0;
            return new ArrayPoolMemoryOwner<byte>(buffer, written);
        }

        public void Reset()
        {
            if (_buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = null;
            }

            _written = 0;
        }
    }
}
