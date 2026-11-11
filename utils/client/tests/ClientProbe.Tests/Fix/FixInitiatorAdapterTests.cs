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
    public async Task Reconnect_ContinuesSequence_SendsNextExpected_AndResetRestartsAtOne()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var adapter = Create(new Publisher());
            var serverWriter = new FixSessionWriter("NEXUSROUTER", "PROBE", TimeProvider.System);

            // First connection: Logon 34=1 with 789=1; router answers seq 1, then exec seq 2.
            var accept = listener.AcceptTcpClientAsync();
            var connect = adapter.ConnectAsync(new FixProfile("127.0.0.1", port, "PROBE", "NEXUSROUTER", 20), CancellationToken.None);
            using (var server = await accept)
            {
                var stream = server.GetStream();
                var scanner = new FixFrameScanner();
                var logon = Text(await ReadFrameAsync(stream, scanner));
                Assert.Contains("|34=1|", logon);
                Assert.Contains("|789=1|", logon);
                await stream.WriteAsync(serverWriter.Logon(1, 20));
                await connect;
                await stream.WriteAsync(Encoding.ASCII.GetBytes("8=FIX.4.4\u00019=5\u000135=8\u000134=2\u000111=A\u000110=000\u0001"));
                for (var i = 0; i < 50 && adapter.Session!.NextExpectedSeqNum != 3; i++)
                {
                    await Task.Delay(50);
                }

                Assert.Equal(3, adapter.Session!.NextExpectedSeqNum);
                await adapter.DisconnectAsync(CancellationToken.None);
                Assert.Contains("|34=2|", Text(await ReadFrameAsync(stream, scanner)));
            }

            // Reconnect: Logon continues at 34=3 and asks for 789=3.
            accept = listener.AcceptTcpClientAsync();
            connect = adapter.ConnectAsync(new FixProfile("127.0.0.1", port, "PROBE", "NEXUSROUTER", 20), CancellationToken.None);
            using (var server = await accept)
            {
                var stream = server.GetStream();
                var scanner = new FixFrameScanner();
                var logon = Text(await ReadFrameAsync(stream, scanner));
                Assert.Contains("|34=3|", logon);
                Assert.Contains("|789=3|", logon);
                Assert.DoesNotContain("|141=Y|", logon);

                // Router Logon arrives at seq 5: probe waits for the 789 resend (3, 4), then a gap fill covers 5.
                await stream.WriteAsync(serverWriter.Logon(5, 20));
                await connect;
                Assert.Equal(3, adapter.Session!.GapFrom);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("8=FIX.4.4\u00019=5\u000135=8\u000134=3\u000143=Y\u000111=B\u000110=000\u0001"));
                await stream.WriteAsync(Encoding.ASCII.GetBytes("8=FIX.4.4\u00019=5\u000135=8\u000134=4\u000143=Y\u000111=C\u000110=000\u0001"));
                await stream.WriteAsync(serverWriter.GapFill(5, 6));
                for (var i = 0; i < 50 && adapter.Session!.NextExpectedSeqNum != 6; i++)
                {
                    await Task.Delay(50);
                }

                Assert.Equal(6, adapter.Session!.NextExpectedSeqNum);
                Assert.Equal(0, adapter.Session.GapFrom);
                await adapter.DisconnectAsync(CancellationToken.None);
            }

            // Reset: 141=Y with 34=1 and 789=1.
            accept = listener.AcceptTcpClientAsync();
            connect = adapter.ConnectAsync(
                new FixProfile("127.0.0.1", port, "PROBE", "NEXUSROUTER", 20, ResetSeqNum: true),
                CancellationToken.None);
            using (var server = await accept)
            {
                var stream = server.GetStream();
                var logon = Text(await ReadFrameAsync(stream, new FixFrameScanner()));
                Assert.Contains("|34=1|", logon);
                Assert.Contains("|141=Y|", logon);
                Assert.Contains("|789=1|", logon);
                await stream.WriteAsync(serverWriter.Logon(1, 20, resetSeqNum: true, nextExpectedMsgSeqNum: null));
                await connect;
                Assert.Equal(2, adapter.Session!.NextExpectedSeqNum);
                await adapter.DisconnectAsync(CancellationToken.None);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Gap_WithoutNextExpected_SendsResendRequest_AndAnswersResendWithGapFill()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var adapter = Create(new Publisher());
            var accept = listener.AcceptTcpClientAsync();
            var connect = adapter.ConnectAsync(
                new FixProfile("127.0.0.1", port, "PROBE", "NEXUSROUTER", 20, SendNextExpected: false),
                CancellationToken.None);
            using var server = await accept;
            var stream = server.GetStream();
            var scanner = new FixFrameScanner();
            Assert.DoesNotContain("|789=", Text(await ReadFrameAsync(stream, scanner)));

            // Router Logon at seq 4 while the probe expects 1: probe asks 7=1, 16=0.
            await stream.WriteAsync(new FixSessionWriter("NEXUSROUTER", "PROBE", TimeProvider.System).Logon(4, 20));
            await connect;
            var resend = Text(await ReadFrameAsync(stream, scanner));
            Assert.Contains("|35=2|", resend);
            Assert.Contains("|7=1|", resend);
            Assert.Contains("|16=0|", resend);

            // Router asks the probe to resend 1..0: one GapFill to the next outgoing seq (3).
            await stream.WriteAsync(Encoding.ASCII.GetBytes("8=FIX.4.4\u00019=5\u000135=2\u000134=5\u00017=1\u000116=0\u000110=000\u0001"));
            var gapFill = Text(await ReadFrameAsync(stream, scanner));
            Assert.Contains("|35=4|", gapFill);
            Assert.Contains("|34=1|", gapFill);
            Assert.Contains("|123=Y|", gapFill);
            Assert.Contains("|36=3|", gapFill);
            await adapter.DisconnectAsync(CancellationToken.None);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void SeqStore_PersistsAcrossInstances()
    {
        var directory = Path.Combine(Path.GetTempPath(), "probe-seq-" + Guid.NewGuid().ToString("N"));
        try
        {
            var key = FixSeqStore.Key("PROBE", "NEXUSROUTER");
            var first = new FixSeqStore(directory);
            first.Set(key, new FixSeqState(7, 12));
            first.Flush(key);

            Assert.Equal(new FixSeqState(7, 12), new FixSeqStore(directory).Get(key));
            Assert.Equal(FixSeqState.Initial, new FixSeqStore(null).Get(key));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
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
