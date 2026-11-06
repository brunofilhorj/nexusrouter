using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

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

            await SendFixFrameAsync(stream, CreateLogonAcknowledgement());
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

    private static FixSession CreateSession(
        int port,
        ChannelWriter<AuditRecord> auditWriter,
        IReconnectPolicy reconnectPolicy,
        int heartBtInt = 1) =>
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
                MissedHeartbeatThreshold = 1,
                LogonTimeout = TimeSpan.FromSeconds(5),
                ShutdownTimeoutSeconds = 2,
            },
            new Fix44FrameDecoder(),
            reconnectPolicy,
            new NullRouter(),
            auditWriter,
            ThrowingSecretResolver.Instance,
            NullLogger<FixSession>.Instance);

    // TLS está desligado nestes testes, então o resolvedor nunca deve ser invocado.
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

    private static byte[] CreateLogonAcknowledgement() =>
        CreateFixFrame(
            "35=A",
            "49=EXCHANGE",
            "56=CLIENT",
            "34=1",
            "52=20260101-12:00:00",
            "98=0",
            "108=1");

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

    private sealed class NullRouter : IMessageRouter
    {
        public void Route(in IngressFrame frame)
        {
        }
    }

    private sealed class FixedReconnectPolicy(TimeSpan delay) : IReconnectPolicy
    {
        public TimeSpan ComputeDelay(int attemptNumber) => delay;
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
