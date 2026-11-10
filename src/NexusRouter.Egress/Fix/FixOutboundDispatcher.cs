using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

using Microsoft.Extensions.Logging;

using NexusRouter.Domain.Abstractions;
using NexusRouter.Domain.Models;
using NexusRouter.Domain.Pooling;
using NexusRouter.Domain.Secrets;
using NexusRouter.Ingress.Sessions;

namespace NexusRouter.Egress.Fix;

/// <summary>
/// Acceptor FIX: escuta um endereço e mantém uma sessão independente por cliente conectado.
/// </summary>
/// <remarks>
/// <para>
/// OMS, risco e qualquer outro cliente conectam aqui. Cada Logon vira uma sessão com fila e MsgSeqNum próprios.
/// Um cliente lento enche só a própria fila. Os outros clientes e os dispatchers de broker não esperam por ele.
/// </para>
/// <para>
/// No modo <see cref="NexusRouter.Domain.Enums.DeliveryMode.PassThrough"/> cada sessão recebe uma cópia do frame
/// com o MsgSeqNum dela. No modo <see cref="NexusRouter.Domain.Enums.DeliveryMode.Parse"/> o frame sai de
/// <see cref="FixMessageSerializer"/> com o SenderCompID deste acceptor e o TargetCompID do cliente.
/// Cliente conhecido recebe <c>ReserveSeq</c> e <c>PublishApp</c> mesmo sem sessão logada. O envio no socket só ocorre se a sessão existe.
/// </para>
/// </remarks>
public sealed class FixOutboundDispatcher : DispatcherBase<FixOutboundDispatcherOptions>
{
    private const int ReadBufferBytes = 64 * 1024;

    private readonly ExponentialBackoffReconnectPolicy _acceptRetry;
    private readonly ConcurrentSessionTable _sessions = new();
    private readonly SemaphoreSlim _adoptGate = new(1, 1);
    private readonly ISecretResolver _secretResolver;
    private readonly ISessionLog _sessionLog;
    private readonly object _knownGate = new();
    private ClientBinding[] _known = [];

    private TcpListener? _listener;
    private CancellationTokenSource? _acceptCts;
    private Task? _accept;
    private int _stopping;
    private int _acceptFailures;

    /// <summary>
    /// Cria o acceptor. O socket de escuta abre em <see cref="DispatcherBase{TOptions}.StartAsync"/>.
    /// </summary>
    /// <param name="dispatcherId">Nome único na rota e prefixo do arquivo de sequence number.</param>
    /// <param name="options">Endereço de escuta, comp ID local, TLS e diretório de sequência.</param>
    /// <param name="logger">Log de accept, Logon e sessão.</param>
    /// <param name="payloadPool">Pool de payloads do dispatcher.</param>
    /// <param name="secretResolver">Resolve a senha do certificado TLS pela chave em <paramref name="options"/>, sob demanda.</param>
    /// <param name="sessionLog">Client index and consumer state. Replaces the per-session sequence file.</param>
    /// <param name="catchUp">Cursor do log de sessão. Nulo entrega só o que chegar pelo channel.</param>
    /// <exception cref="ArgumentException">As options são inválidas.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/>, <paramref name="secretResolver"/> ou <paramref name="sessionLog"/> é nulo.</exception>
    public FixOutboundDispatcher(
        string dispatcherId,
        FixOutboundDispatcherOptions options,
        ILogger<FixOutboundDispatcher> logger,
        DispatchPayloadPool payloadPool,
        ISecretResolver secretResolver,
        ISessionLog sessionLog,
        StreamCatchUp? catchUp = null)
        : base(dispatcherId, Validate(options), options.ChannelCapacity, payloadPool, logger, catchUp)
    {
        ArgumentNullException.ThrowIfNull(secretResolver);
        ArgumentNullException.ThrowIfNull(sessionLog);
        _secretResolver = secretResolver;
        _sessionLog = sessionLog;
        _acceptRetry = new ExponentialBackoffReconnectPolicy(
            options.AcceptRetryBaseDelay,
            options.AcceptRetryMaxDelay,
            options.AcceptRetryJitterFactor);
    }

