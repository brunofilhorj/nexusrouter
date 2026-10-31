using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Threading.Channels;

using Microsoft.Extensions.Logging;

using NexusRouter.Domain.Enums;
using NexusRouter.Domain.Models;
using NexusRouter.Ingress.Sessions;
using NexusRouter.Router;

namespace NexusRouter.Egress.Fix;

/// <summary>
/// Uma sessão FIX aceita. O sequence number e a fila de envio são só desta conexão.
/// </summary>
internal sealed class FixAcceptedSession
{
    private const int AdminMessageBytes = 1024;
    private const int ReadBufferBytes = 64 * 1024;

    private static readonly AsyncLocal<int> WriteLoopDepth = new();

    private readonly TcpClient _client;
    private readonly Stream _stream;
    private readonly SequenceNumberStore _sequence;
    private readonly SessionOptions _options;
    private readonly byte[] _prelude;
    private readonly int _preludeLength;
    private readonly Action<FixAcceptedSession> _onClosed;
    private readonly ILogger _logger;
    private readonly Channel<OutboundFrame> _outbound;
    private readonly SemaphoreSlim _enqueueGate = new(1, 1);
    private readonly TaskCompletionSource _logonReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Pipe? _pipe;
    private CancellationTokenSource? _connectionCts;
    private CancellationTokenSource? _heartbeatCts;
    private Task? _pump;
    private Task? _write;
    private Task? _read;
    private Task? _heartbeat;
    private int _stop;

    /// <summary>
    /// Cria a sessão. O Logon de resposta sai em <see cref="StartAsync"/>.
    /// </summary>
    /// <param name="senderCompId">SenderCompID (tag 49) do cliente.</param>
    /// <param name="client">Socket já aceito.</param>
    /// <param name="stream">Stream do socket, já encapsulado em TLS quando for o caso.</param>
    /// <param name="sequence">Contador desta sessão, já carregado.</param>
    /// <param name="options">Comp IDs desta sessão. Sender é o NexusRouter. Target é o cliente.</param>
    /// <param name="channelCapacity">Tamanho da fila desta sessão.</param>
    /// <param name="heartBtInt">Intervalo do heartbeat, em segundos.</param>
    /// <param name="prelude">Bytes que chegaram no mesmo read do Logon, depois do frame.</param>
    /// <param name="onClosed">Remove a sessão da tabela do dispatcher.</param>
    /// <param name="logger">Log de ciclo de vida. O frame FIX não entra aqui.</param>
    internal FixAcceptedSession(
        string senderCompId,
        TcpClient client,
        Stream stream,
        SequenceNumberStore sequence,
        SessionOptions options,
        int channelCapacity,
        int heartBtInt,
        byte[] prelude,
        int preludeLength,
        Action<FixAcceptedSession> onClosed,
        ILogger logger)
    {
        SenderCompId = senderCompId;
        _client = client;
        _stream = stream;
        _sequence = sequence;
        _options = options;
        HeartBtInt = heartBtInt;
        _prelude = prelude;
        _preludeLength = preludeLength;
        _onClosed = onClosed;
        _logger = logger;
        _outbound = Channel.CreateBounded<OutboundFrame>(new BoundedChannelOptions(channelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    }

    /// <summary>
    /// SenderCompID do cliente.
    /// </summary>
    internal string SenderCompId { get; }

    /// <summary>
    /// HeartBtInt usado no timer desta sessão.
    /// </summary>
    private int HeartBtInt { get; }

    /// <summary>
    /// Envia o Logon de resposta e passa a ler e escrever nesta conexão.
    /// </summary>
    /// <param name="cancellationToken">Cancela o Logon de resposta.</param>
    /// <returns>Tarefa concluída quando a sessão está pronta para receber mensagens de aplicação.</returns>
    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _stop) != 0)
        {
            return;
        }

        _pipe = new Pipe(new PipeOptions(useSynchronizationContext: false));
        // O token do accept só cancela o Logon. A sessão fica viva até StopAsync.
        _connectionCts = new CancellationTokenSource();
        _pump = PumpAsync(_pipe.Reader, _stream, _connectionCts.Token);
        try
        {
            await SendLogonAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _connectionCts?.Cancel();
            _pipe?.Writer.Complete();
            _logonReady.TrySetCanceled(cancellationToken);
            throw;
        }

        _logonReady.TrySetResult();

