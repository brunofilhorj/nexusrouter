using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

using Microsoft.Extensions.Logging;

using NexusRouter.Domain.Abstractions;
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
    private readonly IClientIndex _index;
    private readonly IConsumerState _state;
    private readonly AcceptorSessionPlan _plan;
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
    private int _logoutSent;
    private int _logoutPhase;
    private int _testRequestOutstanding;
    private long _lastInboundTicks;

    /// <summary>
    /// Cria a sessão. O Logon de resposta sai em <see cref="StartAsync"/>.
    /// </summary>
    /// <param name="senderCompId">SenderCompID (tag 49) do cliente.</param>
    /// <param name="client">Socket já aceito.</param>
    /// <param name="stream">Stream do socket, já encapsulado em TLS quando for o caso.</param>
    /// <param name="index">Outbound sequence and client index for this SenderCompID.</param>
    /// <param name="plan">Política de entrada, silêncio e recuperação desta sessão.</param>
    /// <param name="options">Comp IDs desta sessão. Sender é o NexusRouter. Target é o cliente.</param>
    /// <param name="channelCapacity">Tamanho da fila desta sessão.</param>
    /// <param name="prelude">Bytes que chegaram no mesmo read do Logon, depois do frame.</param>
    /// <param name="onClosed">Remove a sessão da tabela do dispatcher.</param>
    /// <param name="logger">Log de ciclo de vida. O frame FIX não entra aqui.</param>
    internal FixAcceptedSession(
        string senderCompId,
        TcpClient client,
        Stream stream,
        IClientIndex index,
        AcceptorSessionPlan plan,
        SessionOptions options,
        int channelCapacity,
        byte[] prelude,
        int preludeLength,
        Action<FixAcceptedSession> onClosed,
        ILogger logger)
    {
        SenderCompId = senderCompId;
        _client = client;
        _stream = stream;
        _index = index;
        _plan = plan;
        _state = plan.State;
        _options = options;
        HeartBtInt = plan.HeartBtInt;
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
        NoteInbound();
        if (_plan.RejectText is not null)
        {
            await SendLogoutAsync(Encoding.ASCII.GetBytes(_plan.RejectText), cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _logoutSent, 1);
            Volatile.Write(ref _logoutPhase, 1);
            _read = ReadLoopAsync(_connectionCts.Token);
            _ = WaitForLogoutAsync();
            return;
        }

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

        // QuickFIX envia o Logon (seq M) e só depois o SequenceReset-GapFill.
        // O dicionário FIX não fixa essa ordem. NewSeqNo é o próximo seq do router (M+1).
        long nextOut = _index.NextOut;
        if (_plan.SkipFrom > 0 && _plan.SkipFrom < nextOut)
        {
            TryEnqueueGapFill(_plan.SkipFrom, nextOut);
        }
        else if (_plan.GapFillLogonSequence && nextOut > 1)
        {
            TryEnqueueGapFill(nextOut - 1, nextOut);
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

        if (Volatile.Read(ref _logoutSent) == 0)
        {
            await SendLogoutDirectAsync().ConfigureAwait(false);
        }

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
    /// <param name="sequence">Seq já reservado e publicado no índice deste cliente.</param>
    /// <returns><see langword="false"/> quando a sessão está parando ou a fila desta sessão está cheia.</returns>
    internal bool TryEnqueue(DispatchPayload payload, long sequence)
    {
        if (Volatile.Read(ref _stop) != 0)
        {
            return false;
        }

        if (payload.RawBuffer is null)
        {
            throw new InvalidOperationException("O payload FIX outbound não tem buffer.");
        }

        if (sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), sequence, "O seq de aplicação precisa ser positivo.");
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
            int capacity = payload.DeliveryMode == DeliveryMode.Parse
                ? Math.Max(payload.RawBuffer.Memory.Length + 128, AdminMessageBytes)
                : payload.RawBuffer.Memory.Length + PassThroughRewriter.MaxGrowthBytes;
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
            long sequence = NextSequence(admin: true);
            int written = FixMessageBuilder.BuildLogout(rented, _options, sequence).Length;
            Volatile.Write(ref _logoutSent, 1);
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

    private async Task SendLogoutAsync(ReadOnlySpan<byte> text, CancellationToken cancellationToken)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(AdminMessageBytes);
        try
        {
            long sequence = NextSequence(admin: true);
            int written = FixMessageBuilder.BuildLogout(rented, _options, sequence, text).Length;
            await WritePipeAsync(rented.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
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
            long sequence = NextSequence(admin: true);
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
                if (!PassThroughRewriter.TryRewriteTo(raw.Memory.Span, destination, sequence, out int written))
                {
                    throw new InvalidOperationException(
                        "O frame PassThrough não pôde ser reescrito com o MsgSeqNum outbound (tags 9, 34 ou 10 ausentes ou inválidas).");
                }

                return written;
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
            long sequence = NextSequence(admin: true);
            rented = ArrayPool<byte>.Shared.Rent(AdminMessageBytes);
            int written = kind switch
            {
                AdminKind.Heartbeat => FixMessageBuilder.BuildHeartbeat(rented, _options, sequence, testReqId.Span).Length,
                AdminKind.TestRequest => FixMessageBuilder.BuildTestRequest(rented, _options, sequence).Length,
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
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        int sinceHeartbeat = 0;
        int tolerance = Math.Max(1, HeartBtInt / 5);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (Volatile.Read(ref _logoutPhase) != 0)
                {
                    continue;
                }

                sinceHeartbeat++;
                if (sinceHeartbeat >= HeartBtInt)
                {
                    TryEnqueueAdmin(AdminKind.Heartbeat, ReadOnlyMemory<byte>.Empty, ignoreStop: false);
                    sinceHeartbeat = 0;
                }

                double silentSeconds = Stopwatch.GetElapsedTime(Volatile.Read(ref _lastInboundTicks)).TotalSeconds;
                if (Volatile.Read(ref _testRequestOutstanding) == 0)
                {
                    if (silentSeconds >= HeartBtInt)
                    {
                        TryEnqueueAdmin(AdminKind.TestRequest, ReadOnlyMemory<byte>.Empty, ignoreStop: false);
                        Volatile.Write(ref _testRequestOutstanding, 1);
                    }
                }
                else if (silentSeconds >= HeartBtInt + tolerance)
                {
                    BeginLogout("Heartbeat timeout");
                }
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
        NoteInbound();
        Volatile.Write(ref _testRequestOutstanding, 0);
        if (!FixWireFrame.TryReadTag(frame, 35, out ReadOnlySpan<byte> msgType))
        {
            return true;
        }

        if (msgType.Length == 1 && msgType[0] == (byte)'5')
        {
            RequestStop();
            return false;
        }

        bool loggingOut = Volatile.Read(ref _logoutPhase) != 0;
        bool resend = msgType.Length == 1 && msgType[0] == (byte)'2';
        if (!loggingOut)
        {
            InboundSeq inbound = CheckInbound(frame);
            if (inbound == InboundSeq.TooLow)
            {
                BeginLogout("MsgSeqNum too low");
                loggingOut = true;
            }
            else if (inbound == InboundSeq.Duplicate)
            {
                return true;
            }
        }

        if (loggingOut && !resend)
        {
            return true;
        }

        if (!loggingOut && msgType.Length == 1 && msgType[0] == (byte)'1' && FixWireFrame.TryReadTag(frame, 112, out ReadOnlySpan<byte> testReqId))
        {
            byte[] copy = testReqId.ToArray();
            TryEnqueueAdmin(AdminKind.Heartbeat, copy, ignoreStop: false);
            return true;
        }

        if (resend)
        {
            HandleResendRequest(frame);
        }

        return true;
    }

    private InboundSeq CheckInbound(ReadOnlySpan<byte> frame)
    {
        if (!FixWireFrame.TryReadTag(frame, 34, out ReadOnlySpan<byte> raw)
            || !FixWireFrame.TryParseNonNegative(raw, out long seq)
            || seq <= 0)
        {
            return InboundSeq.Expected;
        }

        long expected = _state.NextInExpected;
        if (seq > expected)
        {
            _state.NextInExpected = seq + 1;
            AcceptorSessionMetrics.InSeqGap.Add(1);
            _logger.LogWarning(
                "Sessão FIX {SenderCompId} recebeu MsgSeqNum {Seq} acima do esperado {Expected}.",
                SenderCompId,
                seq,
                expected);
            return InboundSeq.Gap;
        }

        if (seq == expected)
        {
            _state.NextInExpected = seq + 1;
            return InboundSeq.Expected;
        }

        bool possDup = FixWireFrame.TryReadTag(frame, 43, out ReadOnlySpan<byte> flag) && flag.Length == 1 && flag[0] == (byte)'Y';
        return possDup ? InboundSeq.Duplicate : InboundSeq.TooLow;
    }

    private void HandleResendRequest(ReadOnlySpan<byte> frame)
    {
        if (!FixWireFrame.TryReadTag(frame, 7, out ReadOnlySpan<byte> beginRaw)
            || !FixWireFrame.TryParseNonNegative(beginRaw, out long beginSeqNo)
            || beginSeqNo <= 0)
        {
            return;
        }

        long nextOut = _index.NextOut;
        if (beginSeqNo >= nextOut)
        {
            AcceptorSessionMetrics.ClientAhead.Add(1);
            _logger.LogWarning(
                "Sessão FIX {SenderCompId} pediu BeginSeqNo {BeginSeqNo} acima do numerado {NextOut}.",
                SenderCompId,
                beginSeqNo,
                nextOut);
            BeginLogout("MsgSeqNum too high");
            return;
        }

        long endInclusive = nextOut - 1;
        if (FixWireFrame.TryReadTag(frame, 16, out ReadOnlySpan<byte> endRaw)
            && FixWireFrame.TryParseNonNegative(endRaw, out long endSeqNo)
            && endSeqNo > 0)
        {
            endInclusive = endSeqNo;
        }

        if (beginSeqNo > endInclusive)
        {
            return;
        }

        long newSeqNo = Math.Min(nextOut, endInclusive + 1);
        if (beginSeqNo >= newSeqNo)
        {
            return;
        }

        TryEnqueueGapFill(beginSeqNo, newSeqNo);
    }

    private void BeginLogout(string text)
    {
        if (Interlocked.CompareExchange(ref _logoutPhase, 1, 0) != 0)
        {
            return;
        }

        _logger.LogWarning("Sessão FIX {SenderCompId} envia Logout: {Text}.", SenderCompId, text);
        TryEnqueueLogout(Encoding.ASCII.GetBytes(text));
        _ = WaitForLogoutAsync();
    }

    private bool TryEnqueueLogout(ReadOnlySpan<byte> text)
    {
        if (!TryEnterGate())
        {
            return false;
        }

        byte[]? rented = null;
        try
        {
            long sequence = NextSequence(admin: true);
            rented = ArrayPool<byte>.Shared.Rent(AdminMessageBytes);
            int written = FixMessageBuilder.BuildLogout(rented, _options, sequence, text).Length;
            if (!_outbound.Writer.TryWrite(new OutboundFrame(rented, written)))
            {
                ArrayPool<byte>.Shared.Return(rented, clearArray: true);
                rented = null;
                return false;
            }

            Volatile.Write(ref _logoutSent, 1);
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

    private async Task WaitForLogoutAsync()
    {
        try
        {
            CancellationToken token = _connectionCts?.Token ?? CancellationToken.None;
            await Task.Delay(TimeSpan.FromSeconds(_plan.LogoutTimeoutSeconds), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        RequestStop();
    }

    private void NoteInbound() => Volatile.Write(ref _lastInboundTicks, Stopwatch.GetTimestamp());

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
        _ = cancellationToken;
    }

    private long NextSequence(bool admin)
    {
        long sequence = _index.ReserveSeq();
        if (admin)
        {
            _index.PublishAdmin(sequence);
        }

        return sequence;
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
        TestRequest,
        Logout,
    }

    private enum InboundSeq
    {
        Expected,
        Gap,
        Duplicate,
        TooLow,
    }
}
