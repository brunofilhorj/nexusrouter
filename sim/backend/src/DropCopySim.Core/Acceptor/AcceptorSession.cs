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
    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly PipeReader _reader;
    private readonly PipeWriter _writer;
    private readonly ILogger<AcceptorSession> _logger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly FixSessionFsm _fsm = new();
    private readonly TaskCompletionSource _logonAccepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _outboundSeqNum;
    private long _lastOutboundTimestamp;
    private long _lastActivityUtcTicks;
    private string _peerCompId = string.Empty;
    private int _heartbeatIntervalSeconds;
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
        _stream = new NetworkStream(socket, ownsSocket: false);
        _reader = PipeReader.Create(_stream, new StreamPipeReaderOptions(leaveOpen: true));
        _writer = PipeWriter.Create(_stream, new StreamPipeWriterOptions(leaveOpen: true));
    }

    /// <summary>Acceptor options captured for this connection.</summary>
    public AcceptorOptions Options { get; }

    /// <summary>Session lifecycle state machine.</summary>
    public FixSessionFsm Fsm => _fsm;

    /// <summary>
    /// Next outbound MsgSeqNum (tag 34). The first allocation returns 1.
    /// </summary>
    public long NextOutboundMsgSeqNum => Volatile.Read(ref _outboundSeqNum) + 1;

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

            await SendAdministrativeMessageAsync(
                "A",
                peerCompId,
                heartbeatInterval,
                null,
                null,
                cancellationToken).ConfigureAwait(false);

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

    private ValueTask SendRejectAsync(string targetCompId, string text, CancellationToken cancellationToken) =>
        SendAdministrativeMessageAsync("3", targetCompId, null, null, text, cancellationToken);

    private async ValueTask SendAdministrativeMessageAsync(
        string msgType,
        string targetCompId,
        int? heartbeatInterval,
        string? testReqId,
        string? text,
        CancellationToken cancellationToken)
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
            messageWriter.WriteField(FixTags.ApplicationLogonRspCode, 0);
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
