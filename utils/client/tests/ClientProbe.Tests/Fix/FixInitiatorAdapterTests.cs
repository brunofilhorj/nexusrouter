using System.Net;
using System.Net.Sockets;
using System.Text;
using ClientProbe.Fix;
using ClientProbe.Probe;
using ClientProbe.Telemetry;
using Microsoft.Extensions.Options;

namespace ClientProbe.Tests.Fix;

public sealed class FixInitiatorAdapterTests
{
    private sealed class Publisher : ITelemetryPublisher
    {
        public List<ProbeEvent> Events { get; } = [];

        public bool TryPublish(ProbeEvent probeEvent, ProbeCounters counters)
        {
            lock (Events)
            {
                Events.Add(probeEvent);
            }

            return true;
        }
    }

    private static FixInitiatorAdapter Create(Publisher publisher) =>
        new(publisher, Options.Create(new ClientProbeOptions()), TimeProvider.System);

    private static async Task<byte[]> ReadFrameAsync(NetworkStream stream, FixFrameScanner scanner)
    {
        var buffer = new byte[4096];
        while (true)
        {
            if (scanner.TryRead(out var frame))
            {
                return frame;
            }

            var read = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotEqual(0, read);
            scanner.Append(buffer.AsSpan(0, read));
        }
    }

    private static string Text(byte[] frame) => Encoding.ASCII.GetString(frame).Replace('\u0001', '|');

    [Fact]
    public async Task Connect_Logon_GoesLive_ThenLogoutOnDisconnect_AndPublishesApplicationFrames()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var publisher = new Publisher();
            var adapter = Create(publisher);
            var accept = listener.AcceptTcpClientAsync();
            var connect = adapter.ConnectAsync(new FixProfile("127.0.0.1", port, "PROBE", "NEXUSROUTER", 20), CancellationToken.None);

            using var server = await accept;
            var stream = server.GetStream();
            var scanner = new FixFrameScanner();
            var logon = Text(await ReadFrameAsync(stream, scanner));
            Assert.Contains("|35=A|49=PROBE|56=NEXUSROUTER|34=1|", logon);
            Assert.Contains("|108=20|", logon);

            var serverWriter = new FixSessionWriter("NEXUSROUTER", "PROBE", TimeProvider.System);
            await stream.WriteAsync(serverWriter.Logon(1, 20));
            await connect;
            Assert.Equal(ProbeState.Live, adapter.State);
            Assert.Equal("PROBE", adapter.Session!.SenderCompId);
            Assert.Equal("NEXUSROUTER", adapter.Session.TargetCompId);
            Assert.Equal(1, adapter.Session.MsgSeqNum);

            // Heartbeat from the router keeps the session alive without reaching the table.
            await stream.WriteAsync(serverWriter.Heartbeat(2));
            var exec = Encoding.ASCII.GetBytes("8=FIX.4.4\u00019=5\u000135=8\u000134=3\u000111=ORD1\u000110=000\u0001");
            await stream.WriteAsync(exec);
            await Task.Delay(300);
            Assert.Equal(1, adapter.Counters.Received);
            Assert.Equal("8", publisher.Events.Single().MsgType);
            Assert.Equal(3, adapter.Session.LastReceivedSeqNum);

            // TestRequest is answered with a Heartbeat carrying TestReqID and a rising MsgSeqNum.
            var testRequest = Encoding.ASCII.GetBytes("8=FIX.4.4\u00019=5\u000135=1\u000134=4\u0001112=T1\u000110=000\u0001");
            await stream.WriteAsync(testRequest);
            var heartbeat = Text(await ReadFrameAsync(stream, scanner));
            Assert.Contains("|35=0|", heartbeat);
            Assert.Contains("|112=T1|", heartbeat);
            Assert.Contains("|34=2|", heartbeat);

            await adapter.DisconnectAsync(CancellationToken.None);
            var logout = Text(await ReadFrameAsync(stream, scanner));
            Assert.Contains("|35=5|", logout);
            Assert.Contains("|34=3|", logout);
            Assert.Equal(ProbeState.Disconnected, adapter.State);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task TcpDrop_AfterLive_GoesFaultedWithLastError()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var adapter = Create(new Publisher());
            var accept = listener.AcceptTcpClientAsync();
            var connect = adapter.ConnectAsync(new FixProfile("127.0.0.1", port, "PROBE", "NEXUSROUTER", 20), CancellationToken.None);
            var server = await accept;
            var stream = server.GetStream();
            await ReadFrameAsync(stream, new FixFrameScanner());
            await stream.WriteAsync(new FixSessionWriter("NEXUSROUTER", "PROBE", TimeProvider.System).Logon(1, 20));
            await connect;
            Assert.Equal(ProbeState.Live, adapter.State);

            server.Close();
            for (var i = 0; i < 50 && adapter.State == ProbeState.Live; i++)
            {
                await Task.Delay(100);
            }

            Assert.Equal(ProbeState.Faulted, adapter.State);
            Assert.False(string.IsNullOrEmpty(adapter.LastError));
            await adapter.DisconnectAsync(CancellationToken.None);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ConnectRefused_GoesFaulted()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var adapter = Create(new Publisher());

        await adapter.ConnectAsync(new FixProfile("127.0.0.1", port, "PROBE", "NEXUSROUTER", 20), CancellationToken.None);

        Assert.Equal(ProbeState.Faulted, adapter.State);
        Assert.NotNull(adapter.LastError);
    }
}
