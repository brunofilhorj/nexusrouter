using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Text;
using DropCopySim.Core.Fix;
using DropCopySim.Core.Telemetry;
using Microsoft.Extensions.Logging;

namespace DropCopySim.Core.Acceptor;

/// <summary>
/// One accepted FIX 4.4 TCP connection: inbound framing, outbound frames, and session sequence numbers.
/// </summary>
/// <remarks>
/// The read loop uses <see cref="PipeReader"/> and <see cref="Fix44FrameScanner"/>.
/// Outbound writes go through a <see cref="PipeWriter"/> on the same socket.
/// Concurrent <see cref="SendOutbound"/> calls are serialized with a semaphore; this session is outside
/// the NexusRouter hot path. CheckSum and BodyLength are produced by the caller
/// (<c>.kiro/steering/b3-guidelines.md</c> §15).
/// </remarks>
public sealed class AcceptorSession : IAsyncDisposable
{
    private const byte Soh = 0x01;

    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly PipeReader _reader;
    private readonly PipeWriter _writer;
    private readonly ILogger<AcceptorSession> _logger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly FixSessionFsm _fsm = new();
    private readonly TaskCompletionSource _logonAccepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _outboundSeqNum;
    private long _nextExpectedInboundMsgSeqNum = 1;
    private long _lastOutboundTimestamp;
    private long _lastActivityUtcTicks;
    private string _peerCompId = string.Empty;
    private int _heartbeatIntervalSeconds;
    private int _logonMode;
    private int _started;
    private int _disposed;

