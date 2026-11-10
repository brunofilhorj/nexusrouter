using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using NexusRouter.Domain.Abstractions;
using NexusRouter.Domain.Enums;
using NexusRouter.Domain.Models;
using NexusRouter.Domain.Secrets;

namespace NexusRouter.Ingress.Sessions;

/// <summary>
/// Sessão FIX: conexão TCP, Logon, heartbeat e entrega do frame ao roteador.
/// </summary>
/// <remarks>
/// Uma thread de longa duração executa o ciclo de conexão. O heartbeat sai por um timer e usa o mesmo
/// envio do Logon. No encerramento, o Logout é escrito depois que a leitura do pipe para,
/// para não usar o stream TLS ao mesmo tempo em leitura e escrita.
/// </remarks>
public sealed class FixSession : IHostedService
{
    private const byte Soh = 0x01;
    private const int MinimumReceiveBytes = 4096;
    private const int AdminMessageBytes = 1024;
    private const int MaxLogonPasswordBytes = 128;

    private readonly SessionOptions _options;
    private readonly ISessionFrameDecoder _frameDecoder;
    private readonly IReconnectPolicy _reconnectPolicy;
    private readonly IMessageRouter _router;
    private readonly ChannelWriter<AuditRecord> _auditChannel;
    private readonly ISecretResolver _secretResolver;
    private readonly ILogger<FixSession> _logger;
    private readonly ISessionLogStream _sessionLog;
    private readonly FixSessionStateMachine _stateMachine = new();
    private readonly SessionId _sessionId;
    private readonly SemaphoreSlim _outboundGate = new(1, 1);
    private readonly FixSessionMetrics? _metrics;
    private readonly TimeProvider _timeProvider;

    private CancellationTokenSource? _stoppingCts;
    private Task? _runTask;
    private Socket? _activeSocket;
    private Stream? _activeSecureStream;
    private long _nextOutboundSequence;
    private long _testRequestSentTicks;
    private int _failedAttempts;
    private int _started;
    private int _logoutRequested;
    private int _shutdownAfterAdminReply;
    private int _closeExpected;
    private int _peerLogout;
    private int _sawInboundSinceTestRequest;
    private int _socketShutdown;