        _write = WriteLoopAsync();
        _read = ReadLoopAsync(_connectionCts.Token);
        _heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(_connectionCts.Token);
        _heartbeat = HeartbeatLoopAsync(_heartbeatCts.Token);
        _logger.LogInformation(
            "Sessão FIX do cliente {SenderCompId} aceita. CompID local {LocalCompId}.",
            SenderCompId,
            _options.SenderCompId);
    }

    /// <summary>
    /// Envia Logout, grava o sequence number e fecha o socket.
    /// </summary>
    /// <param name="cancellationToken">Abandona a espera do loop de escrita e a gravação do sequence number.</param>
    /// <returns>Tarefa concluída quando a sessão parou.</returns>
    internal async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stop, 1) != 0)
        {
            return;
        }

        _onClosed(this);
        _logonReady.TrySetCanceled(cancellationToken);
        _heartbeatCts?.Cancel();
        if (WriteLoopDepth.Value != 0)
        {
            _outbound.Writer.TryComplete();
            _connectionCts?.Cancel();
            await CleanupAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        _outbound.Writer.TryComplete();
        if (_write is not null)
        {
            try
            {
                await _write.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                _connectionCts?.Cancel();
            }
        }

        await SendLogoutDirectAsync().ConfigureAwait(false);

        try
        {
            _pipe?.Writer.Complete();
        }
        catch (InvalidOperationException)
        {
            // O pump ou uma falha anterior já completou o pipe.
        }

        if (_pump is not null)
        {
            try
            {
                await _pump.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                _logger.LogDebug(ex, "Sessão FIX {SenderCompId} não drenou o socket.", SenderCompId);
            }
        }

        _connectionCts?.Cancel();
        await CleanupAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Copia o frame com o MsgSeqNum desta sessão e enfileira. Não bloqueia no socket.
    /// </summary>
    /// <param name="payload">Mensagem do dispatcher. O buffer continua com o chamador.</param>
    /// <returns><see langword="false"/> quando a sessão está parando ou a fila desta sessão está cheia.</returns>
    internal bool TryEnqueue(DispatchPayload payload)
    {
        if (Volatile.Read(ref _stop) != 0)
        {
            return false;
        }

        if (payload.RawBuffer is null)
        {
            throw new InvalidOperationException("O payload FIX outbound não tem buffer.");
        }

        if (!WaitForLogon() || !TryEnterGate())
        {
            return false;
        }

        byte[]? rented = null;
        try
        {
            if (Volatile.Read(ref _stop) != 0)
            {
                return false;
            }

            long sequence = _sequence.Next();
            int capacity = payload.DeliveryMode == DeliveryMode.Parse
                ? Math.Max(payload.RawBuffer.Memory.Length + 128, AdminMessageBytes)
                : Math.Max(payload.RawBuffer.Memory.Length, 1);
            rented = ArrayPool<byte>.Shared.Rent(capacity);
            int written = Prepare(payload, sequence, rented);
            if (!_outbound.Writer.TryWrite(new OutboundFrame(rented, written)))
            {
                ArrayPool<byte>.Shared.Return(rented, clearArray: true);
                rented = null;
                return false;
            }

            rented = null;
            return true;
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            }

            _enqueueGate.Release();
        }
    }

    private async Task SendLogoutDirectAsync()
    {
        if (_pipe is null)
        {
            return;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(AdminMessageBytes);
        try
        {
            long sequence = _sequence.Next();
            int written = FixMessageBuilder.BuildLogout(rented, _options, sequence).Length;
            await WritePipeAsync(rented.AsMemory(0, written), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Sessão FIX {SenderCompId} não enviou Logout.", SenderCompId);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    private async Task SendLogonAsync(CancellationToken cancellationToken)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(AdminMessageBytes);
        try
        {
            long sequence = _sequence.Next();
            int written = FixMessageBuilder.BuildLogon(rented, _options, sequence).Length;
            await WritePipeAsync(rented.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    private int Prepare(DispatchPayload payload, long sequence, byte[] destination)
    {
        switch (payload.DeliveryMode)
        {
            case DeliveryMode.PassThrough:
            {
                IMemoryOwner<byte> raw = payload.RawBuffer
                    ?? throw new InvalidOperationException("O payload FIX outbound não tem buffer.");
                ReadOnlyMemory<byte> source = raw.Memory;
                source.Span.CopyTo(destination);
                if (!PassThroughRewriter.Rewrite(destination.AsSpan(0, source.Length), sequence))
                {
                    throw new InvalidOperationException("O MsgSeqNum outbound não coube no campo 34.");
                }

                return source.Length;
            }

            case DeliveryMode.Parse:
            {
                if (!payload.TryGetParsedMessage(out FixMessage message))
                {
                    throw new InvalidOperationException("O payload em modo Parse não tem mensagem tokenizada.");
                }

                return FixMessageSerializer.Serialize(
                    in message,
                    destination,
                    sequence,
                    _options.SenderCompId,
                    _options.TargetCompId);
            }

            default:
                throw new InvalidOperationException("Modo de entrega desconhecido.");
        }
    }

    private bool TryEnqueueAdmin(AdminKind kind, ReadOnlyMemory<byte> testReqId, bool ignoreStop)
    {
        if (!ignoreStop && Volatile.Read(ref _stop) != 0)
        {
            return false;
        }

        if (!TryEnterGate())
        {
            return false;
        }

        byte[]? rented = null;
        try
        {
            long sequence = _sequence.Next();
            rented = ArrayPool<byte>.Shared.Rent(AdminMessageBytes);
            int written = kind switch
            {
                AdminKind.Heartbeat => FixMessageBuilder.BuildHeartbeat(rented, _options, sequence, testReqId.Span).Length,
                AdminKind.Logout => FixMessageBuilder.BuildLogout(rented, _options, sequence).Length,
                _ => throw new InvalidOperationException("Mensagem administrativa desconhecida."),
            };
            if (!_outbound.Writer.TryWrite(new OutboundFrame(rented, written)))
            {
                ArrayPool<byte>.Shared.Return(rented, clearArray: true);
                rented = null;
                return false;
            }

            rented = null;
            return true;
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            }

            _enqueueGate.Release();
        }
    }

    private void TryEnqueueGapFill(long beginSeqNo, long newSeqNo)
    {
        if (Volatile.Read(ref _stop) != 0 || !TryEnterGate())
        {
            return;
        }

        byte[]? rented = null;
        try
        {
            rented = ArrayPool<byte>.Shared.Rent(AdminMessageBytes);
            int written = FixMessageBuilder.BuildSequenceReset(rented, _options, beginSeqNo, newSeqNo, gapFill: true).Length;
            if (!_outbound.Writer.TryWrite(new OutboundFrame(rented, written)))
            {
                ArrayPool<byte>.Shared.Return(rented, clearArray: true);
                rented = null;
            }
            else
            {
                rented = null;
            }
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            }

            _enqueueGate.Release();
        }
    }

    private bool WaitForLogon()
    {
        Task ready = _logonReady.Task;
        if (ready.IsCompletedSuccessfully)
        {
            return true;
        }

        CancellationToken token = _connectionCts?.Token ?? CancellationToken.None;
        try
        {
            ready.Wait(token);
            return ready.IsCompletedSuccessfully;
        }
        catch (Exception ex) when (ex is OperationCanceledException or AggregateException)
        {
            return false;
        }
    }

    private bool TryEnterGate()
    {
        CancellationToken token = _connectionCts?.Token ?? CancellationToken.None;
        try
        {
            _enqueueGate.Wait(token);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task WriteLoopAsync()
    {
        WriteLoopDepth.Value = 1;
        try
        {
            await foreach (OutboundFrame frame in _outbound.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    await WritePipeAsync(frame.Buffer.AsMemory(0, frame.Length), CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(frame.Buffer, clearArray: true);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DrainOutbound();
            _logger.LogWarning(ex, "Sessão FIX {SenderCompId} interrompeu a escrita.", SenderCompId);
            RequestStop();
        }
        finally
        {
            DrainOutbound();
            WriteLoopDepth.Value = 0;
        }
    }

    private async ValueTask WritePipeAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        PipeWriter writer = _pipe?.Writer ?? throw new IOException("A sessão FIX aceita não está conectada.");
        FlushResult result = await writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        if (result.IsCompleted || result.IsCanceled)
        {
            throw new IOException("O pipe da sessão foi encerrado.");
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(HeartBtInt));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                TryEnqueueAdmin(AdminKind.Heartbeat, ReadOnlyMemory<byte>.Empty, ignoreStop: false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sessão FIX {SenderCompId} interrompeu o heartbeat.", SenderCompId);
        }
    }

    private async Task PumpAsync(PipeReader reader, Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = result.Buffer;
                foreach (ReadOnlyMemory<byte> segment in buffer)
                {
                    await stream.WriteAsync(segment, cancellationToken).ConfigureAwait(false);
                }

                if (!buffer.IsEmpty)
                {
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                reader.AdvanceTo(buffer.End);
                if (result.IsCompleted || result.IsCanceled)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Sessão FIX {SenderCompId} encerrou o pump.", SenderCompId);
        }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ReadBufferBytes);
        int count = 0;
        if (_preludeLength > 0)
        {
            _prelude.AsSpan(0, _preludeLength).CopyTo(buffer);
            count = _preludeLength;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                while (FixWireFrame.TryTakeFrame(buffer.AsSpan(0, count), out int frameLength))
                {
                    bool keep = HandleFrame(buffer.AsSpan(0, frameLength));
                    int remaining = count - frameLength;
                    if (remaining > 0)
                    {
                        Buffer.BlockCopy(buffer, frameLength, buffer, 0, remaining);
                    }

                    count = remaining;
                    if (!keep)
                    {
                        return;
                    }
                }

                if (count == buffer.Length)
                {
                    throw new InvalidOperationException("A mensagem recebida na sessão aceita excedeu o buffer.");
                }

                int read = await _stream.ReadAsync(buffer.AsMemory(count), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new IOException("O cliente fechou a sessão FIX.");
                }

                count += read;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Sessão FIX {SenderCompId} encerrou a leitura.", SenderCompId);
            RequestStop();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private bool HandleFrame(ReadOnlySpan<byte> frame)
    {
        if (!FixWireFrame.TryReadTag(frame, 35, out ReadOnlySpan<byte> msgType))
        {
            return true;
        }

        if (msgType.Length == 1 && msgType[0] == (byte)'5')
        {
            RequestStop();
            return false;
        }

        if (msgType.Length == 1 && msgType[0] == (byte)'1' && FixWireFrame.TryReadTag(frame, 112, out ReadOnlySpan<byte> testReqId))
        {
            byte[] copy = testReqId.ToArray();
            TryEnqueueAdmin(AdminKind.Heartbeat, copy, ignoreStop: false);
            return true;
        }

        if (msgType.Length == 1 && msgType[0] == (byte)'2')
        {
            HandleResendRequest(frame);
        }

        return true;
    }

    private void HandleResendRequest(ReadOnlySpan<byte> frame)
    {
        if (!FixWireFrame.TryReadTag(frame, 7, out ReadOnlySpan<byte> beginRaw)
            || !FixWireFrame.TryParseNonNegative(beginRaw, out long beginSeqNo)
            || beginSeqNo <= 0)
        {
            return;
        }

        long newSeqNo = _sequence.NextExpected;
        if (beginSeqNo >= newSeqNo)
        {
            return;
        }

        TryEnqueueGapFill(beginSeqNo, newSeqNo);
    }

    private void RequestStop() => _ = StopAsync(CancellationToken.None);

    private void DrainOutbound()
    {
        while (_outbound.Reader.TryRead(out OutboundFrame frame))
        {
            ArrayPool<byte>.Shared.Return(frame.Buffer, clearArray: true);
        }
    }

    private async Task CleanupAsync(CancellationToken cancellationToken)
    {
        Task[] pending = Collect(_read, _pump, _heartbeat);
        if (pending.Length > 0)
        {
            try
            {
                await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or AggregateException)
            {
                _logger.LogDebug(ex, "Sessão FIX {SenderCompId} encerrou as tasks.", SenderCompId);
            }
        }

        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }

        _client.Dispose();
        _connectionCts?.Dispose();
        _heartbeatCts?.Dispose();
        await _sequence.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Task[] Collect(params Task?[] tasks)
    {
        int count = 0;
        for (int i = 0; i < tasks.Length; i++)
        {
            if (tasks[i] is not null)
            {
                count++;
            }
        }

        if (count == 0)
        {
            return [];
        }

        var pending = new Task[count];
        int index = 0;
        for (int i = 0; i < tasks.Length; i++)
        {
            if (tasks[i] is Task task)
            {
                pending[index++] = task;
            }
        }

        return pending;
    }

    private readonly struct OutboundFrame
    {
        internal OutboundFrame(byte[] buffer, int length)
        {
            Buffer = buffer;
            Length = length;
        }

        internal byte[] Buffer { get; }

        internal int Length { get; }
    }

    private enum AdminKind
    {
        Heartbeat,
        Logout,
    }
}
