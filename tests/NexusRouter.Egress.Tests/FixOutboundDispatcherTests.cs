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

public sealed class FixOutboundDispatcherTests
{
    [Fact]
    public async Task SendAsync_TwoClients_EachReceivesItsOwnSequence()
    {
        string directory = TempDirectory();
        int port = FreePort();
        await using var running = await StartAsync(directory, port, allowed: []);
        FixOutboundDispatcher dispatcher = running.Dispatcher;
        try
        {
            using TcpClient first = await ConnectAsync(port, "OMS1");
            using TcpClient second = await ConnectAsync(port, "RISK1");

            byte[] firstLogon = await ReadFrameAsync(first.GetStream());
            byte[] secondLogon = await ReadFrameAsync(second.GetStream());
            Assert.Equal("A", Field(firstLogon, 35));
            Assert.Equal("NEXUS", Field(firstLogon, 49));
            Assert.Equal("OMS1", Field(firstLogon, 56));
            Assert.Equal("1", Field(firstLogon, 34));
            Assert.Equal("1", Field(secondLogon, 34));
            Assert.Equal("RISK1", Field(secondLogon, 56));

            Assert.True(dispatcher.TryEnqueue(Payload(ExecutionReport())));

            byte[] firstReport = await ReadFrameAsync(first.GetStream());
            byte[] secondReport = await ReadFrameAsync(second.GetStream());
            Assert.Equal("8", Field(firstReport, 35));
            Assert.Equal("2", Field(firstReport, 34));
            Assert.Equal("2", Field(secondReport, 34));
        }
        finally
        {
            await running.DisposeAsync();
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task SendAsync_AfterRestart_ContinuesSequenceForThatClient()
    {
        string directory = TempDirectory();
        int port = FreePort();
        await using var running = await StartAsync(directory, port, allowed: []);
        FixOutboundDispatcher dispatcher = running.Dispatcher;
        try
        {
            using var client = await ConnectAsync(port, "OMS1");
            Stream stream = client.GetStream();
            _ = await ReadFrameAsync(stream);
            Assert.True(dispatcher.TryEnqueue(Payload(ExecutionReport())));
            Assert.Equal("2", Field(await ReadFrameAsync(stream), 34));

            byte[] logout = await ReadLogoutOnStopAsync(dispatcher, stream);
            Assert.Equal("5", Field(logout, 35));
            Assert.Equal("3", Field(logout, 34));
            await running.Log.StopAsync(CancellationToken.None);

            int restartedPort = FreePort();
            await using var restarted = await StartAsync(directory, restartedPort, allowed: []);
            using var again = await ConnectAsync(restartedPort, "OMS1");
            byte[] logon = await ReadFrameAsync(again.GetStream());
            Assert.Equal("4", Field(logon, 34));
        }
        finally
        {
            await running.DisposeAsync();
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task SendAsync_WhenSessionSequenceHasMoreDigitsThanSourceFrame_DeliversValidFrame()
    {
        string directory = TempDirectory();
        Directory.CreateDirectory(Path.Combine(directory, "clients", "fix-acceptor", "OMS1"));
        await File.WriteAllTextAsync(
            Path.Combine(directory, "clients", "fix-acceptor", "OMS1", "state.json"),
            """{"nextOut":37582,"nextInExpected":1,"epoch":0,"updatedAt":"2026-11-09T00:00:00+00:00"}""");
        await File.WriteAllTextAsync(Path.Combine(directory, "clean"), string.Empty);
        int port = FreePort();
        await using var running = await StartAsync(directory, port, allowed: []);
        FixOutboundDispatcher dispatcher = running.Dispatcher;
        try
        {
            using TcpClient client = await ConnectAsync(port, "OMS1");
            Stream stream = client.GetStream();
            Assert.Equal("37582", Field(await ReadFrameAsync(stream), 34));

            Assert.True(dispatcher.TryEnqueue(Payload(ExecutionReport())));

            byte[] report = await ReadFrameAsync(stream);
            Assert.Equal("37583", Field(report, 34));
            Assert.Equal(Field(report, 9), BodyLength(report).ToString());
            Assert.Equal(Checksum(report).ToString("000"), Field(report, 10));
        }
        finally
        {
            await running.DisposeAsync();
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task SendAsync_WhenKnownClientIsOffline_PublishesTheIndexAndSkipsTheSocket()
    {
        string directory = TempDirectory();
        int port = FreePort();
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
        byte[] frame = ExecutionReport();
        AppendResult appended = stream.Append(frame, inSeq: 7, ingressTs: 1);
        Assert.True(appended.IsOk);
        log.OpenClient("fix-acceptor", "OMS1");
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
            ThrowingSecretResolver.Instance,
            log);
        try
        {
            await dispatcher.StartAsync(CancellationToken.None);
            DispatchPayload payload = Payload(frame);
            payload.LogRef = appended.Ref;
            Assert.True(dispatcher.TryEnqueue(payload));

            ClientBinding binding = log.OpenClient("fix-acceptor", "OMS1");
            var deadline = DateTime.UtcNow.AddSeconds(2);
            IndexReadResult published = IndexReadResult.NotYet;
            while (DateTime.UtcNow < deadline)
            {
                published = binding.Index.TryGet(1, out _);
                if (published == IndexReadResult.Published)
                {
                    break;
                }

                await Task.Delay(15);
            }

            Assert.Equal(2, binding.Index.NextOut);
            Assert.Equal(IndexReadResult.Published, published);

            using TcpClient client = await ConnectAsync(port, "OMS1");
            Assert.Equal("2", Field(await ReadFrameAsync(client.GetStream()), 34));
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
            await log.StopAsync(CancellationToken.None);
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task Logon_WhenSenderIsNotAllowed_ClosesTheSocket()
    {
        string directory = TempDirectory();
        int port = FreePort();
        await using var running = await StartAsync(directory, port, allowed: ["OMS1"]);
        FixOutboundDispatcher dispatcher = running.Dispatcher;
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            byte[] logon = Logon("OMS2", "NEXUS");
            await client.GetStream().WriteAsync(logon);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var buffer = new byte[256];
            int read = await client.GetStream().ReadAsync(buffer, timeout.Token);
            Assert.Equal(0, read);
        }
        finally
        {
            await running.DisposeAsync();
            DeleteDirectory(directory);
        }
    }

    private static async Task<RunningAcceptor> StartAsync(string directory, int port, IReadOnlyList<string> allowed)
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
        var dispatcher = new FixOutboundDispatcher(
            "fix-acceptor",
            new FixOutboundDispatcherOptions
            {
                ListenAddress = "127.0.0.1",
                ListenPort = port,
                SenderCompId = "NEXUS",
                BeginString = "FIX.4.4",
                AllowedSenderCompIds = allowed,
                HeartBtInt = 30,
                LogonTimeout = TimeSpan.FromSeconds(2),
            },
            NullLogger<FixOutboundDispatcher>.Instance,
            new DispatchPayloadPool(),
            ThrowingSecretResolver.Instance,
            log);
        await dispatcher.StartAsync(CancellationToken.None);
        Assert.True(dispatcher.IsHealthy);
        return new RunningAcceptor(dispatcher, log);
    }

    private sealed class RunningAcceptor : IAsyncDisposable
    {
        public RunningAcceptor(FixOutboundDispatcher dispatcher, SessionLogManager log)
        {
            Dispatcher = dispatcher;
            Log = log;
        }

        public FixOutboundDispatcher Dispatcher { get; }

        public SessionLogManager Log { get; }

        public async ValueTask DisposeAsync()
        {
            await Dispatcher.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await Log.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task<TcpClient> ConnectAsync(int port, string senderCompId)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        byte[] logon = Logon(senderCompId, "NEXUS");
        await client.GetStream().WriteAsync(logon);
        return client;
    }

    private static async Task<byte[]> ReadLogoutOnStopAsync(FixOutboundDispatcher dispatcher, Stream stream)
    {
        Task<byte[]> reading = ReadFrameAsync(stream);
        await dispatcher.StopAsync(CancellationToken.None);
        return await reading;
    }

    private static byte[] Logon(string senderCompId, string targetCompId)
    {
        var options = new SessionOptions
        {
            SessionId = senderCompId,
            Host = "127.0.0.1",
            Port = 1,
            SenderCompId = senderCompId,
            TargetCompId = targetCompId,
            BeginString = "FIX.4.4",
            HeartBtInt = 30,
        };
        byte[] buffer = new byte[512];
        int length = FixMessageBuilder.BuildLogon(buffer, options, 1).Length;
        return buffer[..length];
    }

    private static byte[] ExecutionReport()
    {
        const string body = "35=8\u000149=EXCH\u000156=CLIENT\u000134=1\u000152=20260101-00:00:00.000\u000111=ABC\u0001";
        string head = $"8=FIX.4.4\u00019={body.Length}\u0001";
        string withoutChecksum = head + body;
        int sum = 0;
        foreach (char ch in withoutChecksum)
        {
            sum += ch;
        }

        sum %= 256;
        return Encoding.ASCII.GetBytes(withoutChecksum + "10=" + sum.ToString("000") + "\u0001");
    }

    private static DispatchPayload Payload(byte[] frame)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(frame.Length);
        frame.CopyTo(rented.AsSpan(0, frame.Length));
        return new DispatchPayload
        {
            DeliveryMode = DeliveryMode.PassThrough,
            RawBuffer = new ArrayPoolMemoryOwner<byte>(rented, rented.Length),
        };
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buffer = new byte[4096];
        int count = 0;
        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(count), timeout.Token);
            if (read == 0)
            {
                throw new IOException("O socket fechou antes do frame.");
            }

            count += read;
            if (TryTakeFrame(buffer.AsSpan(0, count), out int frameLength))
            {
                return buffer[..frameLength];
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

    // Bytes depois do SOH do campo 9 até o SOH que antecede "10=", inclusive.
    private static int BodyLength(byte[] frame)
    {
        int bodyStart = Array.IndexOf(frame, (byte)1, Encoding.ASCII.GetString(frame).IndexOf("\u00019=", StringComparison.Ordinal) + 1) + 1;
        int checksumTag = Encoding.ASCII.GetString(frame).LastIndexOf("\u000110=", StringComparison.Ordinal);
        return checksumTag + 1 - bodyStart;
    }

    private static int Checksum(byte[] frame)
    {
        int checksumTag = Encoding.ASCII.GetString(frame).LastIndexOf("\u000110=", StringComparison.Ordinal);
        int sum = 0;
        for (int i = 0; i <= checksumTag; i++)
        {
            sum += frame[i];
        }

        return sum % 256;
    }

    private static bool TryTakeFrame(ReadOnlySpan<byte> buffer, out int frameLength)
    {
        ReadOnlySpan<byte> marker = "10="u8;
        int relative = buffer.IndexOf(marker);
        if (relative >= 0 && relative + 6 < buffer.Length && buffer[relative + 6] == 1)
        {
            frameLength = relative + 7;
            return true;
        }

        frameLength = 0;
        return false;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // TLS está desligado nestes testes, então o resolvedor nunca deve ser invocado.
    // Se for, falha alto em vez de ler silenciosamente uma credencial.
    private sealed class ThrowingSecretResolver : ISecretResolver
    {
        public static readonly ThrowingSecretResolver Instance = new();

        public string ResolveRequired(string key) =>
            throw new InvalidOperationException(
                $"O resolvedor de segredos não deveria ser chamado neste teste (chave '{key}').");
    }

    private static string TempDirectory() =>
        Path.Combine(Path.GetTempPath(), "nexusrouter-fix-" + Guid.NewGuid().ToString("N"));

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
}
