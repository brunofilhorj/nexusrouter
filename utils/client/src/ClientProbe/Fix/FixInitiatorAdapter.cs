using System.Net.Sockets;
using System.Text;
using ClientProbe.Probe;
using ClientProbe.Telemetry;
using Microsoft.Extensions.Options;

namespace ClientProbe.Fix;

/// <summary>
/// Minimal FIX 4.4 initiator against the NexusRouter acceptor. Sends Logon, waits for the Logon back
/// (state <see cref="ProbeState.Live"/>), sends Heartbeat when idle and Logout on disconnect.
/// </summary>
/// <remarks>
/// <para>
/// Sequence numbers live in <see cref="FixSeqStore"/>, keyed by SenderCompID and TargetCompID, so a
/// reconnect continues where the previous connection stopped. <see cref="FixProfile.ResetSeqNum"/>
/// sends 141=Y and restarts both sides at 1. <see cref="FixProfile.SendNextExpected"/> sends 789 with
/// the next MsgSeqNum the probe expects, so the acceptor resends what was missed.
/// <see cref="FixProfile.ReplayMode"/> sends tag 19001 when set.
/// </para>
/// <para>
/// Incoming MsgSeqNum is checked: a gap without 789 triggers one ResendRequest (7=first missing, 16=0);
/// SequenceReset (35=4) moves the expected number forward; a ResendRequest from the acceptor is answered
/// with one SequenceReset-GapFill, because the probe sends only session messages. Application frames,
/// including PossDup resends, Reject and Logout, go to telemetry. Logon, ResendRequest and SequenceReset
/// do too, in both directions. Heartbeat and TestRequest stay off the table.
/// </para>
/// </remarks>
public sealed class FixInitiatorAdapter : ProbeAdapterBase, IFixSessionSource
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LogonTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HeartbeatTick = TimeSpan.FromSeconds(1);

    private readonly TimeProvider _time;
    private readonly FixSeqStore _store;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private ActiveSession? _active;
    private volatile FixSessionSnapshot? _snapshot;

    /// <summary>Creates the adapter.</summary>
    /// <param name="publisher">Telemetry entry point.</param>
    /// <param name="options">Probe options. <c>Fix.StateDirectory</c> enables sequence persistence.</param>
    /// <param name="time">Clock.</param>
    public FixInitiatorAdapter(ITelemetryPublisher publisher, IOptions<ClientProbeOptions> options, TimeProvider time)
        : base(ProbeTransport.Fix, publisher, options, time)
    {
        _time = time;
        _store = new FixSeqStore(ResolveDirectory(options.Value.Fix.StateDirectory));
    }

    /// <inheritdoc />
    public FixSessionSnapshot? Session
    {
        get
        {
            var active = _active;
            return active is null ? _snapshot : active.Snapshot();
        }
    }

    /// <inheritdoc />
    public override async Task ConnectAsync(ProbeProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile is not FixProfile fix)
        {
            throw new ArgumentException("FIX adapter requires a FixProfile.", nameof(profile));
        }

        await DisconnectAsync(ct).ConfigureAwait(false);
        Counters.Reset();
        SetState(ProbeState.Connecting);
        SetEndpoint(fix.Endpoint);

        var key = FixSeqStore.Key(fix.SenderCompId, fix.TargetCompId);
        if (fix.ResetSeqNum)
        {
            _store.Set(key, FixSeqState.Initial);
            _store.Flush(key);
        }

        var stored = _store.Get(key);
        var client = new TcpClient { NoDelay = true };
        var session = new ActiveSession(client, fix, new FixSessionWriter(fix.SenderCompId, fix.TargetCompId, _time), key)
        {
            OutSeq = stored.LastOut,
            NextIn = stored.NextInExpected,
        };

        try
        {
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectCts.CancelAfter(ConnectTimeout);
                await client.ConnectAsync(fix.Host, fix.Port, connectCts.Token).ConfigureAwait(false);
            }

            session.Stream = client.GetStream();
            _active = session;
            session.ReadLoop = Task.Run(() => ReadLoopAsync(session), CancellationToken.None);
            int? nextExpected = fix.SendNextExpected ? session.NextIn : null;
            int? replayMode = fix.ReplayMode is { } mode ? (int)mode : null;
            await SendAsync(session, seq => session.Writer.Logon(seq, fix.HeartBtInt, fix.ResetSeqNum, nextExpected, replayMode), CancellationToken.None)
                .ConfigureAwait(false);
            session.HeartbeatLoop = Task.Run(() => HeartbeatLoopAsync(session), CancellationToken.None);

            await session.LogonReceived.Task.WaitAsync(LogonTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await FaultAsync(session, "Logon não confirmado pelo acceptor a tempo.").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await FaultAsync(session, $"Timeout ao conectar em {fix.Endpoint}.").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await StopAsync(session, sendLogout: false).ConfigureAwait(false);
            ClearAfterStop(session);
            throw;
        }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            await FaultAsync(session, ex.Message).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public override async Task DisconnectAsync(CancellationToken ct)
    {
        var session = _active;
        if (session is not null)
        {
            await StopAsync(session, sendLogout: State == ProbeState.Live).ConfigureAwait(false);
            ClearAfterStop(session);
        }

        _snapshot = null;
        SetState(ProbeState.Disconnected);
        SetEndpoint("");
    }

    private async Task ReadLoopAsync(ActiveSession session)
    {
        var scanner = new FixFrameScanner();
        var buffer = new byte[8192];
        try
        {
            while (!session.Cts.IsCancellationRequested)
            {
                var read = await session.Stream!.ReadAsync(buffer, session.Cts.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    await FaultAsync(session, "Conexão encerrada pelo acceptor.").ConfigureAwait(false);
                    return;
                }

                scanner.Append(buffer.AsSpan(0, read));
                while (scanner.TryRead(out var frame))
                {
                    if (!await HandleFrameAsync(session, frame).ConfigureAwait(false))
                    {
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disconnect requested.
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
            if (!session.Cts.IsCancellationRequested)
            {
                await FaultAsync(session, ex.Message).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Handles one frame. Returns false when the session ended.</summary>
    private async Task<bool> HandleFrameAsync(ActiveSession session, byte[] frame)
    {
        var msgType = GetMsgType(frame);
        var hasSeq = TryGetInt(frame, "\u000134="u8, out var seq);
        if (hasSeq)
        {
            session.LastReceivedSeq = seq;
        }

        if (msgType == "4")
        {
            OnFrame(frame);
            // SequenceReset: GapFill or Reset both move the expected number forward. Never backwards.
            if (TryGetInt(frame, "\u000136="u8, out var newSeqNo) && newSeqNo > session.NextIn)
            {
                session.NextIn = newSeqNo;
            }

            ClearGapIfFilled(session);
            session.Dirty = true;
            return true;
        }

        if (hasSeq)
        {
            await CheckIncomingSeqAsync(session, frame, msgType, seq).ConfigureAwait(false);
        }

        switch (msgType)
        {
            case "0":
                return true;
            case "1":
                var testReqId = GetTag(frame, "\u0001112="u8);
                await SendAsync(session, n => session.Writer.Heartbeat(n, testReqId), session.Cts.Token).ConfigureAwait(false);
                return true;
            case "2":
                OnFrame(frame);
                // The probe sends only session messages, so one gap fill covers any resend range.
                if (TryGetInt(frame, "\u00017="u8, out var beginSeqNo) && beginSeqNo > 0)
                {
                    await SendGapFillAsync(session, beginSeqNo, session.Cts.Token).ConfigureAwait(false);
                }

                return true;
            case "A":
                OnFrame(frame);
                if (State == ProbeState.Connecting)
                {
                    SetState(ProbeState.Live);
                }

                session.LogonReceived.TrySetResult();
                return true;
            case "5":
                OnFrame(frame);
                var text = GetTag(frame, "\u000158="u8);
                await FaultAsync(session, string.IsNullOrEmpty(text) ? "Logout recebido do acceptor." : $"Logout recebido do acceptor: {text}").ConfigureAwait(false);
                return false;
            default:
                OnFrame(frame);
                return true;
        }
    }

    /// <summary>Advances the expected MsgSeqNum, or reacts to a gap or a number below the expected one.</summary>
    private async Task CheckIncomingSeqAsync(ActiveSession session, byte[] frame, string? msgType, int seq)
    {
        if (msgType == "A" && GetTag(frame, "\u0001141="u8) == "Y")
        {
            // Acceptor confirmed the reset: its Logon is seq 1 of the new sequence.
            session.NextIn = seq + 1;
            session.GapFrom = 0;
            session.Dirty = true;
            return;
        }

        if (seq == session.NextIn)
        {
            session.NextIn = seq + 1;
            ClearGapIfFilled(session);
            session.Dirty = true;
            return;
        }

        if (seq > session.NextIn)
        {
            if (session.GapFrom != 0)
            {
                // Resend already pending; the acceptor sends up to the latest (16=0).
                session.GapUntil = Math.Max(session.GapUntil, seq);
                return;
            }

            session.GapFrom = session.NextIn;
            session.GapUntil = seq;
            if (msgType == "A" && session.Profile.SendNextExpected)
            {
                // With 789 the acceptor resends NextIn..seq-1 on its own and gap-fills its Logon.
                return;
            }

            await SendAsync(session, n => session.Writer.ResendRequest(n, session.GapFrom, 0), session.Cts.Token).ConfigureAwait(false);
            return;
        }

        if (GetTag(frame, "\u000143="u8) != "Y")
        {
            SetLastError($"MsgSeqNum {seq} abaixo do esperado {session.NextIn} sem PossDupFlag.");
        }
    }

    private static void ClearGapIfFilled(ActiveSession session)
    {
        if (session.GapFrom != 0 && session.NextIn > session.GapUntil)
        {
            session.GapFrom = 0;
            session.GapUntil = 0;
        }
    }

    private async Task HeartbeatLoopAsync(ActiveSession session)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, session.Profile.HeartBtInt));
        using var timer = new PeriodicTimer(HeartbeatTick, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(session.Cts.Token).ConfigureAwait(false))
            {
                Persist(session);
                if (State != ProbeState.Live || _time.GetElapsedTime(session.LastSendTimestamp) < interval)
                {
                    continue;
                }

                await SendAsync(session, n => session.Writer.Heartbeat(n), session.Cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Disconnect requested.
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
            if (!session.Cts.IsCancellationRequested)
            {
                await FaultAsync(session, ex.Message).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Takes the next MsgSeqNum, builds the frame and writes it, serialized with other senders.</summary>
    private async Task SendAsync(ActiveSession session, Func<int, byte[]> build, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var seq = session.OutSeq + 1;
            var frame = build(seq);
            await session.Stream!.WriteAsync(frame, ct).ConfigureAwait(false);
            session.OutSeq = seq;
            session.LastSendTimestamp = _time.GetTimestamp();
            session.Dirty = true;
            Persist(session);
            PublishSentSession(frame);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Answers a ResendRequest: GapFill with 34=BeginSeqNo and 36=next outgoing. OutSeq does not move.</summary>
    private async Task SendGapFillAsync(ActiveSession session, int beginSeqNo, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var next = session.OutSeq + 1;
            var frame = session.Writer.GapFill(Math.Min(beginSeqNo, next), next);
            await session.Stream!.WriteAsync(frame, ct).ConfigureAwait(false);
            session.LastSendTimestamp = _time.GetTimestamp();
            PublishSentSession(frame);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void Persist(ActiveSession session)
    {
        if (!session.Dirty)
        {
            return;
        }

        session.Dirty = false;
        _store.Set(session.Key, new FixSeqState(session.OutSeq, session.NextIn));
        _store.Flush(session.Key);
    }

    /// <summary>Moves to Faulted (unless the session is already stopping), records the error and closes the socket.</summary>
    private async Task FaultAsync(ActiveSession session, string error)
    {
        if (session.Cts.IsCancellationRequested || Interlocked.Exchange(ref session.Faulted, 1) == 1)
        {
            return;
        }

        Persist(session);
        _snapshot = session.Snapshot();
        SetState(ProbeState.Faulted, error);
        session.LogonReceived.TrySetResult();
        await session.Cts.CancelAsync().ConfigureAwait(false);
        session.Close();
    }

    private async Task StopAsync(ActiveSession session, bool sendLogout)
    {
        if (sendLogout && session.Stream is not null && !session.Cts.IsCancellationRequested)
        {
            try
            {
                using var logoutCts = new CancellationTokenSource(CloseTimeout);
                await SendAsync(session, n => session.Writer.Logout(n), logoutCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException or InvalidOperationException)
            {
                // The peer is already gone; closing below is all that is left.
            }
        }

        await session.Cts.CancelAsync().ConfigureAwait(false);
        session.Close();
        foreach (var loop in new[] { session.ReadLoop, session.HeartbeatLoop })
        {
            if (loop is null)
            {
                continue;
            }

            try
            {
                await loop.WaitAsync(CloseTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Sockets are closed; the loops end on their own.
            }
        }

        Persist(session);
    }

    private void ClearAfterStop(ActiveSession session)
    {
        if (ReferenceEquals(_active, session))
        {
            _active = null;
        }

        session.Dispose();
    }

    private static string? ResolveDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        return Path.IsPathRooted(directory) ? directory : Path.Combine(AppContext.BaseDirectory, directory);
    }

    /// <summary>Publishes Logon, ResendRequest, SequenceReset and Logout. Heartbeat stays off the table.</summary>
    private void PublishSentSession(byte[] frame)
    {
        if (GetMsgType(frame) is "A" or "2" or "4" or "5")
        {
            PublishSent(frame);
        }
    }

    private static string? GetMsgType(ReadOnlySpan<byte> frame) => GetTag(frame, "\u000135="u8);

    private static string? GetTag(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> marker)
    {
        var start = frame.IndexOf(marker);
        if (start < 0)
        {
            return null;
        }

        var value = frame[(start + marker.Length)..];
        var end = value.IndexOf((byte)0x01);
        return end < 0 ? null : Encoding.ASCII.GetString(value[..end]);
    }

    private static bool TryGetInt(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> marker, out int value)
    {
        value = 0;
        var text = GetTag(frame, marker);
        return text is not null && int.TryParse(text, out value);
    }

    private sealed class ActiveSession(TcpClient client, FixProfile profile, FixSessionWriter writer, string key) : IDisposable
    {
        public TcpClient Client { get; } = client;
        public FixProfile Profile { get; } = profile;
        public FixSessionWriter Writer { get; } = writer;
        public string Key { get; } = key;
        public CancellationTokenSource Cts { get; } = new();
        public TaskCompletionSource LogonReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public NetworkStream? Stream { get; set; }
        public Task? ReadLoop { get; set; }
        public Task? HeartbeatLoop { get; set; }
        public volatile int OutSeq;
        public volatile int NextIn = 1;
        public volatile int LastReceivedSeq;
        public volatile int GapFrom;
        public volatile int GapUntil;
        public volatile bool Dirty;
        public long LastSendTimestamp;
        public int Faulted;

        public FixSessionSnapshot Snapshot() =>
            new(Profile.SenderCompId, Profile.TargetCompId, OutSeq, LastReceivedSeq, NextIn, GapFrom);

        public void Close()
        {
            try
            {
                Client.Close();
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or IOException)
            {
                // Already closed.
            }
        }

        public void Dispose()
        {
            Close();
            Cts.Dispose();
        }
    }
}