    /// <summary>
    /// Para de aceitar, drena a fila do dispatcher e encerra cada sessão com Logout.
    /// </summary>
    /// <param name="cancellationToken">Abandona a espera do loop e a gravação dos sequence numbers.</param>
    /// <returns>Tarefa concluída quando o acceptor parou.</returns>
    public override async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        _acceptCts?.Cancel();
        StopListener();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        await _adoptGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        FixAcceptedSession[] snapshot;
        try
        {
            Volatile.Write(ref _stopping, 1);
            snapshot = _sessions.Drain();
        }
        finally
        {
            _adoptGate.Release();
        }

        if (_accept is not null)
        {
            try
            {
                await _accept.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                Logger.LogDebug(ex, "Dispatcher {DispatcherId} encerrou o accept.", DispatcherId);
            }
        }

        foreach (FixAcceptedSession session in snapshot)
        {
            await session.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    protected override ValueTask ConnectAsync(CancellationToken cancellationToken)
    {
        IPAddress address = IPAddress.Parse(Options.ListenAddress);
        LoadKnownClients();
        _listener = new TcpListener(address, Options.ListenPort);
        _listener.Start();
        _acceptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _accept = AcceptLoopAsync(_acceptCts.Token);
        var bound = (IPEndPoint)_listener.LocalEndpoint;
        Logger.LogInformation(
            "Dispatcher {DispatcherId} escuta FIX em {Address}:{Port}.",
            DispatcherId,
            bound.Address,
            bound.Port);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask SendAsync(DispatchPayload payload, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        ClientBinding[] known = Volatile.Read(ref _known);
        if (known.Length == 0)
        {
            Logger.LogTrace(
                "Dispatcher {DispatcherId} não tem cliente conhecido. A mensagem ficou no log de ingress.",
                DispatcherId);
            return ValueTask.CompletedTask;
        }

        for (int i = 0; i < known.Length; i++)
        {
            IClientIndex index = known[i].Index;
            long sequence = index.ReserveSeq();
            index.PublishApp(sequence, payload.LogRef);
            string sender = SenderOf(index);
            if (!_sessions.TryGet(sender, out FixAcceptedSession? found) || found is null)
            {
                continue;
            }

            FixAcceptedSession session = found;

            try
            {
                if (!session.TryEnqueue(payload, sequence))
                {
                    Logger.LogWarning(
                        "Sessão {SenderCompId} do dispatcher {DispatcherId} não enfileirou a mensagem.",
                        session.SenderCompId,
                        DispatcherId);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                Logger.LogWarning(
                    ex,
                    "Sessão {SenderCompId} do dispatcher {DispatcherId} não recebeu a mensagem.",
                    session.SenderCompId,
                    DispatcherId);
            }
        }

        return ValueTask.CompletedTask;
    }

    private void LoadKnownClients()
    {
        var buffer = new ClientBinding[16];
        int count = _sessionLog.CopyClients(DispatcherId, buffer);
        if (count > buffer.Length)
        {
            buffer = new ClientBinding[count];
            count = _sessionLog.CopyClients(DispatcherId, buffer);
        }

        if (count == 0)
        {
            Volatile.Write(ref _known, []);
            return;
        }

        if (count != buffer.Length)
        {
            Array.Resize(ref buffer, count);
        }

        Volatile.Write(ref _known, buffer);
    }

    private void Remember(ClientBinding binding)
    {
        lock (_knownGate)
        {
            ClientBinding[] current = Volatile.Read(ref _known);
            for (int i = 0; i < current.Length; i++)
            {
                if (ReferenceEquals(current[i].Index, binding.Index))
                {
                    return;
                }
            }

            var next = new ClientBinding[current.Length + 1];
            current.CopyTo(next, 0);
            next[^1] = binding;
            Volatile.Write(ref _known, next);
        }
    }

    private static string SenderOf(IClientIndex index)
    {
        string key = index.ClientKey;
        int slash = key.LastIndexOf('/');
        return slash < 0 || slash == key.Length - 1 ? key : key[(slash + 1)..];
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                TcpListener listener = _listener ?? throw new IOException("O acceptor FIX não está escutando.");
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _acceptFailures = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex) when (ex is SocketException or IOException or InvalidOperationException)
            {
                if (cancellationToken.IsCancellationRequested || Volatile.Read(ref _stopping) == 1)
                {
                    break;
                }

                int attempt = _acceptFailures;
                if (_acceptFailures < int.MaxValue)
                {
                    _acceptFailures++;
                }

                TimeSpan delay = _acceptRetry.ComputeDelay(attempt);
                Logger.LogWarning(
                    ex,
                    "Dispatcher {DispatcherId} falhou no accept e tenta de novo em {DelayMs} ms.",
                    DispatcherId,
                    delay.TotalMilliseconds);
                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                continue;
            }

            client.NoDelay = true;
            _ = HandleClientAsync(client, cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        Stream? stream = null;
        FixAcceptedSession? session = null;
        bool ownsSocket = true;
        try
        {
            stream = Options.TlsEnabled
                ? await TlsSocketFactory.WrapServerAsync(
                    client,
                    CreateTlsOptions(),
                    Options.TlsRequireClientCertificate,
                    _secretResolver,
                    cancellationToken).ConfigureAwait(false)
                : client.GetStream();

            PendingLogon? logon = await TryReadLogonAsync(stream, cancellationToken).ConfigureAwait(false);
            if (logon is null)
            {
                return;
            }

            AcceptorRecoveryOptions recovery = RecoveryFor(logon.Value.SenderCompId);
            if (logon.Value.ResetSequence && !recovery.AllowResetSeqNumFlag)
            {
                await WriteRejectLogoutAsync(stream, logon.Value.SenderCompId, "ResetSeqNumFlag not allowed", recovery.LogoutTimeoutSeconds, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (recovery.RequireNextExpectedMsgSeqNum && !logon.Value.HasNextExpected)
            {
                await WriteRejectLogoutAsync(stream, logon.Value.SenderCompId, "NextExpectedMsgSeqNum required", recovery.LogoutTimeoutSeconds, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (logon.Value.MsgSeqNum <= 0)
            {
                await WriteRejectLogoutAsync(stream, logon.Value.SenderCompId, "Logon rejected", recovery.LogoutTimeoutSeconds, cancellationToken).ConfigureAwait(false);
                return;
            }

            ClientBinding binding = _sessionLog.OpenClient(DispatcherId, logon.Value.SenderCompId);
            string? reject = DecideLogon(logon.Value, recovery, binding, out long skipFrom, out bool gapFillLogon, out bool echoReset);
            if (reject is null)
            {
                Remember(binding);
            }

            int heartBtInt = logon.Value.HeartBtInt is >= 1 and <= 3600 ? logon.Value.HeartBtInt : Options.HeartBtInt;
            var sessionOptions = new SessionOptions
            {
                SessionId = DispatcherId + "-" + logon.Value.SenderCompId,
                Host = Options.ListenAddress,
                Port = Options.ListenPort,
                SenderCompId = Options.SenderCompId,
                TargetCompId = logon.Value.SenderCompId,
                BeginString = Options.BeginString,
                HeartBtInt = heartBtInt,
                LogonTimeout = Options.LogonTimeout,
                ResetSeqNumFlag = echoReset,
                NextExpectedMsgSeqNum = reject is null && logon.Value.HasNextExpected && recovery.AcceptNextExpectedMsgSeqNum
                    ? binding.State.NextInExpected
                    : null,
            };
            var plan = new AcceptorSessionPlan
            {
                HeartBtInt = heartBtInt,
                SkipFrom = reject is null ? skipFrom : 0,
                GapFillLogonSequence = reject is null && gapFillLogon,
                RejectText = reject,
                LogoutTimeoutSeconds = recovery.LogoutTimeoutSeconds,
                State = binding.State,
            };
            session = new FixAcceptedSession(
                logon.Value.SenderCompId,
                client,
                stream,
                binding.Index,
                plan,
                sessionOptions,
                Options.ChannelCapacity,
                logon.Value.Prelude,
                logon.Value.PreludeLength,
                OnSessionClosed,
                Logger);
            ownsSocket = false;
            stream = null;
            (bool adopted, FixAcceptedSession? replaced) = await AdoptAsync(session, cancellationToken).ConfigureAwait(false);
            if (!adopted)
            {
                await session.StopAsync(CancellationToken.None).ConfigureAwait(false);
                session = null;
                return;
            }

            try
            {
                await session.StartAsync(cancellationToken).ConfigureAwait(false);
                session = null;
            }
            finally
            {
                if (replaced is not null)
                {
                    await replaced.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Logger.LogWarning(ex, "Dispatcher {DispatcherId} recusou uma conexão FIX.", DispatcherId);
        }
        catch (OperationCanceledException)
        {
            Logger.LogDebug("Dispatcher {DispatcherId} cancelou o handshake FIX.", DispatcherId);
        }
        finally
        {
            if (session is not null)
            {
                await session.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                if (stream is not null)
                {
                    await DisposeQuietlyAsync(stream, client).ConfigureAwait(false);
                }
                else if (ownsSocket)
                {
                    client.Dispose();
                }
            }
        }
    }

    private async Task<(bool Adopted, FixAcceptedSession? Replaced)> AdoptAsync(
        FixAcceptedSession session,
        CancellationToken cancellationToken)
    {
        await _adoptGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _stopping) == 1)
            {
                return (false, null);
            }

            FixAcceptedSession? replaced = _sessions.Replace(session);
            if (replaced is not null)
            {
                Logger.LogInformation(
                    "Dispatcher {DispatcherId} substituiu a sessão anterior de {SenderCompId}.",
                    DispatcherId,
                    session.SenderCompId);
            }

            return (true, replaced);
        }
        finally
        {
            _adoptGate.Release();
        }
    }

    private void OnSessionClosed(FixAcceptedSession session) => _sessions.RemoveIfSame(session);

    private async Task<PendingLogon?> TryReadLogonAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Options.LogonTimeout);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ReadBufferBytes);
        int count = 0;
        try
        {
            while (!timeout.IsCancellationRequested)
            {
                if (FixWireFrame.TryTakeFrame(buffer.AsSpan(0, count), out int frameLength))
                {
                    if (!TryAcceptLogon(buffer.AsSpan(0, frameLength), out PendingLogon accepted))
                    {
                        Logger.LogWarning(
                            "Dispatcher {DispatcherId} recusou Logon. TargetCompID esperado {SenderCompId}.",
                            DispatcherId,
                            Options.SenderCompId);
                        string target = string.IsNullOrEmpty(accepted.SenderCompId) ? "UNKNOWN" : accepted.SenderCompId;
                        await WriteRejectLogoutAsync(stream, target, "Logon rejected", Options.Recovery.LogoutTimeoutSeconds, cancellationToken).ConfigureAwait(false);
                        return null;
                    }

                    int preludeLength = count - frameLength;
                    byte[] prelude = preludeLength == 0 ? [] : new byte[preludeLength];
                    if (preludeLength > 0)
                    {
                        buffer.AsSpan(frameLength, preludeLength).CopyTo(prelude);
                    }

                    return accepted.WithPrelude(prelude, preludeLength);
                }

                if (count == buffer.Length)
                {
                    Logger.LogWarning("Dispatcher {DispatcherId} recebeu um Logon maior que o buffer.", DispatcherId);
                    return null;
                }

                int read = await stream.ReadAsync(buffer.AsMemory(count), timeout.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    Logger.LogWarning("Dispatcher {DispatcherId} fechou antes do Logon.", DispatcherId);
                    return null;
                }

                count += read;
            }

            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Logger.LogWarning("Dispatcher {DispatcherId} não recebeu Logon dentro do timeout.", DispatcherId);
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private bool TryAcceptLogon(ReadOnlySpan<byte> frame, out PendingLogon logon)
    {
        logon = default;
        if (!FixWireFrame.TryReadTag(frame, 35, out ReadOnlySpan<byte> msgType) || msgType.Length != 1 || msgType[0] != (byte)'A')
        {
            return false;
        }

        if (!FixWireFrame.TryReadTag(frame, 8, out ReadOnlySpan<byte> beginString)
            || !FixWireFrame.EqualsAscii(beginString, Options.BeginString))
        {
            return false;
        }

        if (!FixWireFrame.TryReadTag(frame, 56, out ReadOnlySpan<byte> target)
            || !FixWireFrame.EqualsAscii(target, Options.SenderCompId))
        {
            return false;
        }

        if (!FixWireFrame.TryReadTag(frame, 49, out ReadOnlySpan<byte> sender)
            || !FixWireFrame.TryDecodeSessionKey(sender, out string senderCompId)
            || !IsAllowed(senderCompId))
        {
            if (FixWireFrame.TryReadTag(frame, 49, out ReadOnlySpan<byte> rawSender)
                && FixWireFrame.TryDecodeSessionKey(rawSender, out string rejected))
            {
                logon = PendingLogon.Identity(rejected);
            }

            return false;
        }

        bool resetSequence = FixWireFrame.TryReadTag(frame, 141, out ReadOnlySpan<byte> reset) && reset.Length == 1 && reset[0] == (byte)'Y';
        long msgSeqNum = 0;
        if (FixWireFrame.TryReadTag(frame, 34, out ReadOnlySpan<byte> seqRaw))
        {
            FixWireFrame.TryParseNonNegative(seqRaw, out msgSeqNum);
        }

        int heartBtInt = 0;
        if (FixWireFrame.TryReadTag(frame, 108, out ReadOnlySpan<byte> heartRaw)
            && FixWireFrame.TryParseNonNegative(heartRaw, out long heart)
            && heart is >= 1 and <= int.MaxValue)
        {
            heartBtInt = (int)heart;
        }

        bool possDup = FixWireFrame.TryReadTag(frame, 43, out ReadOnlySpan<byte> dup) && dup.Length == 1 && dup[0] == (byte)'Y';
        long next = 0;
        bool hasNext = FixWireFrame.TryReadTag(frame, 789, out ReadOnlySpan<byte> nextRaw)
            && FixWireFrame.TryParseNonNegative(nextRaw, out next)
            && next > 0;
        logon = PendingLogon.Accepted(senderCompId, resetSequence, msgSeqNum, possDup, heartBtInt, hasNext, hasNext ? next : 0);
        return true;
    }

    private string? DecideLogon(
        PendingLogon logon,
        AcceptorRecoveryOptions recovery,
        ClientBinding binding,
        out long skipFrom,
        out bool gapFillLogon,
        out bool echoReset)
    {
        skipFrom = 0;
        gapFillLogon = false;
        echoReset = false;
        if (logon.ResetSequence)
        {
            if (logon.HasNextExpected && recovery.AcceptNextExpectedMsgSeqNum && logon.NextExpected > 1)
            {
                NoteClientAhead(logon.SenderCompId, logon.NextExpected, 1);
                return "MsgSeqNum too high";
            }

            ClientBinding reset = _sessionLog.ResetClient(DispatcherId, logon.SenderCompId);
            reset.State.NextInExpected = logon.MsgSeqNum + 1;
            echoReset = true;
            return null;
        }

        long expected = binding.State.NextInExpected;
        if (logon.MsgSeqNum > expected)
        {
            AcceptorSessionMetrics.InSeqGap.Add(1);
            Logger.LogWarning(
                "Sessão FIX {SenderCompId} recebeu MsgSeqNum {Seq} acima do esperado {Expected}.",
                logon.SenderCompId,
                logon.MsgSeqNum,
                expected);
        }
        else if (logon.MsgSeqNum < expected && !logon.PossDup)
        {
            return "MsgSeqNum too low";
        }

        long nextOut = binding.Index.NextOut;
        if (logon.HasNextExpected && recovery.AcceptNextExpectedMsgSeqNum)
        {
            if (logon.NextExpected > nextOut)
            {
                NoteClientAhead(logon.SenderCompId, logon.NextExpected, nextOut);
                return "MsgSeqNum too high";
            }

            if (logon.NextExpected < nextOut)
            {
                if (recovery.OnReconnectGap == OnReconnectGap.SkipToCurrent)
                {
                    skipFrom = logon.NextExpected;
                }
                else
                {
                    // Reenvio de aplicação N..M-1 com PossDup fica no TD-08.
                    // Até lá o intervalo continua no .cidx e o cliente pede por ResendRequest.
                    // O gap-fill abaixo cobre só o seq que o Logon de resposta vai consumir.
                    gapFillLogon = true;
                }
            }
        }

        if (logon.MsgSeqNum >= expected)
        {
            binding.State.NextInExpected = logon.MsgSeqNum + 1;
        }

        return null;
    }

    private void NoteClientAhead(string senderCompId, long requested, long nextOut)
    {
        AcceptorSessionMetrics.ClientAhead.Add(1);
        Logger.LogWarning(
            "Sessão FIX {SenderCompId} pediu seq {Requested} acima do numerado {NextOut}.",
            senderCompId,
            requested,
            nextOut);
    }

    private AcceptorRecoveryOptions RecoveryFor(string senderCompId)
    {
        IReadOnlyList<AcceptorClientOverride> overrides = Options.ClientOverrides;
        for (int i = 0; i < overrides.Count; i++)
        {
            if (string.Equals(overrides[i].SenderCompId, senderCompId, StringComparison.Ordinal))
            {
                return Options.Recovery.Merge(overrides[i]);
            }
        }

        return Options.Recovery;
    }

    private async Task WriteRejectLogoutAsync(
        Stream stream,
        string targetCompId,
        string text,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var options = new SessionOptions
        {
            SessionId = DispatcherId + "-" + targetCompId,
            Host = Options.ListenAddress,
            Port = Options.ListenPort,
            SenderCompId = Options.SenderCompId,
            TargetCompId = targetCompId,
            BeginString = Options.BeginString,
            HeartBtInt = Options.HeartBtInt,
        };
        byte[] rented = ArrayPool<byte>.Shared.Rent(512);
        try
        {
            int written = FixMessageBuilder.BuildLogout(rented, options, 1, Encoding.ASCII.GetBytes(text)).Length;
            await stream.WriteAsync(rented.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            Logger.LogDebug(ex, "Dispatcher {DispatcherId} não enviou Logout de recusa.", DispatcherId);
            return;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ReadBufferBytes);
        int count = 0;
        try
        {
            while (!timeout.IsCancellationRequested)
            {
                if (FixWireFrame.TryTakeFrame(buffer.AsSpan(0, count), out int frameLength))
                {
                    if (FixWireFrame.TryReadTag(buffer.AsSpan(0, frameLength), 35, out ReadOnlySpan<byte> msgType)
                        && msgType.Length == 1
                        && msgType[0] == (byte)'5')
                    {
                        return;
                    }

                    int remaining = count - frameLength;
                    if (remaining > 0)
                    {
                        Buffer.BlockCopy(buffer, frameLength, buffer, 0, remaining);
                    }

                    count = remaining;
                    continue;
                }

                int read = await stream.ReadAsync(buffer.AsMemory(count), timeout.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                count += read;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private bool IsAllowed(string senderCompId)
    {
        IReadOnlyList<string> allowed = Options.AllowedSenderCompIds;
        if (allowed.Count == 0)
        {
            return true;
        }

        for (int i = 0; i < allowed.Count; i++)
        {
            if (string.Equals(allowed[i], senderCompId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private void StopListener()
    {
        try
        {
            _listener?.Stop();
        }
        catch (SocketException ex)
        {
            Logger.LogDebug(ex, "Dispatcher {DispatcherId} já tinha fechado o listener.", DispatcherId);
        }
    }

    private TlsOptions CreateTlsOptions() =>
        new()
        {
            Enabled = true,
            CertificatePath = Options.TlsCertificatePath,
            CertificatePasswordEnvVar = Options.TlsCertificatePasswordEnvVar,
            TargetHost = Options.ListenAddress,
        };

    private static async Task DisposeQuietlyAsync(Stream stream, TcpClient client)
    {
        try
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }

        client.Dispose();
    }

    private static FixOutboundDispatcherOptions Validate(FixOutboundDispatcherOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.EnsureValid();
        return options;
    }

    private readonly struct PendingLogon
    {
        private PendingLogon(
            string senderCompId,
            bool resetSequence,
            long msgSeqNum,
            bool possDup,
            int heartBtInt,
            bool hasNextExpected,
            long nextExpected,
            byte[] prelude,
            int preludeLength)
        {
            SenderCompId = senderCompId;
            ResetSequence = resetSequence;
            MsgSeqNum = msgSeqNum;
            PossDup = possDup;
            HeartBtInt = heartBtInt;
            HasNextExpected = hasNextExpected;
            NextExpected = nextExpected;
            Prelude = prelude;
            PreludeLength = preludeLength;
        }

        internal string SenderCompId { get; }

        internal bool ResetSequence { get; }

        internal long MsgSeqNum { get; }

        internal bool PossDup { get; }

        internal int HeartBtInt { get; }

        internal bool HasNextExpected { get; }

        internal long NextExpected { get; }

        internal byte[] Prelude { get; }

        internal int PreludeLength { get; }

        internal static PendingLogon Identity(string senderCompId) =>
            new(senderCompId, false, 0, false, 0, false, 0, [], 0);

        internal static PendingLogon Accepted(
            string senderCompId,
            bool resetSequence,
            long msgSeqNum,
            bool possDup,
            int heartBtInt,
            bool hasNextExpected,
            long nextExpected) =>
            new(senderCompId, resetSequence, msgSeqNum, possDup, heartBtInt, hasNextExpected, nextExpected, [], 0);

        internal PendingLogon WithPrelude(byte[] prelude, int preludeLength) =>
            new(SenderCompId, ResetSequence, MsgSeqNum, PossDup, HeartBtInt, HasNextExpected, NextExpected, prelude, preludeLength);
    }

    /// <summary>
    /// Tabela das sessões logadas. A troca de uma sessão acontece fora do caminho de envio.
    /// </summary>
    private sealed class ConcurrentSessionTable
    {
        private readonly ConcurrentDictionary<string, FixAcceptedSession> _sessions = new(StringComparer.Ordinal);

        internal FixAcceptedSession? Replace(FixAcceptedSession session)
        {
            _sessions.TryRemove(session.SenderCompId, out FixAcceptedSession? previous);
            _sessions[session.SenderCompId] = session;
            return previous;
        }

        internal void RemoveIfSame(FixAcceptedSession session)
        {
            _sessions.TryRemove(new KeyValuePair<string, FixAcceptedSession>(session.SenderCompId, session));
        }

        internal bool TryGet(string senderCompId, out FixAcceptedSession? session) =>
            _sessions.TryGetValue(senderCompId, out session);

        internal FixAcceptedSession[] Snapshot()
        {
            int count = _sessions.Count;
            if (count == 0)
            {
                return [];
            }

            var copy = new FixAcceptedSession[count];
            int index = 0;
            foreach (FixAcceptedSession session in _sessions.Values)
            {
                if (index == copy.Length)
                {
                    Array.Resize(ref copy, copy.Length + 4);
                }

                copy[index++] = session;
            }

            if (index == copy.Length)
            {
                return copy;
            }

            Array.Resize(ref copy, index);
            return copy;
        }

        internal FixAcceptedSession[] Drain()
        {
            FixAcceptedSession[] snapshot = Snapshot();
            _sessions.Clear();
            return snapshot;
        }
    }
}
