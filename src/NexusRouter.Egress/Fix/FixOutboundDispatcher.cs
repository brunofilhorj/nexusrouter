using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

using Microsoft.Extensions.Logging;

using NexusRouter.Domain.Models;
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
/// Sem sessão logada, a mensagem é descartada neste dispatcher.
/// </para>
/// </remarks>
public sealed class FixOutboundDispatcher : DispatcherBase<FixOutboundDispatcherOptions>
{
    private const int ReadBufferBytes = 64 * 1024;

    private readonly ExponentialBackoffReconnectPolicy _acceptRetry;
    private readonly ConcurrentSessionTable _sessions = new();
    private readonly SemaphoreSlim _adoptGate = new(1, 1);

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
    /// <exception cref="ArgumentException">As options são inválidas.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> é nulo.</exception>
    public FixOutboundDispatcher(
        string dispatcherId,
        FixOutboundDispatcherOptions options,
        ILogger<FixOutboundDispatcher> logger)
        : base(dispatcherId, Validate(options), options.ChannelCapacity, logger)
    {
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
        FixAcceptedSession[] sessions = _sessions.Snapshot();
        if (sessions.Length == 0)
        {
            Logger.LogTrace(
                "Dispatcher {DispatcherId} não tem sessão FIX logada. A mensagem foi descartada.",
                DispatcherId);
            return ValueTask.CompletedTask;
        }

        for (int i = 0; i < sessions.Length; i++)
        {
            FixAcceptedSession session = sessions[i];
            try
            {
                if (!session.TryEnqueue(payload))
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
        SequenceNumberStore? store = null;
        bool ownsSocket = true;
        try
        {
            stream = Options.TlsEnabled
                ? await TlsSocketFactory.WrapServerAsync(
                    client,
                    CreateTlsOptions(),
                    Options.TlsRequireClientCertificate,
                    cancellationToken).ConfigureAwait(false)
                : client.GetStream();

            PendingLogon? logon = await TryReadLogonAsync(stream, cancellationToken).ConfigureAwait(false);
            if (logon is null)
            {
                return;
            }

            store = new SequenceNumberStore(Options.SequenceStoreDirectory, Logger);
            await store.LoadAsync(DispatcherId + "_" + logon.Value.SenderCompId, cancellationToken).ConfigureAwait(false);
            if (logon.Value.ResetSequence)
            {
                await store.ResetAsync(cancellationToken).ConfigureAwait(false);
            }

            var sessionOptions = new SessionOptions
            {
                SessionId = DispatcherId + "-" + logon.Value.SenderCompId,
                Host = Options.ListenAddress,
                Port = Options.ListenPort,
                SenderCompId = Options.SenderCompId,
                TargetCompId = logon.Value.SenderCompId,
                BeginString = Options.BeginString,
                HeartBtInt = Options.HeartBtInt,
                LogonTimeout = Options.LogonTimeout,
            };
            session = new FixAcceptedSession(
                logon.Value.SenderCompId,
                client,
                stream,
                store,
                sessionOptions,
                Options.ChannelCapacity,
                Options.HeartBtInt,
                logon.Value.Prelude,
                logon.Value.PreludeLength,
                OnSessionClosed,
                Logger);
            ownsSocket = false;
            store = null;
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
                if (store is not null)
                {
                    await store.DisposeAsync().ConfigureAwait(false);
                }

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
                    if (!TryAcceptLogon(buffer.AsSpan(0, frameLength), out string senderCompId, out bool resetSequence))
                    {
                        Logger.LogWarning(
                            "Dispatcher {DispatcherId} recusou Logon. TargetCompID esperado {SenderCompId}.",
                            DispatcherId,
                            Options.SenderCompId);
                        return null;
                    }

                    int preludeLength = count - frameLength;
                    byte[] prelude = preludeLength == 0 ? [] : new byte[preludeLength];
                    if (preludeLength > 0)
                    {
                        buffer.AsSpan(frameLength, preludeLength).CopyTo(prelude);
                    }

                    return new PendingLogon(senderCompId, resetSequence, prelude, preludeLength);
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

    private bool TryAcceptLogon(ReadOnlySpan<byte> frame, out string senderCompId, out bool resetSequence)
    {
        senderCompId = string.Empty;
        resetSequence = false;
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
            || !FixWireFrame.TryDecodeSessionKey(sender, out senderCompId)
            || !IsAllowed(senderCompId))
        {
            senderCompId = string.Empty;
            return false;
        }

        resetSequence = FixWireFrame.TryReadTag(frame, 141, out ReadOnlySpan<byte> reset) && reset.Length == 1 && reset[0] == (byte)'Y';
        return true;
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
        internal PendingLogon(string senderCompId, bool resetSequence, byte[] prelude, int preludeLength)
        {
            SenderCompId = senderCompId;
            ResetSequence = resetSequence;
            Prelude = prelude;
            PreludeLength = preludeLength;
        }

        internal string SenderCompId { get; }

        internal bool ResetSequence { get; }

        internal byte[] Prelude { get; }

        internal int PreludeLength { get; }
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
