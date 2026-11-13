using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

using Microsoft.Extensions.Logging;

using NexusRouter.Domain.Abstractions;
using NexusRouter.Domain.Enums;
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
    private static readonly byte[] DayRolloverText = Encoding.ASCII.GetBytes("Trading day rollover");

    private readonly ExponentialBackoffReconnectPolicy _acceptRetry;
    private readonly ConcurrentSessionTable _sessions = new();
    private readonly ConcurrentDictionary<string, BackfillState> _backfills = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Task, byte> _handlers = new();
    private readonly SemaphoreSlim _adoptGate = new(1, 1);
    private readonly ISecretResolver _secretResolver;
    private readonly ISessionLog _sessionLog;
    private readonly Func<ushort, DeliveryMode> _resendDeliveryMode;
    private readonly object _knownGate = new();
    private ClientBinding[] _known = [];
    private readonly HashSet<string> _clientNames = new(StringComparer.Ordinal);

    private TcpListener? _listener;
    private CancellationTokenSource? _acceptCts;
    private Task? _accept;
    private int _stopping;
    private int _dayRollover;
    private int _rolloverGateHeld;
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
    /// <param name="resendDeliveryMode">Modo de entrega da sessão de ingress dona de um stream, usado no reenvio. Nulo reenvia em PassThrough.</param>
    /// <exception cref="ArgumentException">As options são inválidas.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/>, <paramref name="secretResolver"/> ou <paramref name="sessionLog"/> é nulo.</exception>
    public FixOutboundDispatcher(
        string dispatcherId,
        FixOutboundDispatcherOptions options,
        ILogger<FixOutboundDispatcher> logger,
        DispatchPayloadPool payloadPool,
        ISecretResolver secretResolver,
        ISessionLog sessionLog,
        StreamCatchUp? catchUp = null,
        Func<ushort, DeliveryMode>? resendDeliveryMode = null)
        : base(dispatcherId, Validate(options), options.ChannelCapacity, payloadPool, logger, catchUp)
    {
        ArgumentNullException.ThrowIfNull(secretResolver);
        ArgumentNullException.ThrowIfNull(sessionLog);
        _secretResolver = secretResolver;
        _sessionLog = sessionLog;
        _resendDeliveryMode = resendDeliveryMode ?? (static _ => DeliveryMode.PassThrough);
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

        // Os handshakes em andamento usam o índice e o log de sessão. Terminam antes de o host parar o log.
        Task[] pending = [.. _handlers.Keys];
        if (pending.Length > 0)
        {
            try
            {
                await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                Logger.LogWarning(ex, "Dispatcher {DispatcherId} não esperou todos os handshakes FIX terminarem.", DispatcherId);
            }
        }

        foreach (FixAcceptedSession session in snapshot)
        {
            await session.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        _sessionLog.DayRolloverStarting -= OnDayRolloverStartingAsync;
        _sessionLog.DayRolloverCompleted -= OnDayRolloverCompleted;
    }

    /// <inheritdoc />
    protected override ValueTask ConnectAsync(CancellationToken cancellationToken)
    {
        IPAddress address = IPAddress.Parse(Options.ListenAddress);
        LoadKnownClients();
        _listener = new TcpListener(address, Options.ListenPort);
        _listener.Start();
        _sessionLog.DayRolloverStarting += OnDayRolloverStartingAsync;
        _sessionLog.DayRolloverCompleted += OnDayRolloverCompleted;
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
        if (Volatile.Read(ref _dayRollover) != 0)
        {
            return ValueTask.CompletedTask;
        }

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
            string sender = SenderOf(index);
            if (_backfills.TryGetValue(sender, out BackfillState? backfill)
                && backfill.TryQueue(payload))
            {
                continue;
            }

            long sequence = index.ReserveSeq();
            index.PublishApp(sequence, payload.LogRef);
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

        lock (_knownGate)
        {
            _clientNames.Clear();
            foreach (ClientBinding binding in buffer)
            {
                _clientNames.Add(SenderOf(binding.Index));
            }
        }

        Volatile.Write(ref _known, buffer);
    }

    /// <summary>
    /// Abre os arquivos do cliente, desde que ele já exista ou ainda haja vaga em <c>MaxClients</c>.
    /// A checagem e a criação ficam no mesmo lock, então dois Logons novos simultâneos não passam do limite.
    /// </summary>
    private bool TryOpenClient(string senderCompId, out ClientBinding binding)
    {
        lock (_knownGate)
        {
            if (!_clientNames.Contains(senderCompId) && _clientNames.Count >= Options.MaxClients)
            {
                binding = default;
                return false;
            }

            binding = _sessionLog.OpenClient(DispatcherId, senderCompId);
            _clientNames.Add(senderCompId);
            return true;
        }
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
            Task handler = HandleClientAsync(client, cancellationToken);
            _handlers[handler] = 0;
            _ = handler.ContinueWith(
                static (finished, state) => ((ConcurrentDictionary<Task, byte>)state!).TryRemove(finished, out _),
                _handlers,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        Stream? stream = null;
        FixAcceptedSession? session = null;
        ClientBinding binding = default;
        BackfillState? backfillState = null;
        bool backfillStarted = false;
        bool clientOpened = false;
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

            if (Volatile.Read(ref _dayRollover) != 0)
            {
                await WriteRejectLogoutAsync(
                    stream,
                    logon.Value.SenderCompId,
                    "Trading day rollover",
                    Options.Recovery.LogoutTimeoutSeconds,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            // O SenderCompID vem da rede e vira nome de pasta. Valida antes de tocar no disco.
            if (!ClientKey.TryCreateFix(logon.Value.SenderCompId, out _))
            {
                AcceptorSessionMetrics.ClientsRejected.Add(1, new KeyValuePair<string, object?>("reason", "invalid_key"));
                Logger.LogWarning("Dispatcher {DispatcherId} recusou Logon com SenderCompID inválido.", DispatcherId);
                await WriteRejectLogoutAsync(stream, "UNKNOWN", "Logon rejected", Options.Recovery.LogoutTimeoutSeconds, cancellationToken).ConfigureAwait(false);
                return;
            }

            AcceptorRecoveryOptions recovery = RecoveryFor(logon.Value.SenderCompId);
            if (logon.Value.InvalidReplayMode)
            {
                await WriteRejectLogoutAsync(
                    stream,
                    logon.Value.SenderCompId,
                    "Invalid ReplayMode",
                    recovery.LogoutTimeoutSeconds,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

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

            if (!TryOpenClient(logon.Value.SenderCompId, out binding))
            {
                AcceptorSessionMetrics.ClientsRejected.Add(1, new KeyValuePair<string, object?>("reason", "max_clients"));
                Logger.LogWarning(
                    "Dispatcher {DispatcherId} recusou o Logon de {SenderCompId}: MaxClients {MaxClients} atingido.",
                    DispatcherId,
                    logon.Value.SenderCompId,
                    Options.MaxClients);
                await WriteRejectLogoutAsync(stream, logon.Value.SenderCompId, "Too many clients", recovery.LogoutTimeoutSeconds, cancellationToken).ConfigureAwait(false);
                return;
            }

            clientOpened = true;
            ReplayMode requestedReplayMode = logon.Value.ReplayMode ?? recovery.ReplayMode;
            LogonDecision decision = await DecideLogonAsync(
                logon.Value,
                recovery,
                binding,
                async token =>
                {
                    if (requestedReplayMode == ReplayMode.StartOfDay)
                    {
                        backfillState = new BackfillState();
                        _backfills[logon.Value.SenderCompId] = backfillState;
                    }

                    if (_sessions.TryGet(logon.Value.SenderCompId, out FixAcceptedSession? current)
                        && current is not null)
                    {
                        await current.StopAsync(token).ConfigureAwait(false);
                    }
                },
                cancellationToken).ConfigureAwait(false);
            string? reject = decision.Reject;
            long skipFrom = decision.SkipFrom;
            long resumeFrom = decision.ResumeFrom;
            bool gapFillLogon = decision.GapFillLogon;
            bool echoReset = decision.EchoReset;
            bool replayModeApplied = decision.ReplayModeApplied;
            if (reject is null)
            {
                Remember(binding);
                RecordReplayMode(logon.Value.SenderCompId, requestedReplayMode, replayModeApplied);
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
                ResumeFrom = reject is null ? resumeFrom : 0,
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
                Logger,
                _sessionLog.Reader,
                _resendDeliveryMode,
                Options.SlotPublishTimeout);
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
                FixAcceptedSession acceptedSession = session;
                await acceptedSession.StartAsync(cancellationToken).ConfigureAwait(false);
                if (backfillState is not null)
                {
                    backfillStarted = true;
                    await CompleteStartOfDayBackfillAsync(
                        binding,
                        acceptedSession,
                        backfillState,
                        cancellationToken).ConfigureAwait(false);
                }

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
            if (backfillState is not null && !backfillStarted && clientOpened)
            {
                PublishAbandonedBackfill(binding, backfillState);
            }

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

    private async ValueTask OnDayRolloverStartingAsync(CancellationToken cancellationToken)
    {
        await _adoptGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _rolloverGateHeld, 1);
        Volatile.Write(ref _dayRollover, 1);

        FixAcceptedSession[] sessions = _sessions.Drain();
        foreach (FixAcceptedSession session in sessions)
        {
            await session.StopForDayRolloverAsync(DayRolloverText, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private void OnDayRolloverCompleted()
    {
        Volatile.Write(ref _dayRollover, 0);
        if (Interlocked.Exchange(ref _rolloverGateHeld, 0) != 0)
        {
            _adoptGate.Release();
        }
    }

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
        bool hasReplayMode = FixWireFrame.TryReadTag(frame, 19001, out ReadOnlySpan<byte> replayModeRaw);
        ReplayMode? replayMode = null;
        bool invalidReplayMode = false;
        if (hasReplayMode)
        {
            if (replayModeRaw.Length == 1 && replayModeRaw[0] == (byte)'1')
            {
                replayMode = ReplayMode.FromLogon;
            }
            else if (replayModeRaw.Length == 1 && replayModeRaw[0] == (byte)'2')
            {
                replayMode = ReplayMode.StartOfDay;
            }
            else
            {
                invalidReplayMode = true;
            }
        }

        logon = PendingLogon.Accepted(
            senderCompId,
            resetSequence,
            msgSeqNum,
            possDup,
            heartBtInt,
            hasNext,
            hasNext ? next : 0,
            replayMode,
            invalidReplayMode);
        return true;
    }

    private async Task<LogonDecision> DecideLogonAsync(
        PendingLogon logon,
        AcceptorRecoveryOptions recovery,
        ClientBinding binding,
        Func<CancellationToken, ValueTask>? onEpochOpening,
        CancellationToken cancellationToken)
    {
        long skipFrom = 0;
        long resumeFrom = 0;
        bool gapFillLogon = false;
        bool echoReset = false;
        bool replayModeApplied = false;
        if (logon.ResetSequence)
        {
            if (logon.HasNextExpected && recovery.AcceptNextExpectedMsgSeqNum && logon.NextExpected > 1)
            {
                NoteClientAhead(logon.SenderCompId, logon.NextExpected, 1);
                return new LogonDecision("MsgSeqNum too high", 0, 0, false, false, false);
            }

            if (onEpochOpening is not null)
            {
                await onEpochOpening(cancellationToken).ConfigureAwait(false);
            }

            ClientBinding reset = _sessionLog.ResetClient(DispatcherId, logon.SenderCompId);
            reset.State.NextInExpected = logon.MsgSeqNum + 1;
            echoReset = true;
            replayModeApplied = true;
            return new LogonDecision(null, 0, 0, false, echoReset, replayModeApplied);
        }

        bool opensEpoch = binding.State.NextInExpected == 1;
        if (opensEpoch)
        {
            if (logon.HasNextExpected && recovery.AcceptNextExpectedMsgSeqNum && logon.NextExpected > 1)
            {
                NoteClientAhead(logon.SenderCompId, logon.NextExpected, 1);
                return new LogonDecision("MsgSeqNum too high", 0, 0, false, false, false);
            }

            if (onEpochOpening is not null)
            {
                await onEpochOpening(cancellationToken).ConfigureAwait(false);
            }

            ClientBinding reset = _sessionLog.ResetClient(DispatcherId, logon.SenderCompId);
            reset.State.NextInExpected = 1;
            replayModeApplied = true;
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
            return new LogonDecision("MsgSeqNum too low", 0, 0, false, false, false);
        }

        long nextOut = binding.Index.NextOut;
        if (!opensEpoch && logon.HasNextExpected && recovery.AcceptNextExpectedMsgSeqNum)
        {
            if (logon.NextExpected > nextOut)
            {
                NoteClientAhead(logon.SenderCompId, logon.NextExpected, nextOut);
                return new LogonDecision("MsgSeqNum too high", 0, 0, false, false, false);
            }

            if (logon.NextExpected < nextOut)
            {
                if (recovery.OnReconnectGap == OnReconnectGap.SkipToCurrent)
                {
                    skipFrom = logon.NextExpected;
                }
                else
                {
                    // Modo 2 (§3.4.7): com 789 = N o router reenvia N..M-1 pelo .cidx com PossDupFlag=Y
                    // e depois cobre o seq do Logon de resposta com um gap-fill. A sessão faz isso ao iniciar.
                    resumeFrom = logon.NextExpected;
                    gapFillLogon = true;
                }
            }
        }

        if (logon.MsgSeqNum >= expected)
        {
            binding.State.NextInExpected = logon.MsgSeqNum + 1;
        }

        return new LogonDecision(null, skipFrom, resumeFrom, gapFillLogon, echoReset, replayModeApplied);
    }

    private void RecordReplayMode(string senderCompId, ReplayMode replayMode, bool applied)
    {
        string mode = replayMode.ToString();
        string appliedValue = applied ? "true" : "false";
        AcceptorSessionMetrics.ReplayMode.Add(
            1,
            new KeyValuePair<string, object?>("dispatcher_id", DispatcherId),
            new KeyValuePair<string, object?>("mode", mode),
            new KeyValuePair<string, object?>("applied", appliedValue));
        Logger.LogInformation(
            "Dispatcher {DispatcherId} aceitou Logon de {SenderCompId} com ReplayMode {ReplayMode}; aplicado: {Applied}.",
            DispatcherId,
            senderCompId,
            mode,
            applied);
    }

    private async Task CompleteStartOfDayBackfillAsync(
        ClientBinding binding,
        FixAcceptedSession session,
        BackfillState backfill,
        CancellationToken cancellationToken)
    {
        int replayed = 0;
        bool completed = false;
        try
        {
            TradingDayStreamSnapshot[] snapshots = _sessionLog.GetTradingDayStreams();
            var pending = new PriorityQueue<BackfillCursor, (long Timestamp, ushort Stream, int Epoch, long Sequence)>();
            foreach (TradingDayStreamSnapshot snapshot in snapshots)
            {
                var cursor = new BackfillCursor(snapshot);
                if (TryAdvanceBackfillCursor(cursor))
                {
                    pending.Enqueue(
                        cursor,
                        (cursor.Timestamp, snapshot.Stream, snapshot.Epoch, cursor.CurrentSequence));
                }
            }

            while (pending.TryDequeue(out BackfillCursor? cursor, out _))
            {
                ReadOnlyMemory<byte> frame = cursor.Buffer.WrittenMemory;
                await session.EnqueueReplayAsync(
                    frame,
                    _resendDeliveryMode(cursor.CurrentRef.Stream),
                    cursor.CurrentRef,
                    cancellationToken).ConfigureAwait(false);
                backfill.MarkReplayed(cursor.CurrentRef);
                replayed++;

                if (TryAdvanceBackfillCursor(cursor))
                {
                    pending.Enqueue(
                        cursor,
                        (cursor.Timestamp, cursor.Snapshot.Stream, cursor.Snapshot.Epoch, cursor.CurrentSequence));
                }
            }

            var liveFrame = new ArrayBufferWriter<byte>(2048);
            while (true)
            {
                if (backfill.TryDequeue(out BackfillPayload queued))
                {
                    if (!TryReadBackfillPayload(queued, liveFrame, out ReadOnlyMemory<byte> frame))
                    {
                        Logger.LogWarning(
                            "Dispatcher {DispatcherId} não conseguiu ler mensagem adiada para {SenderCompId}; LogRef {LogRef}.",
                            DispatcherId,
                            session.SenderCompId,
                            queued.LogRef);
                        continue;
                    }

                    bool isApplication = FixWireFrame.TryReadTag(frame.Span, 35, out ReadOnlySpan<byte> msgType)
                        && !FixAdminMessageTypes.IsAdmin(msgType);
                    if (isApplication && backfill.WasReplayed(queued.LogRef))
                    {
                        continue;
                    }

                    await session.EnqueueBufferedAsync(
                        frame,
                        queued.DeliveryMode,
                        queued.LogRef,
                        cancellationToken).ConfigureAwait(false);
                    replayed++;

                    continue;
                }

                if (backfill.TryFinishIfEmpty())
                {
                    completed = true;
                    break;
                }
            }

            AcceptorSessionMetrics.StartOfDayBackfill.Add(
                replayed,
                new KeyValuePair<string, object?>("dispatcher_id", DispatcherId));
            Logger.LogInformation(
                "Dispatcher {DispatcherId} entregou {Messages} mensagens no backfill StartOfDay para {SenderCompId}.",
                DispatcherId,
                replayed,
                session.SenderCompId);
        }
        finally
        {
            if (!completed)
            {
                PublishAbandonedBackfill(binding, backfill);
            }
        }
    }

    private bool TryAdvanceBackfillCursor(BackfillCursor cursor)
    {
        while (cursor.NextSequence <= cursor.Snapshot.Committed)
        {
            long sequence = cursor.NextSequence;
            cursor.NextSequence = sequence == long.MaxValue ? long.MinValue : sequence + 1;
            cursor.Buffer.ResetWrittenCount();
            if (!_sessionLog.Reader.TryRead(
                cursor.Snapshot.Stream,
                cursor.Snapshot.Epoch,
                sequence,
                cursor.Buffer,
                out long timestamp))
            {
                continue;
            }

            if (!FixWireFrame.TryReadTag(cursor.Buffer.WrittenSpan, 35, out ReadOnlySpan<byte> msgType)
                || FixAdminMessageTypes.IsAdmin(msgType))
            {
                continue;
            }

            cursor.Timestamp = timestamp;
            cursor.CurrentSequence = sequence;
            cursor.CurrentRef = new LogRef(cursor.Snapshot.Stream, cursor.Snapshot.Epoch, sequence);
            return true;
        }

        return false;
    }

    private bool TryReadBackfillPayload(
        BackfillPayload payload,
        ArrayBufferWriter<byte> destination,
        out ReadOnlyMemory<byte> frame)
    {
        if (HasLogReference(payload.LogRef))
        {
            destination.ResetWrittenCount();
            if (_sessionLog.Reader.TryRead(
                payload.LogRef.Stream,
                payload.LogRef.Epoch,
                payload.LogRef.InSeq,
                destination))
            {
                frame = destination.WrittenMemory;
                return true;
            }

            frame = default;
            return false;
        }

        if (payload.Frame is not null)
        {
            frame = payload.Frame;
            return true;
        }

        frame = default;
        return false;
    }

    private void PublishAbandonedBackfill(ClientBinding binding, BackfillState backfill)
    {
        BackfillPayload[] queued = backfill.StopAndDrain();
        int published = 0;
        foreach (BackfillPayload payload in queued)
        {
            if (backfill.WasReplayed(payload.LogRef))
            {
                continue;
            }

            long sequence = binding.Index.ReserveSeq();
            binding.Index.PublishApp(sequence, payload.LogRef);
            published++;
        }

        if (published > 0)
        {
            Logger.LogWarning(
                "Dispatcher {DispatcherId} publicou {Messages} mensagens adiadas no índice após interromper o backfill.",
                DispatcherId,
                published);
        }
    }

    private static bool HasLogReference(LogRef logRef) => logRef.Stream != 0 && logRef.InSeq > 0;

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

    private sealed class BackfillState
    {
        private readonly object _gate = new();
        private readonly Queue<BackfillPayload> _queued = new();
        private readonly Dictionary<(ushort Stream, int Epoch), long> _replayedThrough = [];
        private bool _active = true;

        internal bool TryQueue(DispatchPayload payload)
        {
            lock (_gate)
            {
                if (!_active)
                {
                    return false;
                }

                byte[]? frame = null;
                if (!HasLogReference(payload.LogRef))
                {
                    IMemoryOwner<byte> raw = payload.RawBuffer
                        ?? throw new InvalidOperationException("O payload FIX outbound não tem buffer.");
                    frame = raw.Memory.ToArray();
                }

                _queued.Enqueue(new BackfillPayload(payload.LogRef, payload.DeliveryMode, frame));
                return true;
            }
        }

        internal bool TryDequeue(out BackfillPayload payload)
        {
            lock (_gate)
            {
                if (_queued.TryDequeue(out payload))
                {
                    return true;
                }
            }

            payload = default;
            return false;
        }

        internal bool TryFinishIfEmpty()
        {
            lock (_gate)
            {
                if (_queued.Count != 0)
                {
                    return false;
                }

                _active = false;
                return true;
            }
        }

        internal void MarkReplayed(LogRef logRef)
        {
            lock (_gate)
            {
                var key = (logRef.Stream, logRef.Epoch);
                if (!_replayedThrough.TryGetValue(key, out long current) || logRef.InSeq > current)
                {
                    _replayedThrough[key] = logRef.InSeq;
                }
            }
        }

        internal bool WasReplayed(LogRef logRef)
        {
            lock (_gate)
            {
                return HasLogReference(logRef)
                    && _replayedThrough.TryGetValue((logRef.Stream, logRef.Epoch), out long sequence)
                    && logRef.InSeq <= sequence;
            }
        }

        internal BackfillPayload[] StopAndDrain()
        {
            lock (_gate)
            {
                _active = false;
                BackfillPayload[] queued = [.. _queued];
                _queued.Clear();
                return queued;
            }
        }
    }

    private sealed class BackfillCursor(TradingDayStreamSnapshot snapshot)
    {
        internal TradingDayStreamSnapshot Snapshot { get; } = snapshot;

        internal ArrayBufferWriter<byte> Buffer { get; } = new(2048);

        internal long NextSequence { get; set; } = snapshot.FirstSequence;

        internal long CurrentSequence { get; set; }

        internal long Timestamp { get; set; }

        internal LogRef CurrentRef { get; set; }
    }

    private readonly record struct BackfillPayload(LogRef LogRef, DeliveryMode DeliveryMode, byte[]? Frame);

    private readonly record struct LogonDecision(
        string? Reject,
        long SkipFrom,
        long ResumeFrom,
        bool GapFillLogon,
        bool EchoReset,
        bool ReplayModeApplied);

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
            ReplayMode? replayMode,
            bool invalidReplayMode,
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
            ReplayMode = replayMode;
            InvalidReplayMode = invalidReplayMode;
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

        internal ReplayMode? ReplayMode { get; }

        internal bool InvalidReplayMode { get; }

        internal byte[] Prelude { get; }

        internal int PreludeLength { get; }

        internal static PendingLogon Identity(string senderCompId) =>
            new(senderCompId, false, 0, false, 0, false, 0, null, false, [], 0);

        internal static PendingLogon Accepted(
            string senderCompId,
            bool resetSequence,
            long msgSeqNum,
            bool possDup,
            int heartBtInt,
            bool hasNextExpected,
            long nextExpected,
            ReplayMode? replayMode,
            bool invalidReplayMode) =>
            new(
                senderCompId,
                resetSequence,
                msgSeqNum,
                possDup,
                heartBtInt,
                hasNextExpected,
                nextExpected,
                replayMode,
                invalidReplayMode,
                [],
                0);

        internal PendingLogon WithPrelude(byte[] prelude, int preludeLength) =>
            new(
                SenderCompId,
                ResetSequence,
                MsgSeqNum,
                PossDup,
                HeartBtInt,
                HasNextExpected,
                NextExpected,
                ReplayMode,
                InvalidReplayMode,
                prelude,
                preludeLength);
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
