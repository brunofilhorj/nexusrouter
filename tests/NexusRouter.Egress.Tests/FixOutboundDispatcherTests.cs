using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using NexusRouter.Domain.Enums;
using NexusRouter.Domain.Models;
using NexusRouter.Domain.Pooling;
using NexusRouter.Egress.Fix;
using NexusRouter.Ingress.Sessions;

namespace NexusRouter.Egress.Tests;

public sealed class FixOutboundDispatcherTests
{
    [Fact]
    public async Task SendAsync_TwoClients_EachReceivesItsOwnSequence()
    {
        string directory = TempDirectory();
        int port = FreePort();
        FixOutboundDispatcher dispatcher = await StartAsync(directory, port, allowed: []);
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
            await dispatcher.StopAsync(CancellationToken.None);
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task SendAsync_AfterRestart_ContinuesSequenceForThatClient()
    {
        string directory = TempDirectory();
        int port = FreePort();
        FixOutboundDispatcher dispatcher = await StartAsync(directory, port, allowed: []);
        FixOutboundDispatcher? restarted = null;
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

            int restartedPort = FreePort();
            restarted = await StartAsync(directory, restartedPort, allowed: []);
            using var again = await ConnectAsync(restartedPort, "OMS1");
            byte[] logon = await ReadFrameAsync(again.GetStream());
            Assert.Equal("4", Field(logon, 34));
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
            if (restarted is not null)
            {
                await restarted.StopAsync(CancellationToken.None);
            }

            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task Logon_WhenSenderIsNotAllowed_ClosesTheSocket()
    {
        string directory = TempDirectory();
        int port = FreePort();
        FixOutboundDispatcher dispatcher = await StartAsync(directory, port, allowed: ["OMS1"]);
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
            await dispatcher.StopAsync(CancellationToken.None);
            DeleteDirectory(directory);
        }
    }

    private static async Task<FixOutboundDispatcher> StartAsync(string directory, int port, IReadOnlyList<string> allowed)
    {
        var dispatcher = new FixOutboundDispatcher(
            "fix-acceptor",
            new FixOutboundDispatcherOptions
            {
                ListenAddress = "127.0.0.1",
                ListenPort = port,
                SenderCompId = "NEXUS",
                BeginString = "FIX.4.4",
                SequenceStoreDirectory = directory,
                AllowedSenderCompIds = allowed,
                HeartBtInt = 30,
                LogonTimeout = TimeSpan.FromSeconds(2),
            },
            NullLogger<FixOutboundDispatcher>.Instance);
        await dispatcher.StartAsync(CancellationToken.None);
        Assert.True(dispatcher.IsHealthy);
        return dispatcher;
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
