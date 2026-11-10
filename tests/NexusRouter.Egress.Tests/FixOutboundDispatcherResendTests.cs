using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NexusRouter.Domain.Abstractions;
using NexusRouter.Domain.Enums;
using NexusRouter.Domain.Models;
using NexusRouter.Domain.Pooling;
using NexusRouter.Domain.Secrets;
using NexusRouter.Egress.Fix;
using NexusRouter.Ingress.Sessions;
using NexusRouter.SessionLog;

namespace NexusRouter.Egress.Tests;

/// <summary>
/// Cenário A4 do roteiro de certificação: o cliente pede <c>ResendRequest</c> e confere o SeqNum da primeira e da última mensagem.
/// </summary>
public sealed class FixOutboundDispatcherResendTests
{
    private const char Soh = '\u0001';

    [Theory]
    [InlineData(DeliveryMode.PassThrough)]
    [InlineData(DeliveryMode.Parse)]
    public async Task ResendRequest_WhenClientWasOffline_ReplaysApplicationMessagesWithPossDup(DeliveryMode mode)
    {
        string directory = TempDirectory();
        int port = FreePort();
        await using Harness harness = await StartAsync(directory, port, mode);
        try
        {
            await harness.AppendAndDispatchAsync("A1", "A2", "A3");
            using TcpClient client = await ConnectAsync(port, "OMS1");
            var stream = new FrameStream(client.GetStream());
            byte[] logon = await stream.ReadFrameAsync();
            Assert.Equal("A", Field(logon, 35));
            Assert.Equal("4", Field(logon, 34));

            await stream.WriteAsync(ResendRequest("OMS1", msgSeqNum: 2, begin: 1, end: 0));

            string[] expected = ["A1", "A2", "A3"];
            for (int i = 0; i < expected.Length; i++)
            {
                byte[] replay = await stream.ReadFrameAsync();
                Assert.Equal("8", Field(replay, 35));
                Assert.Equal((i + 1).ToString(), Field(replay, 34));
                Assert.Equal("Y", Field(replay, 43));
                Assert.Equal("20260101-00:00:00.000", Field(replay, 122));
                Assert.Equal(expected[i], Field(replay, 11));
                Assert.Equal(Field(replay, 9), BodyLength(replay).ToString());
                Assert.Equal(Checksum(replay).ToString("000"), Field(replay, 10));
            }
        }
        finally
        {
            await harness.DisposeAsync();
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task ResendRequest_WhenRangeStartsWithAdministrativeMessage_SendsGapFillThenApplication()
    {
        string directory = TempDirectory();
        int port = FreePort();
        await using Harness harness = await StartAsync(directory, port, DeliveryMode.PassThrough);
        try
        {
            using TcpClient client = await ConnectAsync(port, "OMS1");
            var stream = new FrameStream(client.GetStream());
            Assert.Equal("1", Field(await stream.ReadFrameAsync(), 34));
            await harness.AppendAndDispatchAsync("B1", "B2");
            Assert.Equal("2", Field(await stream.ReadFrameAsync(), 34));
            Assert.Equal("3", Field(await stream.ReadFrameAsync(), 34));

            await stream.WriteAsync(ResendRequest("OMS1", msgSeqNum: 2, begin: 1, end: 3));

            byte[] gapFill = await stream.ReadFrameAsync();
            Assert.Equal("4", Field(gapFill, 35));
            Assert.Equal("1", Field(gapFill, 34));
            Assert.Equal("2", Field(gapFill, 36));
            Assert.Equal("Y", Field(gapFill, 123));
            Assert.Equal("Y", Field(gapFill, 43));
            byte[] first = await stream.ReadFrameAsync();
            Assert.Equal("2", Field(first, 34));
            Assert.Equal("Y", Field(first, 43));
            Assert.Equal("B1", Field(first, 11));
            byte[] last = await stream.ReadFrameAsync();
            Assert.Equal("3", Field(last, 34));
            Assert.Equal("B2", Field(last, 11));
        }
        finally
        {
            await harness.DisposeAsync();
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task ResendRequest_WhenEndSeqNoIsInsideTheRange_StopsAtEndSeqNo()
    {
        string directory = TempDirectory();
        int port = FreePort();
        await using Harness harness = await StartAsync(directory, port, DeliveryMode.PassThrough);
        try
        {
            await harness.AppendAndDispatchAsync("C1", "C2", "C3");
            using TcpClient client = await ConnectAsync(port, "OMS1");
            var stream = new FrameStream(client.GetStream());
            _ = await stream.ReadFrameAsync();

            await stream.WriteAsync(ResendRequest("OMS1", msgSeqNum: 2, begin: 2, end: 2));

            byte[] only = await stream.ReadFrameAsync();
            Assert.Equal("2", Field(only, 34));
            Assert.Equal("C2", Field(only, 11));
            await stream.ExpectSilenceAsync(TimeSpan.FromMilliseconds(300));
        }
        finally
        {
            await harness.DisposeAsync();
            DeleteDirectory(directory);
        }
    }

    private static async Task<Harness> StartAsync(string directory, int port, DeliveryMode mode)
    {
        var log = new SessionLogManager(
            new SessionLogOptions
            {
                Directory = directory,
                SegmentSizeMB = 1,
                IndexSlotsPerSegment = 100_000,
                FlushIntervalMs = 60_000,
                RetentionTradingDays = 5,
            },
            NullLogger<SessionLogManager>.Instance);
        await log.StartAsync(CancellationToken.None);
        ISessionLogStream stream = log.OpenStream("B3");
        var dispatcher = new FixOutboundDispatcher(
            "fix-acceptor",
            new FixOutboundDispatcherOptions
            {
                ListenAddress = "127.0.0.1",
                ListenPort = port,
                SenderCompId = "NEXUS",
                BeginString = "FIX.4.4",
                AllowedSenderCompIds = [],
                HeartBtInt = 30,
                LogonTimeout = TimeSpan.FromSeconds(2),
            },
            NullLogger<FixOutboundDispatcher>.Instance,
            new DispatchPayloadPool(),
            new ThrowingSecretResolver(),
            log,
            catchUp: null,
            resendDeliveryMode: _ => mode);
        log.OpenClient("fix-acceptor", "OMS1");
        await dispatcher.StartAsync(CancellationToken.None);
        return new Harness(dispatcher, log, stream, mode);
    }

    private static async Task<TcpClient> ConnectAsync(int port, string senderCompId)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var options = new SessionOptions
        {
            SessionId = senderCompId,
            Host = "127.0.0.1",
            Port = 1,
            SenderCompId = senderCompId,
            TargetCompId = "NEXUS",
            BeginString = "FIX.4.4",
            HeartBtInt = 30,
        };
        byte[] buffer = new byte[512];
        int length = FixMessageBuilder.BuildLogon(buffer, options, 1).Length;
        await client.GetStream().WriteAsync(buffer.AsMemory(0, length));
        return client;
    }

    private static byte[] ResendRequest(string senderCompId, long msgSeqNum, long begin, long end) =>
        WithChecksum($"35=2{Soh}49={senderCompId}{Soh}56=NEXUS{Soh}34={msgSeqNum}{Soh}52=20260101-00:00:00.000{Soh}7={begin}{Soh}16={end}{Soh}");

    private static byte[] ExecutionReport(string clOrdId, long seq) =>
        WithChecksum($"35=8{Soh}49=EXCH{Soh}56=CLIENT{Soh}34={seq}{Soh}52=20260101-00:00:00.000{Soh}11={clOrdId}{Soh}");

    private static byte[] WithChecksum(string body)
    {
        string head = $"8=FIX.4.4{Soh}9={body.Length}{Soh}";
        string withoutChecksum = head + body;
        int sum = 0;
        foreach (char ch in withoutChecksum)
        {
            sum += ch;
        }

        return Encoding.ASCII.GetBytes(withoutChecksum + "10=" + (sum % 256).ToString("000") + Soh);
    }

    private sealed class FrameStream
    {
        private readonly Stream _inner;
        private readonly List<byte> _pending = [];

        public FrameStream(Stream inner) => _inner = inner;

        public ValueTask WriteAsync(byte[] bytes) => _inner.WriteAsync(bytes);

        public async Task<byte[]> ReadFrameAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var chunk = new byte[4096];
            while (true)
            {
                ReadOnlySpan<byte> span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_pending);
                int marker = span.IndexOf("10="u8);
                if (marker >= 0 && marker + 6 < span.Length && span[marker + 6] == 1)
                {
                    byte[] frame = span[..(marker + 7)].ToArray();
                    _pending.RemoveRange(0, marker + 7);
                    return frame;
                }

                int read = await _inner.ReadAsync(chunk, timeout.Token);
                if (read == 0)
                {
                    throw new IOException("O socket fechou antes do frame.");
                }

                _pending.AddRange(chunk.AsSpan(0, read));
            }
        }

        public async Task ExpectSilenceAsync(TimeSpan window)
        {
            using var timeout = new CancellationTokenSource(window);
            var chunk = new byte[64];
            try
            {
                int read = await _inner.ReadAsync(chunk, timeout.Token);
                Assert.Fail($"Chegaram {read} bytes que o teste não esperava.");
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static string Field(byte[] frame, int tag)
    {
        int index = 0;
        while (index < frame.Length)
        {
            int parsed = 0;
            int start = index;
            while (index < frame.Length && frame[index] is >= (byte)'0' and <= (byte)'9')
            {
                parsed = (parsed * 10) + (frame[index] - (byte)'0');
                index++;
            }

            if (index == start || index >= frame.Length || frame[index] != (byte)'=')
            {
                break;
            }

            index++;
            int valueStart = index;
            int end = Array.IndexOf(frame, (byte)1, index);
            if (end < 0)
            {
                break;
            }

            if (parsed == tag)
            {
                return Encoding.ASCII.GetString(frame, valueStart, end - valueStart);
            }

            index = end + 1;
        }

        throw new InvalidOperationException($"A tag {tag} não está no frame.");
    }

    private static int BodyLength(byte[] frame)
    {
        string text = Encoding.ASCII.GetString(frame);
        int bodyStart = text.IndexOf(Soh, text.IndexOf($"{Soh}9=", StringComparison.Ordinal) + 1) + 1;
        return text.LastIndexOf($"{Soh}10=", StringComparison.Ordinal) + 1 - bodyStart;
    }

    private static int Checksum(byte[] frame)
    {
        int checksumTag = Encoding.ASCII.GetString(frame).LastIndexOf($"{Soh}10=", StringComparison.Ordinal);
        int sum = 0;
        for (int i = 0; i <= checksumTag; i++)
        {
            sum += frame[i];
        }

        return sum % 256;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string TempDirectory() =>
        Path.Combine(Path.GetTempPath(), "nexusrouter-resend-" + Guid.NewGuid().ToString("N"));

    private static void DeleteDirectory(string directory)
    {
        for (int attempt = 0; attempt < 10 && Directory.Exists(directory); attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 9)
            {
                Thread.Sleep(50);
            }
        }
    }

    private sealed class ThrowingSecretResolver : ISecretResolver
    {
        public string ResolveRequired(string key) =>
            throw new InvalidOperationException($"O resolvedor não deveria ser chamado (chave '{key}').");
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly FixOutboundDispatcher _dispatcher;
        private readonly SessionLogManager _log;
        private readonly ISessionLogStream _stream;
        private readonly DeliveryMode _mode;
        private long _inSeq;

        public Harness(FixOutboundDispatcher dispatcher, SessionLogManager log, ISessionLogStream stream, DeliveryMode mode)
        {
            _dispatcher = dispatcher;
            _log = log;
            _stream = stream;
            _mode = mode;
        }

        public async Task AppendAndDispatchAsync(params string[] clOrdIds)
        {
            foreach (string clOrdId in clOrdIds)
            {
                long inSeq = ++_inSeq;
                byte[] frame = ExecutionReport(clOrdId, inSeq);
                AppendResult appended = _stream.Append(frame, inSeq, ingressTs: 1);
                Assert.True(appended.IsOk);
                DispatchPayload payload = Payload(frame);
                payload.LogRef = appended.Ref;
                Assert.True(_dispatcher.TryEnqueue(payload));
            }

            ClientBinding binding = _log.OpenClient("fix-acceptor", "OMS1");
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline)
            {
                long published = binding.Index.NextOut - 1;
                if (published >= clOrdIds.Length && binding.Index.TryGet(published, out _) == IndexReadResult.Published)
                {
                    return;
                }

                await Task.Delay(10);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _dispatcher.StopAsync(CancellationToken.None);
            await _log.StopAsync(CancellationToken.None);
        }

        private DispatchPayload Payload(byte[] frame)
        {
            byte[] rented = ArrayPool<byte>.Shared.Rent(frame.Length);
            frame.CopyTo(rented.AsSpan(0, frame.Length));
            return new DispatchPayload
            {
                DeliveryMode = _mode,
                RawBuffer = new ArrayPoolMemoryOwner<byte>(rented, frame.Length),
            };
        }
    }
}