    /// <summary>
    /// Binds an accepted socket to acceptor options and a logger.
    /// </summary>
    /// <param name="socket">Connected socket returned by the listener. Owned by this session.</param>
    /// <param name="options">Listen identity and session defaults.</param>
    /// <param name="logger">Structured logger for connection lifetime.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public AcceptorSession(Socket socket, AcceptorOptions options, ILogger<AcceptorSession> logger)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _socket = socket;
        Options = options;
        _logger = logger;
        _logonMode = (int)options.LogonMode;
        _stream = new NetworkStream(socket, ownsSocket: false);
        _reader = PipeReader.Create(_stream, new StreamPipeReaderOptions(leaveOpen: true));
        _writer = PipeWriter.Create(_stream, new StreamPipeWriterOptions(leaveOpen: true));
    }

    /// <summary>Acceptor options captured for this connection.</summary>
    public AcceptorOptions Options { get; }

    /// <summary>
    /// How the next client Logon is answered (FR-ACC-05, FR-FAULT-03).
    /// </summary>
    /// <remarks>
    /// Initialized from <see cref="AcceptorOptions.LogonMode"/> and switchable at runtime with
    /// <see cref="SetLogonMode(LogonMode)"/>. Read when a Logon arrives: <see cref="LogonMode.Success"/>
    /// answers with <c>ApplicationLogonRspCode (6930) = 0</c> and moves to
    /// <see cref="AcceptorSessionState.Active"/>; <see cref="LogonMode.InvalidCredentials"/> and
    /// <see cref="LogonMode.NotAuthorized"/> answer with <c>6930 = 1</c> or <c>6930 = 2</c> and close
    /// the session (<c>.kiro/steering/b3-guidelines.md</c> §13).
    /// </remarks>
    public LogonMode LogonMode => (LogonMode)Volatile.Read(ref _logonMode);

    /// <summary>Session lifecycle state machine.</summary>
    public FixSessionFsm Fsm => _fsm;

    /// <summary>
    /// Next outbound MsgSeqNum (tag 34). The first allocation returns 1.
    /// </summary>
    public long NextOutboundMsgSeqNum => Volatile.Read(ref _outboundSeqNum) + 1;

    /// <summary>Next inbound MsgSeqNum expected after the most recently received message or SequenceReset.</summary>
    public long NextExpectedInboundMsgSeqNum => Volatile.Read(ref _nextExpectedInboundMsgSeqNum);

    /// <summary>
    /// UTC time of the last inbound or outbound session activity, or <c>null</c> before any activity.
    /// </summary>
    /// <remarks>Exposed for the <c>GET /v1/state</c> snapshot so it carries the same "last heartbeat"
    /// readout the live <see cref="SessionStateChanged"/> feed does.</remarks>
    public DateTimeOffset? LastActivityUtc
    {
        get
        {
            long ticks = Volatile.Read(ref _lastActivityUtcTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>
    /// Raised after this session changes <see cref="Fsm"/> state, for control-plane telemetry.
    /// </summary>
    public event Action<SessionStateChanged>? StateChanged;

    /// <summary>
    /// Raised for each complete inbound FIX frame.
    /// </summary>
    /// <remarks>The sequence remains valid only until the handler returns.</remarks>
    public event Action<ReadOnlySequence<byte>>? FrameReceived;

    /// <summary>
    /// Reads inbound frames until the peer closes the socket or <paramref name="cancellationToken"/> is canceled.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read loop and ends the session.</param>
    /// <returns>A task that completes when the connection has been moved to <see cref="AcceptorSessionState.Disconnected"/>.</returns>
    /// <exception cref="InvalidOperationException">The session was already started.</exception>
    /// <exception cref="InvalidSessionStateException">The FSM was not <see cref="AcceptorSessionState.Disconnected"/>.</exception>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("The acceptor session is already running.");
        }

        _fsm.TransitionToConnected();
        PublishState();
        _logger.LogInformation(
            "Acceptor session connected. SenderCompId {SenderCompId} TargetCompId {TargetCompId} Remote {RemoteEndPoint}",
            Options.SenderCompId,
            Options.TargetCompId,
            DescribeRemote());

        using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task heartbeatTask = RunHeartbeatLoopAsync(_logonAccepted.Task, heartbeatCancellation.Token);
        Exception? failure = null;
        try
        {
            await ReadFramesAsync(heartbeatCancellation.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransportClosed(ex))
        {
            _logger.LogDebug(ex, "Acceptor session transport closed. SenderCompId {SenderCompId}", Options.SenderCompId);
        }
        catch (Exception ex)
        {
            failure = ex;
            _logger.LogError(ex, "Acceptor session failed. SenderCompId {SenderCompId}", Options.SenderCompId);
            throw;
        }
        finally
        {
            heartbeatCancellation.Cancel();
            try
            {
                await heartbeatTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (heartbeatCancellation.IsCancellationRequested)
            {
            }

            await _reader.CompleteAsync(failure).ConfigureAwait(false);
            Disconnect();
            _logger.LogInformation(
                "Acceptor session disconnected. SenderCompId {SenderCompId}",
                Options.SenderCompId);
        }
    }

    /// <summary>
    /// Assigns the next monotonic outbound MsgSeqNum.
    /// </summary>
    /// <returns>The sequence number to write in tag 34.</returns>
    /// <remarks>Uses <see cref="Interlocked.Increment(ref long)"/>. Safe to call from the outbound path.</remarks>
    public long AllocateOutboundMsgSeqNum() => Interlocked.Increment(ref _outboundSeqNum);

    /// <summary>
    /// Resets outbound MsgSeqNum so the next <see cref="AllocateOutboundMsgSeqNum"/> returns 1.
    /// </summary>
    /// <remarks>Called when the client Logon carries <c>ResetSeqNumFlag=Y</c> (FR-ACC-07).</remarks>
    public void ResetOutboundMsgSeqNum() => Interlocked.Exchange(ref _outboundSeqNum, 0);

    /// <summary>
    /// Switches how the next client Logon is answered (FR-FAULT-03).
    /// </summary>
    /// <param name="mode">The Logon response mode applied to Logons received after this call.</param>
    /// <remarks>
    /// Consumed by the control endpoint (T-22). A Logon already accepted on this connection keeps its
    /// state; the new mode takes effect on the next Logon (typically after a reconnect). Safe to call
    /// from the control plane concurrently with the read loop.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is not a defined value.</exception>
    public void SetLogonMode(LogonMode mode)
    {
        if (mode is not (LogonMode.Success or LogonMode.InvalidCredentials or LogonMode.NotAuthorized))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown Logon mode.");
        }

        LogonMode previous = (LogonMode)Interlocked.Exchange(ref _logonMode, (int)mode);
        if (previous != mode)
        {
            _logger.LogInformation(
                "Logon response mode changed from {PreviousMode} to {LogonMode}. SenderCompId {SenderCompId}",
                previous,
                mode,
                Options.SenderCompId);
        }
    }

    /// <summary>
    /// Writes one complete FIX frame to the client.
    /// </summary>
    /// <param name="frame">Frame bytes, including the CheckSum trailer.</param>
    /// <param name="cancellationToken">Cancels while waiting for the outbound lock or the socket write.</param>
    /// <returns>A task that completes when the frame has been flushed to the socket.</returns>
    /// <exception cref="ArgumentException"><paramref name="frame"/> is empty.</exception>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    /// <remarks>
    /// The span is copied before the first await. Callers are serialized with a semaphore so the
    /// <see cref="PipeWriter"/> is never used concurrently.
    /// </remarks>
    public ValueTask SendOutbound(ReadOnlySpan<byte> frame, CancellationToken cancellationToken = default)
    {
        if (frame.IsEmpty)
        {
            throw new ArgumentException("Frame is empty.", nameof(frame));
        }

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return WriteFrameAsync(frame.ToArray(), cancellationToken);
    }

    /// <summary>
    /// Drops the TCP connection abruptly, without sending a FIX Logout (FR-FAULT-01).
    /// </summary>
    /// <remarks>
    /// Simulates an ungraceful disconnect so the NexusRouter exercises its reconnect-with-backoff
    /// path (<c>.kiro/steering/b3-guidelines.md</c> §13, "Desconexão TCP (sem Logout)"). The read
    /// loop observes the closed transport and moves the session to
    /// <see cref="AcceptorSessionState.Disconnected"/>; <see cref="FixAcceptorService"/> then accepts
    /// a fresh connection (FR-FAULT-06). Safe to call more than once; later calls are ignored.
    /// </remarks>
    public void DropConnection()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _logger.LogWarning(
            "Fault injection: dropping connection without Logout. SenderCompId {SenderCompId}",
            Options.SenderCompId);

        try
        {
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "Fault injection: socket shutdown skipped on drop.");
        }

        try
        {
            _socket.Close();
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "Fault injection: socket close skipped on drop.");
        }
    }

    /// <summary>
    /// Sends a FIX Logout (MsgType=5) carrying <paramref name="text"/> in Text (tag 58) (FR-FAULT-02).
    /// </summary>
    /// <param name="text">
    /// Human-readable Logout reason, for example <c>"MsgSeqNum too low"</c> or
    /// <c>"Password will expire"</c> (<c>.kiro/steering/b3-guidelines.md</c> §13).
    /// </param>
    /// <param name="cancellationToken">Cancels while waiting for the outbound lock or socket write.</param>
    /// <returns>A task that completes once the Logout has been flushed to the client.</returns>
    /// <exception cref="ArgumentException"><paramref name="text"/> is null or empty.</exception>
    /// <exception cref="InvalidSessionStateException">The session is not <see cref="AcceptorSessionState.Active"/>.</exception>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    public async ValueTask ForceLogoutAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (_fsm.State != AcceptorSessionState.Active)
        {
            throw new InvalidSessionStateException(_fsm.State, AcceptorSessionState.LogoutSent, AcceptorSessionState.Active);
        }

        _logger.LogWarning(
            "Fault injection: forcing Logout. SenderCompId {SenderCompId} Text {LogoutText}",
            Options.SenderCompId,
            text);

        await SendAdministrativeMessageAsync(
            "5",
            _peerCompId,
            null,
            null,
            text,
            cancellationToken).ConfigureAwait(false);

        _fsm.TransitionToLogoutSent();
        PublishState();
    }

    /// <summary>
    /// Injects a gap in the outbound session sequence by skipping <paramref name="skip"/> numbers (FR-FAULT-04).
    /// </summary>
    /// <param name="skip">Number of outbound MsgSeqNum values to skip. Must be positive.</param>
    /// <remarks>
    /// The next outbound frame is numbered past the skipped range, so the client detects a gap and
    /// issues a <c>ResendRequest (2)</c>. The inbound handler answers that request with a
    /// <c>SequenceReset (4)</c> <c>GapFillFlag=Y</c> covering the hole
    /// (<c>.kiro/steering/b3-guidelines.md</c> §12.1). This does not send a frame on its own.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="skip"/> is not positive.</exception>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    public void InjectSequenceGap(int skip)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(skip);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        long gapStart = Interlocked.Add(ref _outboundSeqNum, skip) + 1;
        _logger.LogWarning(
            "Fault injection: skipping {Skip} outbound sequence numbers. Next outbound MsgSeqNum {NextSeqNum} SenderCompId {SenderCompId}",
            skip,
            gapStart,
            Options.SenderCompId);
    }

    /// <summary>
    /// Sends one deliberately malformed frame of the requested <paramref name="kind"/> (FR-FAULT-05, optional).
    /// </summary>
    /// <param name="kind">The way the frame should deviate from a valid FIX 4.4 message.</param>
    /// <param name="cancellationToken">Cancels while waiting for the outbound lock or socket write.</param>
    /// <returns>A task that completes once the malformed frame has been flushed to the client.</returns>
    /// <remarks>
    /// Consumes one outbound MsgSeqNum. The frame is otherwise a Heartbeat (MsgType=0) so the only
    /// defect is the injected one (bad CheckSum or a missing header field).
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    public async ValueTask InjectMalformedAsync(
        MalformedInjectionKind kind,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        _logger.LogWarning(
            "Fault injection: sending malformed frame {Kind}. SenderCompId {SenderCompId}",
            kind,
            Options.SenderCompId);

        byte[] frame = BuildMalformedFrame(kind);
        await SendOutbound(frame, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Closes the socket and releases the outbound writer.</summary>
    /// <returns>A task that completes when owned resources are released.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await _writer.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Outbound writer completion failed.");
        }

        try
        {
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "Socket shutdown skipped.");
        }

        _stream.Dispose();
        _socket.Dispose();
        _sendLock.Dispose();
    }

    private async Task ReadFramesAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ReadResult result = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;
            try
            {
                bool continueReading = true;
                while (continueReading && Fix44FrameScanner.TryDecodeAny(ref buffer, out ReadOnlySequence<byte> frame))
                {
                    NoteInboundActivity();
                    _logger.LogTrace("Inbound FIX frame of {FrameLength} bytes", frame.Length);
                    FrameReceived?.Invoke(frame);
                    continueReading = await HandleInboundFrameAsync(frame, cancellationToken).ConfigureAwait(false);
                }

                if (!continueReading)
                {
                    break;
                }
            }
            finally
            {
                _reader.AdvanceTo(buffer.Start, buffer.End);
            }

            if (result.IsCompleted || result.IsCanceled)
            {
                break;
            }
        }
    }

    private async ValueTask<bool> HandleInboundFrameAsync(
        ReadOnlySequence<byte> frame,
        CancellationToken cancellationToken)
    {
        byte[] bytes = frame.ToArray();
        if (TryGetField(bytes, FixTags.MsgSeqNum, out string inboundSequenceValue)
            && long.TryParse(inboundSequenceValue, NumberStyles.None, CultureInfo.InvariantCulture, out long inboundSequence)
            && inboundSequence > 0
            && inboundSequence < long.MaxValue)
        {
            Volatile.Write(ref _nextExpectedInboundMsgSeqNum, inboundSequence + 1);
        }

        bool hasMsgType = TryGetField(bytes, FixTags.MsgType, out string msgType);

        if (_fsm.State == AcceptorSessionState.Connected)
        {
            string peerCompId = TryGetField(bytes, FixTags.SenderCompId, out string senderCompId)
                ? senderCompId
                : Options.TargetCompId;

            if (!TryGetField(bytes, FixTags.BeginString, out string beginString)
                || !string.Equals(beginString, Options.BeginString, StringComparison.Ordinal))
            {
                await SendRejectAsync(peerCompId, "BeginString must be FIX.4.4.", cancellationToken).ConfigureAwait(false);
                return false;
            }

            if (!hasMsgType || !string.Equals(msgType, "A", StringComparison.Ordinal))
            {
                await SendRejectAsync(peerCompId, "The first FIX message must be Logon (MsgType=A).", cancellationToken)
                    .ConfigureAwait(false);
                return false;
            }

            if (!TryGetField(bytes, FixTags.TargetCompId, out string targetCompId)
                || !string.Equals(targetCompId, Options.SenderCompId, StringComparison.Ordinal))
            {
                await SendRejectAsync(peerCompId, "TargetCompID does not match the acceptor SenderCompID.", cancellationToken)
                    .ConfigureAwait(false);
                return false;
            }

            if (!TryGetField(bytes, FixTags.SenderCompId, out senderCompId)
                || !string.Equals(senderCompId, Options.TargetCompId, StringComparison.Ordinal))
            {
                await SendRejectAsync(peerCompId, "SenderCompID does not match the configured client.", cancellationToken)
                    .ConfigureAwait(false);
                return false;
            }

            if (!TryGetField(bytes, FixTags.HeartBtInt, out string heartbeatValue)
                || !int.TryParse(heartbeatValue, NumberStyles.None, CultureInfo.InvariantCulture, out int heartbeatInterval)
                || heartbeatInterval <= 0)
            {
                await SendRejectAsync(peerCompId, "Logon HeartBtInt (108) must be a positive integer.", cancellationToken)
                    .ConfigureAwait(false);
                return false;
            }

            if (TryGetField(bytes, FixTags.ResetSeqNumFlag, out string resetSeqNumFlag)
                && string.Equals(resetSeqNumFlag, "Y", StringComparison.Ordinal))
            {
                ResetOutboundMsgSeqNum();
            }

            _peerCompId = peerCompId;
            _heartbeatIntervalSeconds = heartbeatInterval;
            _fsm.TransitionToLogonReceived();
            PublishState();

            LogonMode logonMode = LogonMode;
            int logonRspCode = (int)logonMode;

            await SendAdministrativeMessageAsync(
                "A",
                peerCompId,
                heartbeatInterval,
                null,
                null,
                cancellationToken,
                logonRspCode).ConfigureAwait(false);

            if (logonMode != LogonMode.Success)
            {
                _logger.LogWarning(
                    "Fault injection: rejecting Logon with ApplicationLogonRspCode {LogonRspCode} ({LogonMode}). SenderCompId {SenderCompId}",
                    logonRspCode,
                    logonMode,
                    Options.SenderCompId);

                // Logon failed deterministically (FR-FAULT-03). End the read loop; RunAsync's finally
                // moves the session LogonReceived -> Disconnected, closing the connection without Active.
                return false;
            }

            _fsm.TransitionToActive();
            PublishState();
            _logonAccepted.TrySetResult();
            return true;
        }

        if (_fsm.State != AcceptorSessionState.Active || !hasMsgType)
        {
            return false;
        }

        switch (msgType)
        {
            case "0":
                return true;
            case "1":
                if (!TryGetField(bytes, FixTags.TestReqId, out string testReqId) || testReqId.Length == 0)
                {
                    await SendRejectAsync(_peerCompId, "TestRequest is missing TestReqID (112).", cancellationToken)
                        .ConfigureAwait(false);
                    return false;
                }

                await SendAdministrativeMessageAsync(
                    "0",
                    _peerCompId,
                    null,
                    testReqId,
                    null,
                    cancellationToken).ConfigureAwait(false);
                return true;
            case "2":
                if (!TryGetSequenceRange(bytes, out long beginSeqNo, out long endSeqNo))
                {
                    await SendRejectAsync(_peerCompId, "ResendRequest requires a valid BeginSeqNo and EndSeqNo.", cancellationToken)
                        .ConfigureAwait(false);
                    return false;
                }

                long nextOutboundSeqNo = NextOutboundMsgSeqNum;
                long newSeqNo = endSeqNo == 0
                    ? Math.Max(nextOutboundSeqNo, beginSeqNo + 1)
                    : endSeqNo + 1;
                await SendSequenceResetAsync(beginSeqNo, newSeqNo, cancellationToken).ConfigureAwait(false);
                AdvanceOutboundMsgSeqNum(newSeqNo);
                return true;
            case "4":
                if (!TryGetField(bytes, FixTags.NewSeqNo, out string newSeqNoValue)
                    || !long.TryParse(newSeqNoValue, NumberStyles.None, CultureInfo.InvariantCulture, out long inboundNewSeqNo)
                    || inboundNewSeqNo <= 0
                    || !IsValidGapFillFlag(bytes))
                {
                    await SendRejectAsync(_peerCompId, "SequenceReset requires a valid NewSeqNo and GapFillFlag.", cancellationToken)
                        .ConfigureAwait(false);
                    return false;
                }

                Volatile.Write(ref _nextExpectedInboundMsgSeqNum, inboundNewSeqNo);
                return true;
            case "5":
                await SendAdministrativeMessageAsync(
                    "5",
                    _peerCompId,
                    null,
                    null,
                    null,
                    cancellationToken).ConfigureAwait(false);
                _fsm.TransitionToLogoutSent();
                PublishState();
                return false;
            default:
                return true;
        }
    }

    private byte[] BuildMalformedFrame(MalformedInjectionKind kind)
    {
        int capacity = checked(128 + Options.SenderCompId.Length + _peerCompId.Length);
        byte[] buffer = new byte[capacity];
        var messageWriter = new B3FixMessageWriter(buffer);
        long sequenceNumber = AllocateOutboundMsgSeqNum();
        messageWriter.WriteB3Header(
            "0",
            Options.SenderCompId,
            _peerCompId.Length == 0 ? Options.TargetCompId : _peerCompId,
            sequenceNumber,
            DateTime.UtcNow,
            sequenceNumber);
        int length = messageWriter.Finish();

        return kind switch
        {
            MalformedInjectionKind.InvalidCheckSum => CorruptCheckSum(buffer, length),
            MalformedInjectionKind.MissingField => RemoveField(buffer, length, FixTags.SendingTime),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown malformed injection kind."),
        };
    }

    private static byte[] CorruptCheckSum(byte[] frame, int length)
    {
        byte[] corrupted = frame[..length];

        // CheckSum (10=XXX<SOH>) is the final seven bytes: "10=" + three digits + SOH.
        // Flip each digit to a different one so the trailer no longer matches the body.
        for (int i = corrupted.Length - 4; i < corrupted.Length - 1; i++)
        {
            corrupted[i] = corrupted[i] == (byte)'0' ? (byte)'1' : (byte)'0';
        }

        return corrupted;
    }

    private static byte[] RemoveField(byte[] frame, int length, int tag)
    {
        ReadOnlySpan<byte> source = frame.AsSpan(0, length);
        Span<byte> prefix = stackalloc byte[12];
        int prefixLength = 0;
        prefix[prefixLength++] = Soh;
        int digitsAt = prefixLength;
        if (!System.Buffers.Text.Utf8Formatter.TryFormat(tag, prefix[digitsAt..], out int digits))
        {
            throw new InvalidOperationException("Tag did not fit in the lookup prefix.");
        }

        prefixLength += digits;
        prefix[prefixLength++] = (byte)'=';
        ReadOnlySpan<byte> needle = prefix[..prefixLength];

        int fieldStart = source.IndexOf(needle);
        if (fieldStart < 0)
        {
            // Field not present; nothing to remove, return as-is.
            return source.ToArray();
        }

        int valueStart = fieldStart + needle.Length;
        int sohOffset = source[valueStart..].IndexOf(Soh);
        if (sohOffset < 0)
        {
            return source.ToArray();
        }

        int fieldEnd = valueStart + sohOffset + 1;
        byte[] result = new byte[length - (fieldEnd - fieldStart)];
        source[..fieldStart].CopyTo(result);
        source[fieldEnd..].CopyTo(result.AsSpan(fieldStart));
        return result;
    }

    private ValueTask SendRejectAsync(string targetCompId, string text, CancellationToken cancellationToken) =>
        SendAdministrativeMessageAsync("3", targetCompId, null, null, text, cancellationToken);

    private async ValueTask SendSequenceResetAsync(
        long beginSeqNo,
        long newSeqNo,
        CancellationToken cancellationToken)
    {
        int capacity = checked(128 + Options.SenderCompId.Length + _peerCompId.Length);
        byte[] buffer = new byte[capacity];
        var messageWriter = new B3FixMessageWriter(buffer);
        messageWriter.WriteB3Header(
            "4",
            Options.SenderCompId,
            _peerCompId,
            beginSeqNo,
            DateTime.UtcNow,
            beginSeqNo);
        messageWriter.WriteField(FixTags.GapFillFlag, "Y");
        messageWriter.WriteField(FixTags.NewSeqNo, newSeqNo);

        int length = messageWriter.Finish();
        await SendOutbound(buffer.AsSpan(0, length), cancellationToken).ConfigureAwait(false);
    }

    private static bool TryGetSequenceRange(ReadOnlySpan<byte> frame, out long beginSeqNo, out long endSeqNo)
    {
        beginSeqNo = 0;
        endSeqNo = 0;
        if (!TryGetField(frame, FixTags.BeginSeqNo, out string beginValue)
            || !long.TryParse(beginValue, NumberStyles.None, CultureInfo.InvariantCulture, out beginSeqNo)
            || beginSeqNo <= 0
            || beginSeqNo == long.MaxValue
            || !TryGetField(frame, FixTags.EndSeqNo, out string endValue)
            || !long.TryParse(endValue, NumberStyles.None, CultureInfo.InvariantCulture, out endSeqNo)
            || endSeqNo < 0
            || (endSeqNo != 0 && (endSeqNo < beginSeqNo || endSeqNo == long.MaxValue)))
        {
            return false;
        }

        return true;
    }

    private static bool IsValidGapFillFlag(ReadOnlySpan<byte> frame) =>
        !TryGetField(frame, FixTags.GapFillFlag, out string gapFillFlag)
        || string.Equals(gapFillFlag, "Y", StringComparison.Ordinal)
        || string.Equals(gapFillFlag, "N", StringComparison.Ordinal);

    private void AdvanceOutboundMsgSeqNum(long nextSequenceNumber)
    {
        long target = nextSequenceNumber - 1;
        long current;
        do
        {
            current = Volatile.Read(ref _outboundSeqNum);
            if (current >= target)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _outboundSeqNum, target, current) != current);
    }

    private async ValueTask SendAdministrativeMessageAsync(
        string msgType,
        string targetCompId,
        int? heartbeatInterval,
        string? testReqId,
        string? text,
        CancellationToken cancellationToken,
        int logonRspCode = 0)
    {
        int capacity = checked(
            128
            + Options.SenderCompId.Length
            + targetCompId.Length
            + (testReqId?.Length ?? 0)
            + (text?.Length ?? 0));
        byte[] buffer = new byte[capacity];
        var messageWriter = new B3FixMessageWriter(buffer);
        long sequenceNumber = AllocateOutboundMsgSeqNum();
        messageWriter.WriteB3Header(
            msgType,
            Options.SenderCompId,
            targetCompId,
            sequenceNumber,
            DateTime.UtcNow,
            sequenceNumber);

        if (heartbeatInterval is int interval)
        {
            messageWriter.WriteField(FixTags.EncryptMethod, 0);
            messageWriter.WriteField(FixTags.HeartBtInt, interval);
            messageWriter.WriteField(FixTags.ApplicationLogonRspCode, logonRspCode);
        }

        if (testReqId is not null)
        {
            messageWriter.WriteField(FixTags.TestReqId, testReqId);
        }

        if (text is not null)
        {
            messageWriter.WriteField(FixTags.Text, text);
        }

        int length = messageWriter.Finish();
        await SendOutbound(buffer.AsSpan(0, length), cancellationToken).ConfigureAwait(false);
    }

    private async Task RunHeartbeatLoopAsync(Task logonAccepted, CancellationToken cancellationToken)
    {
        try
        {
            await logonAccepted.WaitAsync(cancellationToken).ConfigureAwait(false);
            TimeSpan interval = TimeSpan.FromSeconds(_heartbeatIntervalSeconds);
            using var timer = new PeriodicTimer(interval);

            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                long lastSent = Volatile.Read(ref _lastOutboundTimestamp);
                if (lastSent == 0 || Stopwatch.GetElapsedTime(lastSent) >= interval)
                {
                    await SendAdministrativeMessageAsync(
                        "0",
                        _peerCompId,
                        null,
                        null,
                        null,
                        cancellationToken).ConfigureAwait(false);
                }

                // Re-publish the session view each heartbeat tick while Active so the SimPanel's
                // "last heartbeat" and MsgSeqNum stay live: PublishState otherwise only fires on FSM
                // transitions, leaving the panel frozen once the session reaches Active.
                if (_fsm.State == AcceptorSessionState.Active)
                {
                    PublishState();
                }
            }
        }
        catch (Exception ex) when (IsTransportClosed(ex))
        {
            _logger.LogDebug(ex, "Acceptor heartbeat loop stopped. SenderCompId {SenderCompId}", Options.SenderCompId);
        }
    }

    private static bool TryGetField(ReadOnlySpan<byte> frame, int tag, out string value)
    {
        value = string.Empty;
        int fieldStart = 0;
        while (fieldStart < frame.Length)
        {
            int separatorOffset = frame[fieldStart..].IndexOf((byte)0x01);
            if (separatorOffset < 0)
            {
                return false;
            }

            ReadOnlySpan<byte> field = frame.Slice(fieldStart, separatorOffset);
            int equalsOffset = field.IndexOf((byte)'=');
            if (equalsOffset > 0
                && int.TryParse(field[..equalsOffset], NumberStyles.None, CultureInfo.InvariantCulture, out int fieldTag)
                && fieldTag == tag)
            {
                value = Encoding.ASCII.GetString(field[(equalsOffset + 1)..]);
                return true;
            }

            fieldStart += separatorOffset + 1;
        }

        return false;
    }

    private async ValueTask WriteFrameAsync(byte[] frame, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            FlushResult result = await _writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            if (result.IsCanceled)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            Volatile.Write(ref _lastOutboundTimestamp, Stopwatch.GetTimestamp());
            // Outbound frames (heartbeats included) count as session activity for the SimPanel's
            // "last heartbeat" readout, matching SessionStateChanged.LastHeartbeatUtc's documented
            // "last inbound or outbound heartbeat activity" meaning.
            Volatile.Write(ref _lastActivityUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
        }
        finally
        {
            try
            {
                _sendLock.Release();
            }
            catch (ObjectDisposedException)
            {
                // DisposeAsync won the race during shutdown.
            }
        }
    }

    private void NoteInboundActivity()
    {
        _fsm.RecordInboundMessage();
        Volatile.Write(ref _lastActivityUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    private void Disconnect()
    {
        if (_fsm.State == AcceptorSessionState.Disconnected)
        {
            return;
        }

        _fsm.TransitionToDisconnected();
        PublishState();
    }

    private void PublishState()
    {
        long activityTicks = Volatile.Read(ref _lastActivityUtcTicks);
        StateChanged?.Invoke(new SessionStateChanged
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            State = _fsm.State.ToString(),
            SenderCompId = Options.SenderCompId,
            TargetCompId = Options.TargetCompId,
            MsgSeqNum = NextOutboundMsgSeqNum,
            LastHeartbeatUtc = activityTicks == 0
                ? null
                : new DateTimeOffset(activityTicks, TimeSpan.Zero),
        });
    }

    private string DescribeRemote()
    {
        try
        {
            return _socket.RemoteEndPoint?.ToString() ?? "unknown";
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "Remote endpoint is unavailable.");
            return "unknown";
        }
    }

    private static bool IsTransportClosed(Exception exception) =>
        exception is OperationCanceledException or IOException or SocketException or ObjectDisposedException;
}
