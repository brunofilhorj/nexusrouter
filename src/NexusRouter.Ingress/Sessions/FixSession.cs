using System.Buffers;
using System.IO.Pipelines;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Channels;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using NexusRouter.Domain.Abstractions;
using NexusRouter.Domain.Enums;
using NexusRouter.Domain.Models;

namespace NexusRouter.Ingress.Sessions;

/// <summary>
/// Sessão FIX: conexão TCP, Logon, heartbeat e entrega do frame ao roteador.
/// </summary>
/// <remarks>
/// Uma thread de longa duração executa o ciclo de conexão. O heartbeat sai por um timer e usa o mesmo
/// envio do Logon. No encerramento, o Logout é escrito depois que a leitura do pipe para,
/// para não usar o <see cref="SslStream"/> ao mesmo tempo em leitura e escrita.
/// </remarks>
public sealed class FixSession : IHostedService
{
    private const byte Soh = 0x01;
    private const int MinimumReceiveBytes = 4096;
    private const int AdminMessageBytes = 1024;
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly SessionOptions _options;
    private readonly ISessionFrameDecoder _frameDecoder;
    private readonly IReconnectPolicy _reconnectPolicy;
    private readonly IMessageRouter _router;
    private readonly ChannelWriter<AuditRecord> _auditChannel;
    private readonly ILogger<FixSession> _logger;
    private readonly FixSessionStateMachine _stateMachine = new();
    private readonly SessionId _sessionId;
    private readonly SemaphoreSlim _outboundGate = new(1, 1);

    private CancellationTokenSource? _stoppingCts;
    private Task? _runTask;
    private Socket? _activeSocket;
    private SslStream? _activeSsl;
    private long _nextOutboundSequence;
    private long _testRequestSentTicks;
    private int _failedAttempts;
    private int _started;
    private int _logoutRequested;
    private int _shutdownAfterAdminReply;
    private int _closeExpected;
    private int _peerLogout;
    private int _sawInboundSinceTestRequest;

