using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;

using Microsoft.Extensions.Logging;

using NexusRouter.Domain.Enums;
using NexusRouter.Domain.Models;
using NexusRouter.Ingress.Sessions;
using NexusRouter.Router;

namespace NexusRouter.Egress.Fix;

/// <summary>
/// Sessão FIX cliente: Logon, heartbeat e reenvio do frame para um downstream.
/// </summary>
/// <remarks>
/// <para>
/// No modo <see cref="DeliveryMode.PassThrough"/> o frame é copiado e o MsgSeqNum desta sessão
/// é aplicado com <see cref="PassThroughRewriter"/>. A cópia evita alterar o buffer compartilhado pelo fan-out.
/// No modo <see cref="DeliveryMode.Parse"/> o frame sai de <see cref="FixMessageSerializer"/>.
/// </para>
/// <para>
/// Erro de socket espera o backoff, faz Logon de novo e tenta o envio mais uma vez.
/// O contador de sequência é o de <see cref="SequenceNumberStore"/> e não o da sessão inbound.
/// </para>
/// </remarks>
public sealed class FixOutboundDispatcher : DispatcherBase<FixOutboundDispatcherOptions>
{
    private const int AdminMessageBytes = 1024;
    private const int ReadBufferBytes = 64 * 1024;
    private const byte Soh = 0x01;

    private readonly SequenceNumberStore _sequenceStore;
    private readonly ExponentialBackoffReconnectPolicy _reconnectPolicy;
    private readonly SessionOptions _sessionOptions;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private TcpClient? _tcp;
    private Stream? _stream;
    private Pipe? _pipe;
    private CancellationTokenSource? _connectionCts;
    private CancellationTokenSource? _heartbeatCts;
    private Task? _pump;
    private Task? _read;
    private Task? _heartbeat;
    private TaskCompletionSource<bool> _logon = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _storeReady;
    private int _reconnectAttempts;

    /// <summary>
    /// Cria o dispatcher. O TCP e o Logon acontecem em <see cref="DispatcherBase{TOptions}.StartAsync"/>.
    /// </summary>
    /// <param name="dispatcherId">Nome único na rota e no arquivo de sequence number.</param>
    /// <param name="options">Host, comp IDs, TLS e diretório de sequência.</param>
    /// <param name="logger">Log de conexão, Logon e reconexão.</param>
    /// <exception cref="ArgumentException">As options são inválidas.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> é nulo.</exception>
    public FixOutboundDispatcher(
        string dispatcherId,
        FixOutboundDispatcherOptions options,
        ILogger<FixOutboundDispatcher> logger)
        : base(dispatcherId, Validate(options), options.ChannelCapacity, logger)
    {
        _sequenceStore = new SequenceNumberStore(options.SequenceStoreDirectory, logger);
        _reconnectPolicy = new ExponentialBackoffReconnectPolicy(
            options.ReconnectBaseDelay,
            options.ReconnectMaxDelay,
            options.ReconnectJitterFactor);
        _sessionOptions = new SessionOptions
        {
            SessionId = dispatcherId,
            Host = options.Host,
            Port = options.Port,
            SenderCompId = options.SenderCompId,
            TargetCompId = options.TargetCompId,
            BeginString = options.BeginString,
            HeartBtInt = options.HeartBtInt,
            LogonTimeout = options.LogonTimeout,
        };
    }