    /// <summary>
    /// Cria a sessão com as dependências do ciclo de transporte.
    /// </summary>
    /// <param name="options">Host, porta, identificadores FIX e TLS.</param>
    /// <param name="frameDecoder">Decoder do protocolo desta sessão.</param>
    /// <param name="reconnectPolicy">Atraso entre tentativas. Quem chama não espera dentro da política.</param>
    /// <param name="router">Destino das mensagens de aplicação.</param>
    /// <param name="auditChannel">Canal não bloqueante para falhas de conexão.</param>
    /// <param name="secretResolver">Resolve a senha do certificado TLS pela chave em <paramref name="options"/>, sob demanda.</param>
    /// <param name="logger">Log estruturado da sessão.</param>
    /// <param name="sessionLog">Log de sessão. O frame original é gravado antes da máquina de estados.</param>
    /// <param name="metrics">Contadores de rejeição. Nulo desliga a métrica.</param>
    /// <param name="timeProvider">Relógio da checagem de SendingTime. Nulo usa o relógio do sistema.</param>
    /// <exception cref="ArgumentNullException">Alguma dependência é nula.</exception>
    /// <exception cref="ArgumentException">Host, comp IDs ou BeginString são inválidos.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A porta, o HeartBtInt, o limite de heartbeats ou o timeout de Logon é inválido.</exception>
    public FixSession(
        SessionOptions options,
        ISessionFrameDecoder frameDecoder,
        IReconnectPolicy reconnectPolicy,
        IMessageRouter router,
        ChannelWriter<AuditRecord> auditChannel,
        ISecretResolver secretResolver,
        ILogger<FixSession> logger,
        ISessionLog sessionLog,
        FixSessionMetrics? metrics = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(frameDecoder);
        ArgumentNullException.ThrowIfNull(reconnectPolicy);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(auditChannel);
        ArgumentNullException.ThrowIfNull(secretResolver);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(sessionLog);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Host);
        if (options.Port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(options.Port), options.Port, "A porta deve estar entre 1 e 65535.");
        }

        ValidateAsciiToken(options.BeginString, nameof(options.BeginString));
        ValidateAsciiToken(options.SenderCompId, nameof(options.SenderCompId));
        ValidateAsciiToken(options.TargetCompId, nameof(options.TargetCompId));
        if (options.HeartBtInt <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.HeartBtInt), options.HeartBtInt, "HeartBtInt deve ser positivo.");
        }

        if (options.MissedHeartbeatThreshold <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.MissedHeartbeatThreshold),
                options.MissedHeartbeatThreshold,
                "O limite de heartbeats ausentes deve ser positivo.");
        }

        if (options.LogonTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.LogonTimeout), options.LogonTimeout, "O timeout de Logon deve ser positivo.");
        }

        if (options.SendingTimeTolerance < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.SendingTimeTolerance),
                options.SendingTimeTolerance,
                "A tolerância de SendingTime não pode ser negativa.");
        }

        if (options.CancelOnDisconnectType is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.CancelOnDisconnectType),
                options.CancelOnDisconnectType,
                "CancelOnDisconnectType aceita 0, 1, 2 ou 3.");
        }

        if (options.CodTimeoutWindow is < 0 or > 60_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.CodTimeoutWindow),
                options.CodTimeoutWindow,
                "CODTimeoutWindow aceita de 0 a 60000 milissegundos.");
        }

        if (options.LogonText is { Length: > 128 })
        {
            throw new ArgumentOutOfRangeException(nameof(options.LogonText), options.LogonText.Length, "Text (58) aceita no máximo 128 caracteres.");
        }

        if (!string.IsNullOrEmpty(options.LogonText))
        {
            ValidateAsciiToken(options.LogonText, nameof(options.LogonText));
        }

        if (!string.IsNullOrEmpty(options.PasswordEnvVar))
        {
            ValidateAsciiToken(options.PasswordEnvVar, nameof(options.PasswordEnvVar));
        }

        _options = options;
        _frameDecoder = frameDecoder;
        _reconnectPolicy = reconnectPolicy;
        _router = router;
        _auditChannel = auditChannel;
        _secretResolver = secretResolver;
        _logger = logger;
        _sessionId = new SessionId(options.SessionId);
        _sessionLog = sessionLog.OpenStream(options.SessionId);
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Dispara o ciclo de conexão numa thread de longa duração e retorna em seguida.
    /// </summary>
    /// <param name="cancellationToken">Cancelamento do host. Também encerra o ciclo.</param>
    /// <returns>Tarefa já concluída.</returns>
    /// <exception cref="InvalidOperationException">A sessão já foi iniciada.</exception>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            throw new InvalidOperationException("A sessão já foi iniciada.");
        }

        _stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runTask = Task.Factory.StartNew(
            () => RunAsync(_stoppingCts.Token),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Pede Logout, cancela o ciclo e espera a thread de longa duração.
    /// </summary>
    /// <param name="cancellationToken">Abandona a espera. O ciclo já foi cancelado.</param>
    /// <returns>Tarefa concluída quando o ciclo termina ou quando <see cref="SessionOptions.ShutdownTimeoutSeconds"/> expira.</returns>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _logoutRequested, 1);
        _stoppingCts?.Cancel();

        Task? runTask = _runTask;
        if (runTask is null)
        {
            return;
        }

        TimeSpan stopTimeout = TimeSpan.FromSeconds(_options.ShutdownTimeoutSeconds);

        try
        {
            await runTask.WaitAsync(stopTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning(
                "Sessão {SessionId} não encerrou em {TimeoutSeconds}s",
                _sessionId.Value,
                stopTimeout.TotalSeconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("A espera pelo encerramento da sessão {SessionId} foi cancelada", _sessionId.Value);
        }
        catch (OperationCanceledException)
        {
            // O próprio ciclo cancelou a leitura para poder enviar o Logout.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sessão {SessionId} encerrou com falha", _sessionId.Value);
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _stateMachine.TransitionToConnecting();
                PublishLifecycle(
                    SessionState.Connecting,
                    _failedAttempts == 0 ? null : "Reconnecting");
                await ConnectAndPumpAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsStopRequested(ex, cancellationToken))
            {
                break;
            }
            catch (Exception ex)
            {
                ReportConnectionFailure(ex);
            }
            finally
            {
                ForceDisconnected();
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            TimeSpan delay = _reconnectPolicy.ComputeDelay(_failedAttempts);
            _logger.LogWarning(
                "Sessão {SessionId} reconecta em {DelayMs} ms (tentativa {Attempt})",
                _sessionId.Value,
                delay.TotalMilliseconds,
                _failedAttempts);
            if (_failedAttempts < int.MaxValue)
            {
                _failedAttempts++;
            }

            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task ConnectAndPumpAsync(CancellationToken cancellationToken)
    {
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        socket.NoDelay = true;
        socket.SendTimeout = (int)TimeSpan.FromSeconds(_options.ShutdownTimeoutSeconds).TotalMilliseconds;
        await socket.ConnectAsync(_options.Host, _options.Port, cancellationToken).ConfigureAwait(false);

        TcpClient? tlsClient = null;
        Stream? secureStream = null;
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? heartbeat = null;
        Task? logonWatch = null;
        try
        {
            if (_options.TlsEnabled)
            {
                tlsClient = AdoptConnectedSocket(socket);
                secureStream = await TlsSocketFactory.WrapAsync(
                    tlsClient,
                    new TlsOptions
                    {
                        Enabled = true,
                        CertificatePath = _options.TlsCertificatePath,
                        CertificatePasswordEnvVar = _options.TlsCertificatePasswordEnvVar,
                        TargetHost = _options.Host,
                    },
                    _secretResolver,
                    cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Sessão {SessionId} autenticou TLS com {Host}:{Port}",
                    _sessionId.Value,
                    _options.Host,
                    _options.Port);
            }

            _failedAttempts = 0;
            Volatile.Write(ref _shutdownAfterAdminReply, 0);
            Volatile.Write(ref _closeExpected, 0);
            Volatile.Write(ref _peerLogout, 0);
            Volatile.Write(ref _testRequestSentTicks, 0);
            Volatile.Write(ref _sawInboundSinceTestRequest, 0);
            Volatile.Write(ref _socketShutdown, 0);
            Volatile.Write(ref _activeSocket, socket);
            Volatile.Write(ref _activeSecureStream, secureStream);
            _logger.LogInformation(
                "Sessão {SessionId} conectou em {Host}:{Port}",
                _sessionId.Value,
                _options.Host,
                _options.Port);

            _stateMachine.TransitionToLogonSent();
            PublishLifecycle(SessionState.LogonSent, "Connected");
            SendAdmin(OutboundAdmin.Logon, ReadOnlySpan<byte>.Empty);

            var pipe = new Pipe(CreatePipeOptions());
            Task fill = FillPipeAsync(socket, secureStream, pipe.Writer, cancellationToken);
            Task read = ReadPipeAsync(pipe.Reader, cancellationToken);
            heartbeat = HeartbeatLoopAsync(connectionCts.Token);
            logonWatch = WatchLogonAsync(connectionCts.Token);
            await Task.WhenAll(fill, read).ConfigureAwait(false);
        }
        finally
        {
            connectionCts.Cancel();
            await ObserveAdminLoopAsync(heartbeat).ConfigureAwait(false);
            await ObserveAdminLoopAsync(logonWatch).ConfigureAwait(false);
            TrySendLogout();
            Volatile.Write(ref _activeSecureStream, null);
            Volatile.Write(ref _activeSocket, null);
            if (secureStream is not null)
            {
                await secureStream.DisposeAsync().ConfigureAwait(false);
            }

            tlsClient?.Dispose();
        }
    }

    private async Task FillPipeAsync(
        Socket socket,
        Stream? secureStream,
        PipeWriter writer,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Memory<byte> memory = writer.GetMemory(MinimumReceiveBytes);
                int received = secureStream is null
                    ? await socket.ReceiveAsync(memory, SocketFlags.None, cancellationToken).ConfigureAwait(false)
                    : await secureStream.ReadAsync(memory, cancellationToken).ConfigureAwait(false);
                if (received == 0)
                {
                    break;
                }

                writer.Advance(received);
                FlushResult flush = await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (flush.IsCompleted || flush.IsCanceled)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if ((Volatile.Read(ref _shutdownAfterAdminReply) == 1 || Volatile.Read(ref _closeExpected) == 1)
                && ex is SocketException or ObjectDisposedException or IOException)
            {
                failure = null;
            }
            else
            {
                failure = ex;
                throw;
            }
        }
        finally
        {
            await writer.CompleteAsync(failure).ConfigureAwait(false);
        }
    }

    private async Task ReadPipeAsync(PipeReader reader, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = result.Buffer;
                try
                {
                    Consume(ref buffer);
                }
                finally
                {
                    reader.AdvanceTo(buffer.Start, buffer.End);
                }

                if (result.IsCompleted || result.IsCanceled)
                {
                    break;
                }
            }
        }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
        }
    }

    private void Consume(ref ReadOnlySequence<byte> buffer)
    {
        while (_frameDecoder.TryDecode(ref buffer, out IngressFrame decoded))
        {
            if (!IsFrameValid(in decoded))
            {
                continue;
            }

            LogRef logRef = AppendOriginal(in decoded);
            var frame = new IngressFrame(
                _sessionId,
                decoded.RawBuffer,
                decoded.IngressTimestampTicks,
                decoded.FrameLength,
                logRef);
            Dispatch(in frame);
        }
    }

    /// <summary>
    /// Valida o frame antes do log e do router. Frame inválido gera Reject, é contado e não segue adiante.
    /// </summary>
    private bool IsFrameValid(in IngressFrame decoded)
    {
        ReadOnlySequence<byte> raw = decoded.RawBuffer;
        int length = raw.Length > int.MaxValue ? int.MaxValue : (int)raw.Length;
        if (decoded.FrameLength > 0 && decoded.FrameLength < length)
        {
            length = decoded.FrameLength;
        }

        if (length == 0)
        {
            return true;
        }

        FixFrameValidation result;
        bool isReject;
        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (raw.IsSingleSegment)
        {
            ReadOnlySpan<byte> span = raw.FirstSpan[..length];
            result = FixFrameValidator.Validate(span, _options, now);
            isReject = result.IsValid ? false : IsRejectMessage(span);
        }
        else
        {
            byte[] rented = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                raw.Slice(0, length).CopyTo(rented);
                result = FixFrameValidator.Validate(rented.AsSpan(0, length), _options, now);
                isReject = result.IsValid ? false : IsRejectMessage(rented.AsSpan(0, length));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        if (result.IsValid)
        {
            return true;
        }

        _metrics?.RecordReject(_sessionId.Value, result.SessionRejectReason, result.RefTagId);
        _logger.LogWarning(
            "Sessão {SessionId} rejeitou frame de entrada: {Text} (RefSeqNum {RefSeqNum}, RefTagID {RefTagId})",
            _sessionId.Value,
            result.Text,
            result.RefSeqNum,
            result.RefTagId);

        // Um Reject inválido não é respondido com outro Reject, para não gerar laço entre as pontas.
        if (!isReject)
        {
            SendReject(in result);
        }

        return false;
    }

    private static bool IsRejectMessage(ReadOnlySpan<byte> frame) => frame.IndexOf("\u000135=3\u0001"u8) >= 0;

    private void SendReject(in FixFrameValidation validation)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(AdminMessageBytes);
        try
        {
            _outboundGate.Wait();
            try
            {
                long sequence = Interlocked.Increment(ref _nextOutboundSequence);
                Span<byte> message = FixMessageBuilder.BuildReject(
                    rented,
                    _options,
                    sequence,
                    validation.RefSeqNum,
                    validation.RefTagId,
                    validation.SessionRejectReason,
                    validation.Text);
                WriteSocket(message);
            }
            finally
            {
                _outboundGate.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "Sessão {SessionId} não enviou o Reject", _sessionId.Value);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private LogRef AppendOriginal(in IngressFrame decoded)
    {
        if (!TryReadSequenceField(decoded.RawBuffer, "34="u8, out long inSeq) || inSeq <= 0)
        {
            return default;
        }

        ReadOnlySequence<byte> raw = decoded.RawBuffer;
        if (raw.Length == 0)
        {
            return default;
        }

        int length = raw.Length > int.MaxValue ? int.MaxValue : (int)raw.Length;
        if (decoded.FrameLength > 0 && decoded.FrameLength < length)
        {
            length = decoded.FrameLength;
        }

        AppendResult result;
        if (raw.IsSingleSegment)
        {
            result = _sessionLog.Append(raw.FirstSpan[..length], inSeq, decoded.IngressTimestampTicks);
        }
        else
        {
            byte[] rented = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                raw.Slice(0, length).CopyTo(rented);
                result = _sessionLog.Append(rented.AsSpan(0, length), inSeq, decoded.IngressTimestampTicks);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        return result.IsOk ? result.Ref : default;
    }

    private void Dispatch(in IngressFrame frame)
    {
        Interlocked.Exchange(ref _sawInboundSinceTestRequest, 1);
        Span<byte> msgType = stackalloc byte[16];
        if (!TryReadMsgType(frame.RawBuffer, msgType, out int length))
        {
            _stateMachine.RecordInboundMessage();
            _router.Route(in frame);
            return;
        }

        ReadOnlySpan<byte> type = msgType[..length];
        if (IsSessionAdmin(type))
        {
            HandleAdmin(type, frame.RawBuffer);
            if (Volatile.Read(ref _shutdownAfterAdminReply) == 1)
            {
                ShutdownActiveSocket();
            }

            return;
        }

        _stateMachine.RecordInboundMessage();
        _router.Route(in frame);
    }

    private void HandleAdmin(ReadOnlySpan<byte> msgType, ReadOnlySequence<byte> raw)
    {
        if (msgType.Length != 1)
        {
            _stateMachine.RecordInboundMessage();
            return;
        }

        switch (msgType[0])
        {
            case (byte)'0':
                _stateMachine.HandleHeartbeat();
                return;
            case (byte)'1':
                _stateMachine.HandleTestRequest();
                Span<byte> testReqId = stackalloc byte[64];
                if (!TryCopyField(raw, "112="u8, testReqId, out int testReqIdLength))
                {
                    SendAdmin(OutboundAdmin.Heartbeat, ReadOnlySpan<byte>.Empty);
                    return;
                }

                SendAdmin(OutboundAdmin.Heartbeat, testReqId[..testReqIdLength]);
                return;
            case (byte)'2':
                HandleResendRequest(raw);
                return;
            case (byte)'5':
                _stateMachine.HandleLogout();
                Volatile.Write(ref _peerLogout, 1);
                TryEnterLogoutSent();
                SendAdmin(OutboundAdmin.Logout, ReadOnlySpan<byte>.Empty);
                Volatile.Write(ref _shutdownAfterAdminReply, 1);
                return;
            case (byte)'A':
                if (_stateMachine.State == SessionState.LogonSent)
                {
                    if (!TryAcceptLogon(raw))
                    {
                        return;
                    }

                    _stateMachine.TransitionToActive();
                    PublishLifecycle(SessionState.Active, "Logon");
                    return;
                }

                _stateMachine.RecordInboundMessage();
                return;
            default:
                _stateMachine.RecordInboundMessage();
                return;
        }
    }

    private static bool IsSessionAdmin(ReadOnlySpan<byte> msgType)
    {
        if (msgType.Length != 1)
        {
            return false;
        }

        return msgType[0] is (byte)'0' or (byte)'1' or (byte)'2' or (byte)'5' or (byte)'A';
    }

    private static bool TryReadMsgType(ReadOnlySequence<byte> buffer, Span<byte> destination, out int length)
    {
        length = 0;
        var reader = new SequenceReader<byte>(buffer);
        Span<byte> tag = stackalloc byte[4];
        while (reader.TryAdvanceTo(Soh, advancePastDelimiter: false))
        {
            if (reader.Remaining < tag.Length)
            {
                return false;
            }

            var peek = reader;
            if (!peek.TryCopyTo(tag))
            {
                return false;
            }

            if (tag[1] != (byte)'3' || tag[2] != (byte)'5' || tag[3] != (byte)'=')
            {
                reader.Advance(1);
                continue;
            }

            reader.Advance(tag.Length);
            while (reader.TryRead(out byte value))
            {
                if (value == Soh)
                {
                    return length > 0;
                }

                if (length == destination.Length)
                {
                    return false;
                }

                destination[length++] = value;
            }

            return false;
        }

        return false;
    }

    /// <summary>
    /// Responde a um ResendRequest com um SequenceReset-GapFill.
    /// </summary>
    /// <remarks>
    /// Esta sessão só envia mensagens administrativas (Logon, Heartbeat, TestRequest, Logout), que a spec FIX manda
    /// substituir por gap-fill em vez de retransmitir. Por isso o intervalo pedido inteiro vira um único SequenceReset.
    /// O pedido sem BeginSeqNo válido é ignorado com aviso: a sessão ainda não gera Reject (35=3).
    /// </remarks>
    private void HandleResendRequest(ReadOnlySequence<byte> raw)
    {
        _stateMachine.HandleResendRequest();
        if (!TryReadSequenceField(raw, "7="u8, out long beginSeqNo) || beginSeqNo <= 0)
        {
            _logger.LogWarning("Sessão {SessionId} recebeu ResendRequest sem BeginSeqNo válido", _sessionId.Value);
            return;
        }

        // EndSeqNo ausente ou 0 significa "até o fim" (FIX 4.4).
        if (!TryReadSequenceField(raw, "16="u8, out long endSeqNo) || endSeqNo < 0)
        {
            endSeqNo = 0;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(AdminMessageBytes);
        try
        {
            _outboundGate.Wait();
            try
            {
                long nextSequence = Volatile.Read(ref _nextOutboundSequence) + 1;
                if (beginSeqNo >= nextSequence)
                {
                    _logger.LogWarning(
                        "Sessão {SessionId} recebeu ResendRequest a partir de {BeginSeqNo}, ainda não enviado (próximo {NextSeqNo})",
                        _sessionId.Value,
                        beginSeqNo,
                        nextSequence);
                    return;
                }

                long newSeqNo = endSeqNo > 0 && endSeqNo + 1 < nextSequence ? endSeqNo + 1 : nextSequence;
                Span<byte> message = FixMessageBuilder.BuildSequenceReset(rented, _options, beginSeqNo, newSeqNo, gapFill: true);
                WriteSocket(message);
            }
            finally
            {
                _outboundGate.Release();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static bool TryReadSequenceField(ReadOnlySequence<byte> raw, ReadOnlySpan<byte> tag, out long value)
    {
        value = 0;
        Span<byte> digits = stackalloc byte[20];
        return TryCopyField(raw, tag, digits, out int length)
            && Utf8Parser.TryParse(digits[..length], out value, out int consumed)
            && consumed == length;
    }

    private void SendAdmin(OutboundAdmin kind, ReadOnlySpan<byte> testReqId)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(AdminMessageBytes);
        try
        {
            _outboundGate.Wait();
            try
            {
                long sequence = Interlocked.Increment(ref _nextOutboundSequence);
                int written = kind == OutboundAdmin.Logon
                    ? WriteLogon(rented, sequence)
                    : WriteAdmin(rented, kind, sequence, testReqId);
                WriteSocket(rented.AsSpan(0, written));
            }
            finally
            {
                _outboundGate.Release();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: kind == OutboundAdmin.Logon);
        }
    }

    private int WriteAdmin(Span<byte> buffer, OutboundAdmin kind, long sequence, ReadOnlySpan<byte> testReqId)
    {
        Span<byte> message = kind switch
        {
            OutboundAdmin.Heartbeat => FixMessageBuilder.BuildHeartbeat(buffer, _options, sequence, testReqId),
            OutboundAdmin.TestRequest => FixMessageBuilder.BuildTestRequest(buffer, _options, sequence),
            OutboundAdmin.Logout => FixMessageBuilder.BuildLogout(buffer, _options, sequence),
            _ => throw new InvalidOperationException("Mensagem administrativa desconhecida."),
        };
        return message.Length;
    }

    private int WriteLogon(Span<byte> buffer, long sequence)
    {
        Span<byte> password = stackalloc byte[MaxLogonPasswordBytes];
        int passwordLength = ResolveLogonPassword(password);
        int written = FixMessageBuilder.BuildLogon(buffer, _options, sequence, password[..passwordLength]).Length;
        password[..passwordLength].Clear();
        return written;
    }

    private int ResolveLogonPassword(Span<byte> destination)
    {
        if (string.IsNullOrEmpty(_options.PasswordEnvVar))
        {
            return 0;
        }

        string secret = _secretResolver.ResolveRequired(_options.PasswordEnvVar);
        if (secret.Length > destination.Length)
        {
            throw new InvalidOperationException("A senha do Logon excede 128 bytes.");
        }

        for (int i = 0; i < secret.Length; i++)
        {
            char ch = secret[i];
            if (ch > 127 || ch == Soh)
            {
                throw new InvalidOperationException("A senha do Logon deve ser ASCII e não pode conter SOH.");
            }

            destination[i] = (byte)ch;
        }

        return secret.Length;
    }

    private bool TryAcceptLogon(ReadOnlySequence<byte> raw)
    {
        if (!TryReadSequenceField(raw, "108="u8, out long heartBtInt) || heartBtInt != _options.HeartBtInt)
        {
            RejectPeerLogon("HeartBtInt não confere");
            return false;
        }

        Span<byte> resetFlag = stackalloc byte[8];
        bool echoed = TryCopyField(raw, "141="u8, resetFlag, out int resetLength)
            && resetLength == 1
            && resetFlag[0] == (byte)'Y';
        if (echoed != _options.ResetSeqNumFlag)
        {
            RejectPeerLogon("ResetSeqNumFlag não foi ecoado");
            return false;
        }

        return true;
    }

    private void RejectPeerLogon(string reason)
    {
        _logger.LogWarning("Sessão {SessionId} recusou o Logon: {Reason}", _sessionId.Value, reason);
        try
        {
            SendAdmin(OutboundAdmin.Logout, ReadOnlySpan<byte>.Empty);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "Sessão {SessionId} não enviou Logout após recusar o Logon", _sessionId.Value);
        }

        Volatile.Write(ref _closeExpected, 1);
        ShutdownActiveSocket();
    }

    private void WriteSocket(ReadOnlySpan<byte> payload)
    {
        Stream? secure = Volatile.Read(ref _activeSecureStream);
        if (secure is not null)
        {
            secure.Write(payload);
            return;
        }

        Volatile.Read(ref _activeSocket)?.Send(payload, SocketFlags.None);
    }

    private void TrySendLogout()
    {
        if (Interlocked.Exchange(ref _logoutRequested, 0) != 1)
        {
            return;
        }

        if (Volatile.Read(ref _activeSocket) is null)
        {
            return;
        }

        // Socket já encerrado (peer mudo, Logout do peer ou timeout de Logon): o Logout não seria entregue.
        // Enviá-lo publicaria LogoutSent sem envio real, ou repetiria o Logout que já respondeu ao peer.
        if (Volatile.Read(ref _socketShutdown) == 1)
        {
            _logger.LogDebug("Sessão {SessionId} não envia Logout: o socket já foi encerrado", _sessionId.Value);
            return;
        }

        try
        {
            TryEnterLogoutSent();
            SendAdmin(OutboundAdmin.Logout, ReadOnlySpan<byte>.Empty);
            _logger.LogInformation("Sessão {SessionId} enviou Logout", _sessionId.Value);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            _logger.LogWarning(ex, "Sessão {SessionId} não enviou o Logout de encerramento", _sessionId.Value);
        }
    }

    private bool TryEnterLogoutSent()
    {
        if (_stateMachine.State != SessionState.Active)
        {
            return false;
        }

        try
        {
            _stateMachine.TransitionToLogoutSent();
        }
        catch (InvalidSessionStateException)
        {
            return false;
        }

        PublishLifecycle(SessionState.LogoutSent, "Logout");
        return true;
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.HeartBtInt));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_stateMachine.State != SessionState.Active || Volatile.Read(ref _activeSocket) is null)
                {
                    continue;
                }

                long sentTicks = Volatile.Read(ref _testRequestSentTicks);
                long elapsed = Environment.TickCount64 - sentTicks;
                long intervalMilliseconds = _options.HeartBtInt * 1000L;
                if (sentTicks != 0 && elapsed >= intervalMilliseconds)
                {
                    if (Volatile.Read(ref _sawInboundSinceTestRequest) == 0)
                    {
                        _logger.LogWarning(
                            "Sessão {SessionId} não respondeu ao TestRequest em {HeartBtInt}s",
                            _sessionId.Value,
                            _options.HeartBtInt);
                        Volatile.Write(ref _closeExpected, 1);
                        ShutdownActiveSocket();
                        return;
                    }

                    Volatile.Write(ref _testRequestSentTicks, 0);
                }

                SendAdmin(OutboundAdmin.Heartbeat, ReadOnlySpan<byte>.Empty);
                if (Volatile.Read(ref _testRequestSentTicks) != 0
                    || !_stateMachine.IsHeartbeatStale(_options.MissedHeartbeatThreshold, _options.HeartBtInt))
                {
                    continue;
                }

                _logger.LogWarning(
                    "Sessão {SessionId} sem heartbeat após {MissedHeartbeats} intervalos; enviando TestRequest",
                    _sessionId.Value,
                    _options.MissedHeartbeatThreshold);
                Interlocked.Exchange(ref _sawInboundSinceTestRequest, 0);
                Volatile.Write(ref _testRequestSentTicks, Environment.TickCount64);
                SendAdmin(OutboundAdmin.TestRequest, ReadOnlySpan<byte>.Empty);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sessão {SessionId} interrompeu o heartbeat", _sessionId.Value);
            Volatile.Write(ref _closeExpected, 1);
            ShutdownActiveSocket();
        }
    }

    private async Task WatchLogonAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_options.LogonTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (_stateMachine.State != SessionState.LogonSent)
        {
            return;
        }

        _logger.LogWarning(
            "Sessão {SessionId} não recebeu Logon em {TimeoutSeconds}s",
            _sessionId.Value,
            _options.LogonTimeout.TotalSeconds);
        Volatile.Write(ref _closeExpected, 1);
        ShutdownActiveSocket();
    }

    private async Task ObserveAdminLoopAsync(Task? loop)
    {
        if (loop is null)
        {
            return;
        }

        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sessão {SessionId} encerrou um loop administrativo com falha", _sessionId.Value);
        }
    }

    private void PublishLifecycle(SessionState newState, string? detail)
    {
        var lifecycleEvent = new SessionLifecycleEvent
        {
            SessionId = _sessionId,
            NewState = newState,
            Timestamp = DateTimeOffset.UtcNow,
            Detail = detail,
        };
        _logger.LogInformation(
            "Sessão {SessionId} entrou no estado {NewState}. Detalhe: {Detail}",
            _sessionId.Value,
            newState,
            detail);
        if (!_auditChannel.TryWrite(new AuditRecord
        {
            SessionId = _sessionId,
            LifecycleEvent = lifecycleEvent,
        }))
        {
            _logger.LogWarning(
                "Canal de auditoria cheio ao registrar ciclo de vida da sessão {SessionId}",
                _sessionId.Value);
        }
    }

    private static bool TryCopyField(ReadOnlySequence<byte> source, ReadOnlySpan<byte> tag, Span<byte> destination, out int length)
    {
        length = 0;
        var reader = new SequenceReader<byte>(source);
        while (reader.TryAdvanceTo(Soh, advancePastDelimiter: true))
        {
            if (!reader.IsNext(tag, advancePast: true))
            {
                continue;
            }

            while (reader.TryRead(out byte value))
            {
                if (value == Soh)
                {
                    return length > 0;
                }

                if (length == destination.Length)
                {
                    return false;
                }

                destination[length++] = value;
            }

            return false;
        }

        return false;
    }

    private static TcpClient AdoptConnectedSocket(Socket socket)
    {
        var client = new TcpClient();
        Socket unused = client.Client;
        client.Client = socket;
        unused.Dispose();
        return client;
    }

    private void ShutdownActiveSocket()
    {
        Socket? socket = Volatile.Read(ref _activeSocket);
        if (socket is null)
        {
            return;
        }

        Volatile.Write(ref _socketShutdown, 1);
        try
        {
            socket.Shutdown(SocketShutdown.Both);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // A outra ponta já fechou o socket.
        }

        // No Windows, Shutdown não acorda um ReceiveAsync pendente: ele só termina quando o peer envia FIN ou RST.
        // Com o peer mudo (o caso do TestRequest sem resposta) a sessão ficaria presa. Close aborta o receive, e o
        // FillPipeAsync já trata esse erro como esperado quando _closeExpected ou _shutdownAfterAdminReply valem 1.
        socket.Close();
    }

    private void ReportConnectionFailure(Exception exception)
    {
        _logger.LogError(
            exception,
            "Sessão {SessionId} falhou ao comunicar com {Host}:{Port}",
            _sessionId.Value,
            _options.Host,
            _options.Port);

        bool wrote = _auditChannel.TryWrite(new AuditRecord
        {
            SessionId = _sessionId,
            IngressTimestampTicks = Stopwatch.GetTimestamp(),
            WasDropped = true,
            DropReason = exception.Message,
        });
        if (!wrote)
        {
            _logger.LogWarning("Canal de auditoria cheio ao registrar falha da sessão {SessionId}", _sessionId.Value);
        }
    }

    private void ForceDisconnected()
    {
        SessionState previous = _stateMachine.State;
        if (previous == SessionState.Disconnected)
        {
            return;
        }

        try
        {
            _stateMachine.TransitionToDisconnected();
        }
        catch (InvalidSessionStateException ex)
        {
            _logger.LogWarning(
                ex,
                "Sessão {SessionId} não pôde ir para Disconnected a partir de {State}",
                _sessionId.Value,
                _stateMachine.State);
            return;
        }

        string detail = previous != SessionState.LogoutSent && Volatile.Read(ref _peerLogout) == 1
            ? "Logout"
            : "Disconnected";
        PublishLifecycle(SessionState.Disconnected, detail);
    }

    private static bool IsStopRequested(Exception exception, CancellationToken cancellationToken)
    {
        if (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (exception is OperationCanceledException)
        {
            return true;
        }

        if (exception is not AggregateException aggregate)
        {
            return false;
        }

        foreach (Exception inner in aggregate.InnerExceptions)
        {
            if (inner is not OperationCanceledException)
            {
                return false;
            }
        }

        return aggregate.InnerExceptions.Count > 0;
    }

    private static void ValidateAsciiToken(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!Ascii.IsValid(value) || value.Contains('=') || value.Contains((char)Soh))
        {
            throw new ArgumentException("O valor deve ser ASCII e não pode conter SOH nem '='.", name);
        }
    }

    private static PipeOptions CreatePipeOptions() =>
        new(
            pool: MemoryPool<byte>.Shared,
            pauseWriterThreshold: 64 * 1024,
            resumeWriterThreshold: 32 * 1024,
            minimumSegmentSize: 4096,
            useSynchronizationContext: false);

    private enum OutboundAdmin
    {
        Logon,
        Heartbeat,
        TestRequest,
        Logout,
    }
}
