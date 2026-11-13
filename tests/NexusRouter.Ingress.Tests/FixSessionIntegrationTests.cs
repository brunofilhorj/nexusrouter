using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using NexusRouter.Domain.Abstractions;
using NexusRouter.Domain.Enums;
using NexusRouter.Domain.Models;
using NexusRouter.Domain.Secrets;
using NexusRouter.Ingress.Decoders;
using NexusRouter.Ingress.Sessions;

namespace NexusRouter.Ingress.Tests;

public sealed class FixSessionIntegrationTests
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FixSession_WhenPeerCloses_TransitionsToDisconnectedThenReconnects()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Channel<AuditRecord> auditChannel = CreateAuditChannel();
        var reconnectPolicy = new SequencedReconnectPolicy();
        var session = CreateSession(port, auditChannel.Writer, reconnectPolicy);
        TcpClient? peer = null;

        try
        {
            await session.StartAsync(CancellationToken.None);
            peer = await listener.AcceptTcpClientAsync().WaitAsync(StepTimeout);
            NetworkStream stream = peer.GetStream();
            byte[] logon = await ReadFixFrameAsync(stream);

            Assert.Equal("A", GetFieldValue(logon, 35));
            Assert.Equal("CLIENT", GetFieldValue(logon, 49));
            Assert.Equal("EXCHANGE", GetFieldValue(logon, 56));

            await SendFixFrameAsync(stream, CreateLogonAcknowledgement());
            await WaitForLifecycleStateAsync(auditChannel.Reader, SessionState.Active);

            peer.Dispose();
            peer = null;
            listener.Stop();

            await WaitForLifecycleStateAsync(auditChannel.Reader, SessionState.Disconnected);
            await WaitForLifecycleStateAsync(auditChannel.Reader, SessionState.Connecting);
        }
        finally
        {
            listener.Stop();
            peer?.Dispose();
            await session.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FixSession_WhenPeerStopsResponding_SendsHeartbeatsAndTestRequestThenDisconnects()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Channel<AuditRecord> auditChannel = CreateAuditChannel();
        var session = CreateSession(
            port,
            auditChannel.Writer,
            new FixedReconnectPolicy(TimeSpan.FromHours(1)));
        TcpClient? peer = null;

        try
        {
            await session.StartAsync(CancellationToken.None);
            peer = await listener.AcceptTcpClientAsync().WaitAsync(StepTimeout);
            NetworkStream stream = peer.GetStream();
            byte[] logon = await ReadFixFrameAsync(stream);
            Assert.Equal("A", GetFieldValue(logon, 35));

            await SendFixFrameAsync(stream, CreateLogonAcknowledgement());
            await WaitForLifecycleStateAsync(auditChannel.Reader, SessionState.Active);

            var heartbeatTimestamps = new List<long>(capacity: 2);
            while (heartbeatTimestamps.Count < 2)
            {
                byte[] outbound = await ReadFixFrameAsync(stream);
                string messageType = GetFieldValue(outbound, 35);
                if (messageType == "0")
                {
                    heartbeatTimestamps.Add(System.Diagnostics.Stopwatch.GetTimestamp());
                }
                else if (messageType == "1")
                {
                    string testRequestId = GetFieldValue(outbound, 112);
                    await SendFixFrameAsync(stream, CreateHeartbeatAcknowledgement(testRequestId, 2));
                }
            }

            TimeSpan heartbeatInterval = System.Diagnostics.Stopwatch.GetElapsedTime(
                heartbeatTimestamps[0],
                heartbeatTimestamps[1]);
            Assert.InRange(heartbeatInterval.TotalMilliseconds, 500, 3_000);

            string nextMessageType;
            do
            {
                byte[] outbound = await ReadFixFrameAsync(stream);
                nextMessageType = GetFieldValue(outbound, 35);
            }
            while (nextMessageType != "1");

            await WaitForLifecycleStateAsync(auditChannel.Reader, SessionState.Disconnected);
        }
        finally
        {
            listener.Stop();
            peer?.Dispose();
            await session.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData("7=1", "16=0")]
    [InlineData("7=1", "16=1")]
    [Trait("Category", "Integration")]
    public async Task FixSession_WhenPeerSendsResendRequest_RepliesWithWellFormedGapFill(string beginField, string endField)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Channel<AuditRecord> auditChannel = CreateAuditChannel();
        var session = CreateSession(
            port,
            auditChannel.Writer,
            new FixedReconnectPolicy(TimeSpan.FromHours(1)),
            heartBtInt: 30);
        TcpClient? peer = null;

        try
        {
            await session.StartAsync(CancellationToken.None);
            peer = await listener.AcceptTcpClientAsync().WaitAsync(StepTimeout);
            NetworkStream stream = peer.GetStream();
            byte[] logon = await ReadFixFrameAsync(stream);
            Assert.Equal("A", GetFieldValue(logon, 35));

            await SendFixFrameAsync(stream, CreateLogonAcknowledgement(30));
            await WaitForLifecycleStateAsync(auditChannel.Reader, SessionState.Active);

            await SendFixFrameAsync(
                stream,
                CreateFixFrame("35=2", "49=EXCHANGE", "56=CLIENT", "34=2", "52=20260101-12:00:02", beginField, endField));

            byte[] reply = await ReadFixFrameAsync(stream);

            Assert.Equal("4", GetFieldValue(reply, 35));
            Assert.Equal("1", GetFieldValue(reply, 34));
            Assert.Equal("Y", GetFieldValue(reply, 123));
            Assert.Equal("Y", GetFieldValue(reply, 43));
            Assert.Equal("2", GetFieldValue(reply, 36));
            GetFieldValue(reply, 122);
            FixMessageBuilderTests.AssertValidBodyLengthAndChecksum(reply);
        }
        finally
        {
            listener.Stop();
            peer?.Dispose();
            await session.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FixSession_WhenPeerSendsFrameWithBadSendingTime_RepliesRejectAndSkipsRouter()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Channel<AuditRecord> auditChannel = CreateAuditChannel();
        var router = new CountingRouter();
        var session = CreateSession(
            port,
            auditChannel.Writer,
            new FixedReconnectPolicy(TimeSpan.FromHours(1)),
            heartBtInt: 30,
            router: router,
            sendingTimeTolerance: TimeSpan.FromSeconds(120));
        TcpClient? peer = null;
        try
        {
            await session.StartAsync(CancellationToken.None);
            peer = await listener.AcceptTcpClientAsync().WaitAsync(StepTimeout);
            NetworkStream stream = peer.GetStream();
            await ReadFixFrameAsync(stream);
            string now = DateTime.UtcNow.ToString("yyyyMMdd-HH:mm:ss.fff");
            await SendFixFrameAsync(
                stream,
                CreateFixFrame("35=A", "49=EXCHANGE", "56=CLIENT", "34=1", $"52={now}", "98=0", "108=30"));
            await WaitForLifecycleStateAsync(auditChannel.Reader, SessionState.Active);

            await SendFixFrameAsync(
                stream,
                CreateFixFrame("35=D", "49=EXCHANGE", "56=CLIENT", "34=2", "52=20200101-12:00:00.000"));

            byte[] reply = await ReadFixFrameAsync(stream);
            Assert.Equal("3", GetFieldValue(reply, 35));
            Assert.Equal("2", GetFieldValue(reply, 45));
            Assert.Equal("52", GetFieldValue(reply, 371));
            Assert.Equal("10", GetFieldValue(reply, 373));
            FixMessageBuilderTests.AssertValidBodyLengthAndChecksum(reply);

            await SendFixFrameAsync(
                stream,
                CreateFixFrame("35=D", "49=EXCHANGE", "56=CLIENT", "34=3", $"52={DateTime.UtcNow:yyyyMMdd-HH:mm:ss.fff}"));
            await router.WaitForCountAsync(1);
            Assert.Equal(1, router.Count);
        }
        finally
        {
            listener.Stop();
            peer?.Dispose();
            await session.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData("123=Y")]
    [InlineData("123=N")]
    [Trait("Category", "Integration")]
    public async Task FixSession_WhenPeerSendsSequenceResetAhead_AdvancesExpectedInbound(string gapFill)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Channel<AuditRecord> auditChannel = CreateAuditChannel();
        var router = new CountingRouter();
        var log = new NullSessionLog();
        var session = CreateSession(
            port,
            auditChannel.Writer,
            new FixedReconnectPolicy(TimeSpan.FromHours(1)),
            heartBtInt: 30,
            router: router,
            sessionLog: log);
        TcpClient? peer = null;
        try
        {
            await session.StartAsync(CancellationToken.None);
            peer = await listener.AcceptTcpClientAsync().WaitAsync(StepTimeout);
            NetworkStream stream = peer.GetStream();
            await ReadFixFrameAsync(stream);
            await SendFixFrameAsync(stream, CreateLogonAcknowledgement(30));
            await WaitForLifecycleStateAsync(auditChannel.Reader, SessionState.Active);

            await SendFixFrameAsync(
                stream,
                CreateFixFrame(
                    "35=4",
                    "49=EXCHANGE",
                    "56=CLIENT",
                    "34=2",
                    "52=20260101-12:00:02",
                    "43=Y",
                    gapFill,
                    "36=8"));

            await WaitForExpectedAsync(log.Ingress, 8);
            Assert.Equal(0, router.Count);
        }
        finally
        {
            listener.Stop();
            peer?.Dispose();
            await session.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FixSession_WhenSequenceResetLowersExpected_RepliesReject()
    {
        byte[] reply = await SendSequenceResetAfterLogonAsync("35=4", "49=EXCHANGE", "56=CLIENT", "34=2", "52=20260101-12:00:02", "123=N", "36=1");
        Assert.Equal("3", GetFieldValue(reply, 35));
        Assert.Equal("2", GetFieldValue(reply, 45));
        Assert.Equal("36", GetFieldValue(reply, 371));
        Assert.Equal("5", GetFieldValue(reply, 373));
        FixMessageBuilderTests.AssertValidBodyLengthAndChecksum(reply);
    }

    [Theory]
    [InlineData("36=abc", "6")]
    [InlineData("123=Y", "1")]
    [Trait("Category", "Integration")]
    public async Task FixSession_WhenNewSeqNoMissingOrInvalid_RepliesReject(string newSeqField, string reason)
    {
        byte[] reply = await SendSequenceResetAfterLogonAsync(
            "35=4",
            "49=EXCHANGE",
            "56=CLIENT",
            "34=2",
            "52=20260101-12:00:02",
            newSeqField);
        Assert.Equal("3", GetFieldValue(reply, 35));
        Assert.Equal("36", GetFieldValue(reply, 371));
        Assert.Equal(reason, GetFieldValue(reply, 373));
        FixMessageBuilderTests.AssertValidBodyLengthAndChecksum(reply);
    }

    private async Task<byte[]> SendSequenceResetAfterLogonAsync(params string[] fields)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Channel<AuditRecord> auditChannel = CreateAuditChannel();
        var session = CreateSession(
            port,
            auditChannel.Writer,
            new FixedReconnectPolicy(TimeSpan.FromHours(1)),
            heartBtInt: 30);
        TcpClient? peer = null;
        try
        {
            await session.StartAsync(CancellationToken.None);
            peer = await listener.AcceptTcpClientAsync().WaitAsync(StepTimeout);
            NetworkStream stream = peer.GetStream();
            await ReadFixFrameAsync(stream);
            await SendFixFrameAsync(stream, CreateLogonAcknowledgement(30));
            await WaitForLifecycleStateAsync(auditChannel.Reader, SessionState.Active);
            await SendFixFrameAsync(stream, CreateFixFrame(fields));
            return await ReadFixFrameAsync(stream);
        }
        finally
        {
            listener.Stop();
            peer?.Dispose();
            await session.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitForExpectedAsync(MemoryConsumerState state, long expected)
    {
        DateTime deadline = DateTime.UtcNow + StepTimeout;
        while (state.NextInExpected != expected && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Equal(expected, state.NextInExpected);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FixSession_WhenLogonEchoesHeartBtAndReset_BecomesActiveWithoutLoggingPassword()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Channel<AuditRecord> auditChannel = CreateAuditChannel();
        var logger = new CollectingLogger();
        var session = CreateSession(
            port,
            auditChannel.Writer,
            new FixedReconnectPolicy(TimeSpan.FromHours(1)),
            heartBtInt: 30,
            resetSeqNumFlag: true,
            passwordEnvVar: "NEXUS_FIX_PASSWORD",
            secretResolver: new MapSecretResolver("NEXUS_FIX_PASSWORD", "s3cret"),
            logger: logger,
            logonText: "NexusRouter",
            cancelOnDisconnectType: 1,
            codTimeoutWindow: 30000);
        TcpClient? peer = null;

        try
        {
            await session.StartAsync(CancellationToken.None);
            peer = await listener.AcceptTcpClientAsync().WaitAsync(StepTimeout);
            NetworkStream stream = peer.GetStream();
            byte[] logon = await ReadFixFrameAsync(stream);

            Assert.Equal("Y", GetFieldValue(logon, 141));
            Assert.Equal("30", GetFieldValue(logon, 108));
            Assert.Equal("6", GetFieldValue(logon, 95));
            Assert.Equal("s3cret", GetFieldValue(logon, 96));
            Assert.Equal("NexusRouter", GetFieldValue(logon, 58));
            Assert.Equal("1", GetFieldValue(logon, 35002));
            Assert.Equal("30000", GetFieldValue(logon, 35003));
            Assert.DoesNotContain(logger.Messages, message => message.Contains("s3cret", StringComparison.Ordinal));

            await SendFixFrameAsync(stream, CreateLogonAcknowledgement(30, resetSeqNum: true));
            await WaitForLifecycleStateAsync(auditChannel.Reader, SessionState.Active);
            Assert.DoesNotContain(logger.Messages, message => message.Contains("s3cret", StringComparison.Ordinal));
        }
        finally
        {
            listener.Stop();
            peer?.Dispose();
            await session.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(false, 99, false)]
    [InlineData(true, 30, false)]
    [Trait("Category", "Integration")]
    public async Task FixSession_WhenLogonEchoMismatches_SendsLogout(bool resetSeqNumFlag, int heartBtInt, bool echoReset)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Channel<AuditRecord> auditChannel = CreateAuditChannel();
        var session = CreateSession(
            port,
            auditChannel.Writer,
            new FixedReconnectPolicy(TimeSpan.FromHours(1)),
            heartBtInt: 30,
            resetSeqNumFlag: resetSeqNumFlag);
        TcpClient? peer = null;

        try
        {
            await session.StartAsync(CancellationToken.None);
            peer = await listener.AcceptTcpClientAsync().WaitAsync(StepTimeout);
            NetworkStream stream = peer.GetStream();
            _ = await ReadFixFrameAsync(stream);

            await SendFixFrameAsync(stream, CreateLogonAcknowledgement(heartBtInt, echoReset));
            byte[] reply = await ReadFixFrameAsync(stream);
            Assert.Equal("5", GetFieldValue(reply, 35));
        }
        finally
        {
            listener.Stop();
            peer?.Dispose();
            await session.StopAsync(CancellationToken.None);
        }
    }

    private static FixSession CreateSession(
        int port,
        ChannelWriter<AuditRecord> auditWriter,
        IReconnectPolicy reconnectPolicy,
        int heartBtInt = 1,
        bool resetSeqNumFlag = false,
        string? passwordEnvVar = null,
        ISecretResolver? secretResolver = null,
        ILogger<FixSession>? logger = null,
        string? logonText = null,
        int? cancelOnDisconnectType = null,
        int? codTimeoutWindow = null,
        IMessageRouter? router = null,
        TimeSpan? sendingTimeTolerance = null,
        ISessionLog? sessionLog = null) =>
        new(
            new SessionOptions
            {
                SessionId = "FIX-SESSION-TEST",
                Host = IPAddress.Loopback.ToString(),
                Port = port,
                SenderCompId = "CLIENT",
                TargetCompId = "EXCHANGE",
                BeginString = "FIX.4.4",
                HeartBtInt = heartBtInt,
                LogonText = logonText,
                PasswordEnvVar = passwordEnvVar,
                CancelOnDisconnectType = cancelOnDisconnectType,
                CodTimeoutWindow = codTimeoutWindow,
                ResetSeqNumFlag = resetSeqNumFlag,
                MissedHeartbeatThreshold = 1,
                LogonTimeout = TimeSpan.FromSeconds(5),
                ShutdownTimeoutSeconds = 2,
                SendingTimeTolerance = sendingTimeTolerance ?? TimeSpan.Zero,
            },
            new Fix44FrameDecoder(),
            reconnectPolicy,
            router ?? new NullRouter(),
            auditWriter,
            secretResolver ?? ThrowingSecretResolver.Instance,
            logger ?? NullLogger<FixSession>.Instance,
            sessionLog ?? new NullSessionLog());

    // TLS está desligado nestes testes, então o resolvedor nunca deve ser invocado.
    private sealed class NullSessionLog : ISessionLog
    {
        public ISessionLogReader Reader { get; } = new NullReader();

        public event Func<CancellationToken, ValueTask>? DayRolloverStarting
        {
            add { }
            remove { }
        }

        public event Action? DayRolloverCompleted
        {
            add { }
            remove { }
        }

        public MemoryConsumerState Ingress { get; } = new();

        public ISessionLogStream OpenStream(string sessionId) => new NullStream();

        public IConsumerState OpenStreamState(string sessionId) => Ingress;

        public ClientBinding OpenClient(string dispatcherId, string clientKey) =>
            throw new NotSupportedException();

        public ClientBinding ResetClient(string dispatcherId, string clientKey) =>
            throw new NotSupportedException();

        public IConsumerState OpenBroker(string dispatcherId) =>
            throw new NotSupportedException();

        public int CopyClients(string dispatcherId, Span<ClientBinding> destination) => 0;

        public TradingDayStreamSnapshot[] GetTradingDayStreams() => [];

        private sealed class NullStream : ISessionLogStream
        {
            public ushort StreamId => 1;

            public int Epoch => 0;

            public long Committed => 0;

            public long SyncRotationCount => 0;

            public bool IsDegraded => false;

            public AppendResult Append(ReadOnlySpan<byte> frame, long inSeq, long ingressTs) =>
                AppendResult.Ok(new LogRef(StreamId, Epoch, inSeq));

            public void BeginEpoch(int epoch, long baseSeq)
            {
            }
        }

        private sealed class NullReader : ISessionLogReader
        {
            public bool TryRead(ushort stream, int epoch, long inSeq, IBufferWriter<byte> destination) => false;

            public bool TryRead(
                ushort stream,
                int epoch,
                long inSeq,
                IBufferWriter<byte> destination,
                out long ingressTimestampTicks)
            {
                ingressTimestampTicks = 0;
                return false;
            }

            public bool TryReadAt(ushort stream, uint segment, ulong offset, IBufferWriter<byte> destination) => false;

            public long GetCommitted(ushort stream, int epoch) => 0;
        }
    }

    private sealed class ThrowingSecretResolver : ISecretResolver
    {
        public static readonly ThrowingSecretResolver Instance = new();

        public string ResolveRequired(string key) =>
            throw new InvalidOperationException(
                $"O resolvedor de segredos não deveria ser chamado neste teste (chave '{key}').");
    }

    private static Channel<AuditRecord> CreateAuditChannel() =>
        Channel.CreateUnbounded<AuditRecord>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    private static async Task<byte[]> ReadFixFrameAsync(NetworkStream stream)
    {
        var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        Task reader = Task.Run(async () =>
        {
            try
            {
                byte[]? frame = await ReadOneFixFrameAsync(stream, cancellation.Token);
                if (frame is null)
                {
                    throw new EndOfStreamException("O peer fechou o socket antes de enviar um frame FIX completo.");
                }

                completion.TrySetResult(frame);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });

        try
        {
            return await completion.Task.WaitAsync(StepTimeout);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException("O peer não enviou o frame FIX esperado dentro do limite.", exception);
        }
        finally
        {
            cancellation.Cancel();
            await reader;
        }
    }

    private static async Task<byte[]?> ReadOneFixFrameAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(capacity: 256);
        byte[] singleByte = new byte[1];
        while (true)
        {
            int read = await stream.ReadAsync(singleByte, cancellationToken);
            if (read == 0)
            {
                return null;
            }

            bytes.Add(singleByte[0]);
            if (HasChecksumTerminator(bytes))
            {
                return bytes.ToArray();
            }
        }
    }

    private static bool HasChecksumTerminator(List<byte> bytes)
    {
        int start = bytes.Count - 8;
        return start >= 0
            && bytes[start] == 0x01
            && bytes[start + 1] == (byte)'1'
            && bytes[start + 2] == (byte)'0'
            && bytes[start + 3] == (byte)'='
            && bytes[start + 4] is >= (byte)'0' and <= (byte)'9'
            && bytes[start + 5] is >= (byte)'0' and <= (byte)'9'
            && bytes[start + 6] is >= (byte)'0' and <= (byte)'9'
            && bytes[start + 7] == 0x01;
    }

    private static async Task SendFixFrameAsync(NetworkStream stream, byte[] frame)
    {
        using var cancellation = new CancellationTokenSource(StepTimeout);
        await stream.WriteAsync(frame, cancellation.Token);
    }

    private static async Task WaitForLifecycleStateAsync(
        ChannelReader<AuditRecord> auditReader,
        SessionState expectedState)
    {
        var completion = new TaskCompletionSource<SessionState>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        Task reader = Task.Run(async () =>
        {
            try
            {
                await foreach (AuditRecord record in auditReader.ReadAllAsync(cancellation.Token))
                {
                    if (record.LifecycleEvent?.NewState == expectedState)
                    {
                        completion.TrySetResult(expectedState);
                        return;
                    }
                }

                completion.TrySetException(
                    new InvalidOperationException($"O canal foi concluído antes do estado {expectedState}."));
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });

        try
        {
            await completion.Task.WaitAsync(StepTimeout);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"A sessão não publicou o estado {expectedState} dentro do limite.", exception);
        }
        finally
        {
            cancellation.Cancel();
            await reader;
        }
    }

    private static byte[] CreateLogonAcknowledgement(int heartBtInt = 1, bool resetSeqNum = false)
    {
        var fields = new List<string>
        {
            "35=A",
            "49=EXCHANGE",
            "56=CLIENT",
            "34=1",
            "52=20260101-12:00:00",
            "98=0",
            $"108={heartBtInt}",
        };
        if (resetSeqNum)
        {
            fields.Add("141=Y");
        }

        return CreateFixFrame(fields.ToArray());
    }

    private static byte[] CreateHeartbeatAcknowledgement(string testRequestId, int sequenceNumber) =>
        CreateFixFrame(
            "35=0",
            "49=EXCHANGE",
            "56=CLIENT",
            $"34={sequenceNumber}",
            "52=20260101-12:00:01",
            $"112={testRequestId}");

    private static byte[] CreateFixFrame(params string[] fields)
    {
        var bodyBuilder = new StringBuilder();
        foreach (string field in fields)
        {
            bodyBuilder.Append(field).Append('\x01');
        }

        string body = bodyBuilder.ToString();
        int bodyLength = Encoding.ASCII.GetByteCount(body);
        string prefix = $"8=FIX.4.4\u00019={bodyLength}\u0001{body}";
        byte[] prefixBytes = Encoding.ASCII.GetBytes(prefix);
        int checksum = 0;
        foreach (byte value in prefixBytes)
        {
            checksum += value;
        }

        return Encoding.ASCII.GetBytes($"{prefix}10={checksum & 0xFF:D3}\u0001");
    }

    private static string GetFieldValue(ReadOnlySpan<byte> frame, int tag)
    {
        string prefix = $"{tag}=";
        string message = Encoding.ASCII.GetString(frame);
        foreach (string field in message.Split('\x01', StringSplitOptions.RemoveEmptyEntries))
        {
            if (field.StartsWith(prefix, StringComparison.Ordinal))
            {
                return field[prefix.Length..];
            }
        }

        throw new InvalidDataException($"O campo FIX {tag} não foi encontrado.");
    }

    private sealed class MemoryConsumerState : IConsumerState
    {
        private long _nextInExpected = 1;

        public long NextOut => 1;

        public long NextInExpected
        {
            get => Volatile.Read(ref _nextInExpected);
            set => Volatile.Write(ref _nextInExpected, value);
        }

        public long Epoch { get; set; }

        public long GetExpected(ushort stream) => 0;

        public void SetExpected(ushort stream, long inSeq)
        {
        }

        public long Cursor(ushort stream) => 0;

        public void SetCursor(ushort stream, long value)
        {
        }
    }

    private sealed class NullRouter : IMessageRouter
    {
        public void Route(in IngressFrame frame)
        {
        }
    }

    private sealed class CountingRouter : IMessageRouter
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Route(in IngressFrame frame) => Interlocked.Increment(ref _count);

        public async Task WaitForCountAsync(int expected)
        {
            DateTime deadline = DateTime.UtcNow + StepTimeout;
            while (Count < expected && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }
        }
    }

    private sealed class FixedReconnectPolicy(TimeSpan delay) : IReconnectPolicy
    {
        public TimeSpan ComputeDelay(int attemptNumber) => delay;
    }

    private sealed class MapSecretResolver(string key, string value) : ISecretResolver
    {
        public string ResolveRequired(string lookup) =>
            lookup == key
                ? value
                : throw new InvalidOperationException($"Segredo '{lookup}' não definido neste teste.");
    }

    private sealed class CollectingLogger : ILogger<FixSession>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull =>
            NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

    private sealed class SequencedReconnectPolicy : IReconnectPolicy
    {
        private int _attempt;

        public TimeSpan ComputeDelay(int attemptNumber) =>
            Interlocked.Increment(ref _attempt) == 1
                ? TimeSpan.Zero
                : TimeSpan.FromHours(1);
    }
}