    /// <summary>
    /// Drena a fila, envia Logout, grava o sequence number e fecha o socket.
    /// </summary>
    /// <param name="cancellationToken">Abandona a espera do loop e a gravação do sequence number.</param>
    /// <returns>Tarefa concluída quando a sessão outbound parou.</returns>
    public override async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        _heartbeatCts?.Cancel();
        await SendLogoutQuietlyAsync().ConfigureAwait(false);
        await _sequenceStore.StopAsync(cancellationToken).ConfigureAwait(false);
        await CloseConnectionAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async ValueTask ConnectAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _storeReady, 1, 0) == 0)
        {
            try
            {
                await _sequenceStore.LoadAsync(DispatcherId, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                Interlocked.Exchange(ref _storeReady, 0);
                throw;
            }
        }

        await OpenSessionAsync(cancellationToken).ConfigureAwait(false);
        _reconnectAttempts = 0;
    }

    /// <inheritdoc />
    protected override async ValueTask SendAsync(DispatchPayload payload, CancellationToken cancellationToken)
    {
        try
        {
            await WritePayloadAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsConnectionFailure(ex) && !cancellationToken.IsCancellationRequested)
        {
            Logger.LogWarning(ex, "Dispatcher {DispatcherId} perdeu a sessão FIX outbound e vai reconectar.", DispatcherId);
            await ReconnectAsync(cancellationToken).ConfigureAwait(false);
            await WritePayloadAsync(payload, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task OpenSessionAsync(CancellationToken cancellationToken)
    {
        await CloseConnectionAsync().ConfigureAwait(false);

        _tcp = new TcpClient();
        try
        {
            _tcp.NoDelay = true;
            await _tcp.ConnectAsync(Options.Host, Options.Port, cancellationToken).ConfigureAwait(false);
            _stream = Options.TlsEnabled
                ? await TlsSocketFactory.WrapAsync(_tcp, CreateTlsOptions(), cancellationToken).ConfigureAwait(false)
                : _tcp.GetStream();

            _pipe = new Pipe(new PipeOptions(useSynchronizationContext: false));
            _logon = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _connectionCts = new CancellationTokenSource();
            _pump = PumpAsync(_pipe.Reader, _stream, _connectionCts.Token);
            _read = ReadLoopAsync(_stream, _connectionCts.Token);
            await SendAdminAsync(OutboundAdmin.Logon, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
            await _logon.Task.WaitAsync(Options.LogonTimeout, cancellationToken).ConfigureAwait(false);

            _heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(_connectionCts.Token);
            _heartbeat = HeartbeatLoopAsync(_heartbeatCts.Token);
            Logger.LogInformation(
                "Dispatcher {DispatcherId} fez Logon em {Host}:{Port}.",
                DispatcherId,
                Options.Host,
                Options.Port);
        }
        catch
        {
            await CloseConnectionAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task ReconnectAsync(CancellationToken cancellationToken)
    {
        int attempt = _reconnectAttempts;
        if (_reconnectAttempts < int.MaxValue)
        {
            _reconnectAttempts++;
        }

        TimeSpan delay = _reconnectPolicy.ComputeDelay(attempt);
        Logger.LogWarning(
            "Dispatcher {DispatcherId} reconecta em {DelayMs} ms (tentativa {Attempt}).",
            DispatcherId,
            delay.TotalMilliseconds,
            attempt);
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        await OpenSessionAsync(cancellationToken).ConfigureAwait(false);
        _reconnectAttempts = 0;
    }

    private async ValueTask WritePayloadAsync(DispatchPayload payload, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        byte[]? rented = null;
        try
        {
            ReadOnlyMemory<byte> frame = PrepareFrame(payload, ref rented);
            await WriteFrameUnderGateAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            }

            _writeGate.Release();
        }
    }

    private ReadOnlyMemory<byte> PrepareFrame(DispatchPayload payload, ref byte[]? rented)
    {
        if (payload.RawBuffer is null)
        {
            throw new InvalidOperationException("O payload FIX outbound não tem buffer.");
        }

        long sequence = _sequenceStore.Next();
        switch (payload.DeliveryMode)
        {
            case DeliveryMode.PassThrough:
            {
                ReadOnlyMemory<byte> source = payload.RawBuffer.Memory;
                rented = ArrayPool<byte>.Shared.Rent(Math.Max(source.Length, 1));
                source.CopyTo(rented);
                if (!PassThroughRewriter.Rewrite(rented.AsSpan(0, source.Length), sequence))
                {
                    throw new InvalidOperationException("O MsgSeqNum outbound não coube no campo 34.");
                }

                return rented.AsMemory(0, source.Length);
            }

            case DeliveryMode.Parse:
            {
                if (!payload.TryGetParsedMessage(out FixMessage message))
                {
                    throw new InvalidOperationException("O payload em modo Parse não tem mensagem tokenizada.");
                }

                int capacity = Math.Max(payload.RawBuffer.Memory.Length + 128, AdminMessageBytes);
                rented = ArrayPool<byte>.Shared.Rent(capacity);
                int written = FixMessageSerializer.Serialize(
                    in message,
                    rented,
                    sequence,
                    Options.SenderCompId,
                    Options.TargetCompId);
                return rented.AsMemory(0, written);
            }

            default:
                throw new InvalidOperationException("Modo de entrega desconhecido.");
        }
    }

    private async ValueTask SendAdminAsync(OutboundAdmin kind, ReadOnlyMemory<byte> testReqId, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        byte[] rented = ArrayPool<byte>.Shared.Rent(AdminMessageBytes);
        try
        {
            long sequence = _sequenceStore.Next();
            int written = kind switch
            {
                OutboundAdmin.Logon => FixMessageBuilder.BuildLogon(rented, _sessionOptions, sequence).Length,
                OutboundAdmin.Heartbeat => FixMessageBuilder.BuildHeartbeat(rented, _sessionOptions, sequence, testReqId.Span).Length,
                OutboundAdmin.Logout => FixMessageBuilder.BuildLogout(rented, _sessionOptions, sequence).Length,
                _ => throw new InvalidOperationException("Mensagem administrativa desconhecida."),
            };
            await WriteFrameUnderGateAsync(rented.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            _writeGate.Release();
        }
    }

    private async ValueTask WriteFrameUnderGateAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        PipeWriter writer = _pipe?.Writer ?? throw new IOException("A sessão FIX outbound não está conectada.");
        FlushResult result = await writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        if (result.IsCompleted || result.IsCanceled)
        {
            throw new IOException("O pipe outbound foi encerrado.");
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Options.HeartBtInt));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await SendAdminAsync(OutboundAdmin.Heartbeat, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Dispatcher {DispatcherId} interrompeu o heartbeat outbound.", DispatcherId);
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
            _logon.TrySetException(ex);
        }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
        }
    }

    private async Task ReadLoopAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ReadBufferBytes);
        int count = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (count == buffer.Length)
                {
                    throw new InvalidOperationException("A mensagem recebida na sessão outbound excedeu o buffer.");
                }

                int read = await stream.ReadAsync(buffer.AsMemory(count), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new IOException("A sessão FIX outbound foi fechada pela contraparte.");
                }

                count += read;
                while (TryTakeFrame(buffer.AsSpan(0, count), out int frameLength))
                {
                    HandleFrame(buffer.AsSpan(0, frameLength), cancellationToken);
                    int remaining = count - frameLength;
                    if (remaining > 0)
                    {
                        Buffer.BlockCopy(buffer, frameLength, buffer, 0, remaining);
                    }

                    count = remaining;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logon.TrySetException(ex);
            _pipe?.Writer.Complete(ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private void HandleFrame(ReadOnlySpan<byte> frame, CancellationToken cancellationToken)
    {
        if (!TryReadTag(frame, 35, out ReadOnlySpan<byte> msgType))
        {
            return;
        }

        if (msgType.Length == 1 && msgType[0] == (byte)'A')
        {
            _logon.TrySetResult(true);
            return;
        }

        if (msgType.Length == 1 && msgType[0] == (byte)'5')
        {
            throw new IOException("A contraparte enviou Logout.");
        }

        if (msgType.Length == 1 && msgType[0] == (byte)'1' && TryReadTag(frame, 112, out ReadOnlySpan<byte> testReqId))
        {
            QueueTestRequestReply(testReqId, cancellationToken);
        }
    }

    private void QueueTestRequestReply(ReadOnlySpan<byte> testReqId, CancellationToken cancellationToken)
    {
        byte[] copy = testReqId.ToArray();
        Task reply = SendAdminAsync(OutboundAdmin.Heartbeat, copy, cancellationToken).AsTask();
        _ = reply.ContinueWith(
            completed =>
            {
                if (completed.Exception is not null)
                {
                    Logger.LogWarning(completed.Exception, "Dispatcher {DispatcherId} não respondeu ao TestRequest.", DispatcherId);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private async Task SendLogoutQuietlyAsync()
    {
        if (_pipe is null || _stream is null)
        {
            return;
        }

        try
        {
            await SendAdminAsync(OutboundAdmin.Logout, ReadOnlyMemory<byte>.Empty, CancellationToken.None)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or TimeoutException or InvalidOperationException or OperationCanceledException)
        {
            Logger.LogDebug(ex, "Dispatcher {DispatcherId} não enviou Logout.", DispatcherId);
        }
    }

    private async Task CloseConnectionAsync()
    {
        _heartbeatCts?.Cancel();
        try
        {
            _pipe?.Writer.Complete();
        }
        catch (InvalidOperationException)
        {
            // A leitura já completou o pipe ao detectar o fim da sessão.
        }

        if (_pump is not null)
        {
            try
            {
                await _pump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException)
            {
                Logger.LogDebug(ex, "Dispatcher {DispatcherId} encerrou a escrita outbound.", DispatcherId);
            }
        }

        _connectionCts?.Cancel();
        Task[] pending = Collect(_read, _heartbeat);
        if (pending.Length > 0)
        {
            try
            {
                await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or AggregateException)
            {
                Logger.LogDebug(ex, "Dispatcher {DispatcherId} encerrou a leitura outbound.", DispatcherId);
            }
        }

        _pump = null;
        _read = null;
        _heartbeat = null;

        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
        }

        _tcp?.Dispose();
        _tcp = null;
        _pipe = null;
        _connectionCts?.Dispose();
        _connectionCts = null;
        _heartbeatCts?.Dispose();
        _heartbeatCts = null;
    }

    private TlsOptions CreateTlsOptions() =>
        new()
        {
            Enabled = true,
            CertificatePath = Options.TlsCertificatePath,
            CertificatePasswordEnvVar = Options.TlsCertificatePasswordEnvVar,
            TargetHost = Options.Host,
        };

    private static bool TryTakeFrame(ReadOnlySpan<byte> buffer, out int frameLength)
    {
        ReadOnlySpan<byte> marker = "10="u8;
        int searchFrom = 0;
        while (searchFrom < buffer.Length)
        {
            int relative = buffer[searchFrom..].IndexOf(marker);
            if (relative < 0)
            {
                break;
            }

            int at = searchFrom + relative;
            bool boundary = at == 0 || buffer[at - 1] == Soh;
            if (boundary
                && at + 6 < buffer.Length
                && IsDigit(buffer[at + 3])
                && IsDigit(buffer[at + 4])
                && IsDigit(buffer[at + 5])
                && buffer[at + 6] == Soh)
            {
                frameLength = at + 7;
                return true;
            }

            searchFrom = at + 3;
        }

        frameLength = 0;
        return false;
    }

    private static bool TryReadTag(ReadOnlySpan<byte> frame, int tag, out ReadOnlySpan<byte> value)
    {
        int index = 0;
        while (index < frame.Length)
        {
            int parsed = 0;
            bool any = false;
            while (index < frame.Length && IsDigit(frame[index]))
            {
                any = true;
                parsed = (parsed * 10) + (frame[index] - (byte)'0');
                index++;
            }

            if (!any || index >= frame.Length || frame[index] != (byte)'=')
            {
                break;
            }

            index++;
            int valueStart = index;
            int end = frame[index..].IndexOf(Soh);
            if (end < 0)
            {
                break;
            }

            if (parsed == tag)
            {
                value = frame.Slice(valueStart, end);
                return true;
            }

            index = valueStart + end + 1;
        }

        value = default;
        return false;
    }

    private static bool IsDigit(byte value) => value is >= (byte)'0' and <= (byte)'9';

    private static bool IsConnectionFailure(Exception exception) =>
        exception is IOException or SocketException or ObjectDisposedException or TimeoutException;

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

    private static FixOutboundDispatcherOptions Validate(FixOutboundDispatcherOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.EnsureValid();
        return options;
    }

    private enum OutboundAdmin
    {
        Logon,
        Heartbeat,
        Logout,
    }
}
