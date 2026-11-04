using System.Net;
using System.Net.Sockets;
using System.Text;
using DropCopySim.Core.Acceptor;
using DropCopySim.Core.Fix;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DropCopySim.Integration.Tests;

public sealed class AcceptorSessionProtocolTests
{
    [Fact]
    public async Task RunAsync_ValidLogon_HandlesHeartbeatTestRequestAndLogout()
    {
        int port = GetAvailablePort();
        var service = CreateService(port, heartBtIntSeconds: 1);
        await service.StartAsync(CancellationToken.None);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            NetworkStream stream = client.GetStream();

            await stream.WriteAsync(BuildClientFrame("A", 1, (FixTags.HeartBtInt, "1"), (FixTags.ResetSeqNumFlag, "Y")));

            string logonAck = await ReadFrameAsync(stream);
            Assert.Equal("A", GetField(logonAck, FixTags.MsgType));
            Assert.Equal("0", GetField(logonAck, FixTags.EncryptMethod));
            Assert.Equal("1", GetField(logonAck, FixTags.HeartBtInt));
            Assert.Equal("0", GetField(logonAck, FixTags.ApplicationLogonRspCode));
            Assert.Equal("1", GetField(logonAck, FixTags.MsgSeqNum));

            await stream.WriteAsync(BuildClientFrame("1", 2, (FixTags.TestReqId, "probe-1")));
            string testResponse = await ReadFrameAsync(stream);
            Assert.Equal("0", GetField(testResponse, FixTags.MsgType));
            Assert.Equal("probe-1", GetField(testResponse, FixTags.TestReqId));

            string idleHeartbeat = await ReadFrameAsync(stream);
            Assert.Equal("0", GetField(idleHeartbeat, FixTags.MsgType));

            await stream.WriteAsync(BuildClientFrame("5", 3));
            string logout = await ReadFrameAsync(stream);
            Assert.Equal("5", GetField(logout, FixTags.MsgType));
            await WaitForConditionAsync(() => service.ActiveSession is null);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RunAsync_UnsupportedBeginString_RejectsAndClosesSession()
    {
        int port = GetAvailablePort();
        var service = CreateService(port, heartBtIntSeconds: 1);
        await service.StartAsync(CancellationToken.None);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            NetworkStream stream = client.GetStream();

            await stream.WriteAsync(BuildRawClientFrame("FIX.4.2", "A", (FixTags.HeartBtInt, "1")));

            string reject = await ReadFrameAsync(stream);
            Assert.Equal("3", GetField(reject, FixTags.MsgType));
            Assert.Contains("BeginString", GetField(reject, FixTags.Text), StringComparison.Ordinal);
            await WaitForConditionAsync(() => service.ActiveSession is null);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static FixAcceptorService CreateService(int port, int heartBtIntSeconds) =>
        new(
            Options.Create(new AcceptorOptions
            {
                ListenAddress = IPAddress.Loopback.ToString(),
                ListenPort = port,
                HeartBtIntSeconds = heartBtIntSeconds,
            }),
            NullLogger<FixAcceptorService>.Instance,
            NullLogger<AcceptorSession>.Instance);

    private static byte[] BuildClientFrame(string msgType, long sequenceNumber, params (int Tag, string Value)[] fields)
    {
        byte[] buffer = new byte[1024];
        var writer = new B3FixMessageWriter(buffer);
        writer.WriteB3Header(
            msgType,
            "CLIENT1",
            "ENTRYPOINT-FIRM-0001",
            sequenceNumber,
            DateTime.UtcNow,
            sequenceNumber);

        foreach ((int tag, string value) in fields)
        {
            writer.WriteField(tag, value);
        }

        int length = writer.Finish();
        return buffer.AsSpan(0, length).ToArray();
    }

    private static byte[] BuildRawClientFrame(string beginString, string msgType, params (int Tag, string Value)[] fields)
    {
        const char soh = '\x01';
        var body = new StringBuilder()
            .Append(FixTags.MsgType).Append('=').Append(msgType).Append(soh)
            .Append(FixTags.SenderCompId).Append("=CLIENT1").Append(soh)
            .Append(FixTags.TargetCompId).Append("=ENTRYPOINT-FIRM-0001").Append(soh)
            .Append(FixTags.MsgSeqNum).Append("=1").Append(soh)
            .Append(FixTags.SendingTime).Append("=20260101-00:00:00.000").Append(soh)
            .Append(FixTags.BusinessSeqNum).Append("=1").Append(soh);

        foreach ((int tag, string value) in fields)
        {
            body.Append(tag).Append('=').Append(value).Append(soh);
        }

        string prefix = $"8={beginString}{soh}9={body.Length:000000}{soh}{body}";
        int checksum = Encoding.ASCII.GetBytes(prefix).Sum(static value => value) & 0xFF;
        return Encoding.ASCII.GetBytes($"{prefix}10={checksum:000}{soh}");
    }

    private static async Task<string> ReadFrameAsync(NetworkStream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var frame = new MemoryStream();
        byte[] nextByte = new byte[1];

        while (true)
        {
            int bytesRead = await stream.ReadAsync(nextByte, timeout.Token);
            if (bytesRead == 0)
            {
                throw new IOException("The acceptor closed the connection before sending a complete frame.");
            }

            frame.WriteByte(nextByte[0]);
            if (frame.Length < 8)
            {
                continue;
            }

            byte[] written = frame.GetBuffer();
            int end = checked((int)frame.Length);
            ReadOnlySpan<byte> trailer = written.AsSpan(end - 8, 8);
            if (trailer[0] == 0x01
                && trailer[1] == (byte)'1'
                && trailer[2] == (byte)'0'
                && trailer[3] == (byte)'='
                && trailer[7] == 0x01)
            {
                return Encoding.ASCII.GetString(written, 0, end);
            }
        }
    }

    private static string GetField(string frame, int tag)
    {
        string prefix = $"{tag}=";
        string field = frame.Split('\x01').First(value => value.StartsWith(prefix, StringComparison.Ordinal));
        return field[prefix.Length..];
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task WaitForConditionAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }
}