    /// <summary>
    /// Cria a sessão com as dependências do ciclo de transporte.
    /// </summary>
    /// <param name="options">Host, porta, identificadores FIX e TLS.</param>
    /// <param name="frameDecoder">Decoder do protocolo desta sessão.</param>
    /// <param name="reconnectPolicy">Atraso entre tentativas. Quem chama não espera dentro da política.</param>
    /// <param name="router">Destino das mensagens de aplicação.</param>
    /// <param name="auditChannel">Canal não bloqueante para falhas de conexão.</param>
    /// <param name="logger">Log estruturado da sessão.</param>
    /// <exception cref="ArgumentNullException">Alguma dependência é nula.</exception>
    /// <exception cref="ArgumentException">Host, comp IDs ou BeginString são inválidos.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A porta, o HeartBtInt, o limite de heartbeats ou o timeout de Logon é inválido.</exception>
    public FixSession(
        SessionOptions options,
        ISessionFrameDecoder frameDecoder,
        IReconnectPolicy reconnectPolicy,
        IMessageRouter router,
        ChannelWriter<AuditRecord> auditChannel,
        ILogger<FixSession> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(frameDecoder);
        ArgumentNullException.ThrowIfNull(reconnectPolicy);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(auditChannel);
        ArgumentNullException.ThrowIfNull(logger);
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

        _options = options;
        _frameDecoder = frameDecoder;
        _reconnectPolicy = reconnectPolicy;
        _router = router;
        _auditChannel = auditChannel;
        _logger = logger;
        _sessionId = new SessionId(options.SessionId);
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
    /// <returns>Tarefa concluída quando o ciclo termina ou quando <see cref="StopTimeout"/> expira.</returns>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _logoutRequested, 1);
        _stoppingCts?.Cancel();

        Task? runTask = _runTask;
        if (runTask is null)
        {
            return;
        }

        try
        {
            await runTask.WaitAsync(StopTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning(
                "Sessão {SessionId} não encerrou em {TimeoutSeconds}s",
                _sessionId.Value,
                StopTimeout.TotalSeconds);
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
        socket.SendTimeout = (int)StopTimeout.TotalMilliseconds;
        await socket.ConnectAsync(_options.Host, _options.Port, cancellationToken).ConfigureAwait(false);

        SslStream? sslStream = null;
        X509Certificate2? certificate = null;
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? heartbeat = null;
        Task? logonWatch = null;
        try
        {
            if (_options.TlsEnabled)
            {
                certificate = LoadClientCertificate();
                var networkStream = new NetworkStream(socket, ownsSocket: false);
                sslStream = new SslStream(networkStream, leaveInnerStreamOpen: false);
                await AuthenticateTlsAsync(sslStream, certificate, cancellationToken).ConfigureAwait(false);
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
            Volatile.Write(ref _activeSocket, socket);
            Volatile.Write(ref _activeSsl, sslStream);
            _logger.LogInformation(
                "Sessão {SessionId} conectou em {Host}:{Port}",
                _sessionId.Value,
                _options.Host,
                _options.Port);

            _stateMachine.TransitionToLogonSent();
            PublishLifecycle(SessionState.LogonSent, "Connected");
            SendAdmin(OutboundAdmin.Logon, ReadOnlySpan<byte>.Empty);

            var pipe = new Pipe(CreatePipeOptions());
            Task fill = FillPipeAsync(socket, sslStream, pipe.Writer, cancellationToken);
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
            Volatile.Write(ref _activeSsl, null);
            Volatile.Write(ref _activeSocket, null);
            if (sslStream is not null)
            {
                await sslStream.DisposeAsync().ConfigureAwait(false);
            }

            certificate?.Dispose();
        }
    }

    private async Task FillPipeAsync(
        Socket socket,
        SslStream? sslStream,
        PipeWriter writer,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Memory<byte> memory = writer.GetMemory(MinimumReceiveBytes);
                int received = sslStream is null
                    ? await socket.ReceiveAsync(memory, SocketFlags.None, cancellationToken).ConfigureAwait(false)
                    : await sslStream.ReadAsync(memory, cancellationToken).ConfigureAwait(false);
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
            var frame = new IngressFrame(
                _sessionId,
                decoded.RawBuffer,
                decoded.IngressTimestampTicks,
                decoded.FrameLength);
            Dispatch(in frame);
        }
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
                SendOutbound(_stateMachine.HandleResendRequest());
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

    private void SendOutbound(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return;
        }

        _outboundGate.Wait();
        try
        {
            WriteSocket(payload);
        }
        finally
        {
            _outboundGate.Release();
        }
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
                Span<byte> message = kind switch
                {
                    OutboundAdmin.Logon => FixMessageBuilder.BuildLogon(rented, _options, sequence),
                    OutboundAdmin.Heartbeat => FixMessageBuilder.BuildHeartbeat(rented, _options, sequence, testReqId),
                    OutboundAdmin.TestRequest => FixMessageBuilder.BuildTestRequest(rented, _options, sequence),
                    OutboundAdmin.Logout => FixMessageBuilder.BuildLogout(rented, _options, sequence),
                    _ => throw new InvalidOperationException("Mensagem administrativa desconhecida."),
                };
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

    private void WriteSocket(ReadOnlySpan<byte> payload)
    {
        SslStream? ssl = Volatile.Read(ref _activeSsl);
        if (ssl is not null)
        {
            ssl.Write(payload);
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

    private X509Certificate2? LoadClientCertificate()
    {
        if (string.IsNullOrWhiteSpace(_options.TlsCertificatePath))
        {
            return null;
        }

        string? password = null;
        if (!string.IsNullOrWhiteSpace(_options.TlsCertificatePasswordEnvVar))
        {
            password = Environment.GetEnvironmentVariable(_options.TlsCertificatePasswordEnvVar);
            if (string.IsNullOrEmpty(password))
            {
                throw new InvalidOperationException(
                    $"A variável de ambiente '{_options.TlsCertificatePasswordEnvVar}' não está definida.");
            }
        }

        return X509CertificateLoader.LoadPkcs12FromFile(
            _options.TlsCertificatePath,
            password,
            X509KeyStorageFlags.EphemeralKeySet);
    }

    private async Task AuthenticateTlsAsync(
        SslStream stream,
        X509Certificate2? certificate,
        CancellationToken cancellationToken)
    {
        var authentication = new SslClientAuthenticationOptions
        {
            TargetHost = _options.Host,
        };
        if (certificate is not null)
        {
            var certificates = new X509CertificateCollection();
            certificates.Add(certificate);
            authentication.ClientCertificates = certificates;
        }

        await stream.AuthenticateAsClientAsync(authentication, cancellationToken).ConfigureAwait(false);
    }

    private void ShutdownActiveSocket()
    {
        Socket? socket = Volatile.Read(ref _activeSocket);
        if (socket is null)
        {
            return;
        }

        try
        {
            socket.Shutdown(SocketShutdown.Both);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // A outra ponta já fechou o socket.
        }
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
            IngressTimestampTicks = Environment.TickCount64,
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
