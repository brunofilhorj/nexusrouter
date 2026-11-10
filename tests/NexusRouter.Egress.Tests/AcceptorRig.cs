using System.Buffers;
using System.Diagnostics;
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
/// Acceptor FIX real sobre um <see cref="SessionLogManager"/> real, para os testes da retransmissão sobre o log (T-48).
/// </summary>
internal sealed class AcceptorRig : IAsyncDisposable
{
    internal const string DispatcherId = "fix-acceptor";

    private readonly FixOutboundDispatcher _dispatcher;
    private long _inSeq;
    private bool _stopped;

    private AcceptorRig(string directory, int port, SessionLogManager log, ISessionLogStream stream, FixOutboundDispatcher dispatcher)
    {
        Directory = directory;
        Port = port;
        Log = log;
        Stream = stream;
        _dispatcher = dispatcher;
    }

    internal string Directory { get; }

    internal int Port { get; }

    internal SessionLogManager Log { get; }

    internal ISessionLogStream Stream { get; }

    /// <summary>
    /// Sobe o log e o acceptor. Os clientes de <paramref name="knownClients"/> existem antes do primeiro Logon (offline).
    /// </summary>
    internal static async Task<AcceptorRig> StartAsync(
        string directory,
        AcceptorRecoveryOptions? recovery = null,
        DeliveryMode mode = DeliveryMode.PassThrough,
        IReadOnlyList<string>? knownClients = null,
        TimeSpan? slotTimeout = null,
        int uncleanMargin = 5)
    {
        var log = new SessionLogManager(
            new SessionLogOptions
            {
                Directory = directory,
                SegmentSizeMB = 1,
                IndexSlotsPerSegment = 100_000,
                FlushIntervalMs = 60_000,
                RetentionTradingDays = 5,
                UncleanRestartSeqMargin = uncleanMargin,
            },
            NullLogger<SessionLogManager>.Instance);
        await log.StartAsync(CancellationToken.None);
        ISessionLogStream stream = log.OpenStream("B3");
        foreach (string client in knownClients ?? [])
        {
            log.OpenClient(DispatcherId, client);
        }

        int port = FixWire.FreePort();
        var dispatcher = new FixOutboundDispatcher(
            DispatcherId,
            new FixOutboundDispatcherOptions
            {
                ListenAddress = "127.0.0.1",
                ListenPort = port,
                SenderCompId = "NEXUS",
                BeginString = "FIX.4.4",
                AllowedSenderCompIds = [],
                HeartBtInt = 30,
                LogonTimeout = TimeSpan.FromSeconds(2),
                SlotPublishTimeout = slotTimeout ?? TimeSpan.FromMilliseconds(30),
                Recovery = recovery ?? new AcceptorRecoveryOptions(),
            },
            NullLogger<FixOutboundDispatcher>.Instance,
            new DispatchPayloadPool(),
            new NoSecrets(),
            log,
            catchUp: null,
            resendDeliveryMode: _ => mode);
        await dispatcher.StartAsync(CancellationToken.None);
        return new AcceptorRig(directory, port, log, stream, dispatcher);
    }

    /// <summary>
    /// Grava um ExecutionReport no log de ingress sem entregar ao dispatcher.
    /// </summary>
    internal Appended Append(string clOrdId)
    {
        long inSeq = ++_inSeq;
        byte[] frame = FixWire.ExecutionReport(clOrdId, inSeq);
        AppendResult appended = Stream.Append(frame, inSeq, ingressTs: 1);
        Assert.True(appended.IsOk);
        return new Appended(frame, appended.Ref);
    }

    /// <summary>
    /// Entrega ao dispatcher uma mensagem já gravada, como o fan-out faz.
    /// </summary>
    internal void Dispatch(Appended appended, DeliveryMode mode = DeliveryMode.PassThrough)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(appended.Frame.Length);
        appended.Frame.CopyTo(rented.AsSpan(0, appended.Frame.Length));
        var payload = new DispatchPayload
        {
            DeliveryMode = mode,
            RawBuffer = new ArrayPoolMemoryOwner<byte>(rented, appended.Frame.Length),
            LogRef = appended.Ref,
        };
        Assert.True(_dispatcher.TryEnqueue(payload));
    }

    /// <summary>
    /// Grava e entrega cada mensagem e espera o índice dos clientes conhecidos publicar o último slot.
    /// </summary>
    internal async Task AppendAndDispatchAsync(params string[] clOrdIds)
    {
        ClientBinding[] known = KnownClients();
        long[] target = new long[known.Length];
        for (int i = 0; i < known.Length; i++)
        {
            target[i] = known[i].Index.NextOut + clOrdIds.Length - 1;
        }

        foreach (string clOrdId in clOrdIds)
        {
            Dispatch(Append(clOrdId));
        }

        for (int i = 0; i < known.Length; i++)
        {
            await WaitPublishedAsync(known[i].Index, target[i]);
        }
    }

    internal ClientBinding Client(string senderCompId) => Log.OpenClient(DispatcherId, senderCompId);

    internal static async Task WaitPublishedAsync(IClientIndex index, long seq)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (index.TryGet(seq, out _) == IndexReadResult.Published)
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"O slot {seq} não foi publicado.");
    }

    /// <summary>
    /// Para o acceptor e o log (marcador <c>clean</c> gravado).
    /// </summary>
    internal async Task StopAsync()
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        await _dispatcher.StopAsync(CancellationToken.None);
        await Log.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// Para como se o processo tivesse caído: remove o marcador <c>clean</c> que o shutdown limpo deixou.
    /// </summary>
    internal async Task StopAsUncleanAsync()
    {
        await StopAsync();
        File.Delete(Path.Combine(Directory, "clean"));
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private ClientBinding[] KnownClients()
    {
        var buffer = new ClientBinding[16];
        int count = Log.CopyClients(DispatcherId, buffer);
        return buffer[..count];
    }

    internal readonly record struct Appended(byte[] Frame, LogRef Ref);

    private sealed class NoSecrets : ISecretResolver
    {
        public string ResolveRequired(string key) =>
            throw new InvalidOperationException($"O resolvedor não deveria ser chamado (chave '{key}').");
    }
}

/// <summary>
/// Cliente FIX cru sobre TCP: lê frames inteiros e escreve mensagens montadas pelo teste.
/// </summary>
internal sealed class FixPeer : IDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly List<byte> _pending = [];

    private FixPeer(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
    }

    internal static async Task<FixPeer> ConnectAsync(
        AcceptorRig rig,
        string senderCompId,
        long msgSeqNum = 1,
        bool reset = false,
        long? nextExpected = null)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, rig.Port);
        var peer = new FixPeer(client);
        var options = new SessionOptions
        {
            SessionId = senderCompId,
            Host = "127.0.0.1",
            Port = 1,
            SenderCompId = senderCompId,
            TargetCompId = "NEXUS",
            BeginString = "FIX.4.4",
            HeartBtInt = 30,
            ResetSeqNumFlag = reset,
            NextExpectedMsgSeqNum = nextExpected,
        };
        byte[] buffer = new byte[512];
        int length = FixMessageBuilder.BuildLogon(buffer, options, msgSeqNum).Length;
        await peer.SendAsync(buffer[..length]);
        return peer;
    }

    internal ValueTask SendAsync(byte[] bytes) => _stream.WriteAsync(bytes);

    internal async Task<byte[]> ReadFrameAsync()
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

            int read = await _stream.ReadAsync(chunk, timeout.Token);
            if (read == 0)
            {
                throw new IOException("O socket fechou antes do frame.");
            }

            _pending.AddRange(chunk.AsSpan(0, read));
        }
    }

    /// <summary>
    /// Falha se chegar qualquer byte dentro da janela. O socket fechado também conta como quebra do silêncio.
    /// </summary>
    internal async Task ExpectSilenceAsync(TimeSpan window)
    {
        Assert.Empty(_pending);
        using var timeout = new CancellationTokenSource(window);
        var chunk = new byte[64];
        try
        {
            int read = await _stream.ReadAsync(chunk, timeout.Token);
            Assert.Fail(read == 0 ? "O socket fechou dentro da janela de silêncio." : $"Chegaram {read} bytes que o teste não esperava.");
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Espera o servidor fechar o socket e devolve quanto tempo isso levou.
    /// </summary>
    internal async Task<TimeSpan> WaitClosedAsync(TimeSpan limit)
    {
        var watch = Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource(limit);
        var chunk = new byte[256];
        try
        {
            while (await _stream.ReadAsync(chunk, timeout.Token) > 0)
            {
            }
        }
        catch (IOException)
        {
        }
        catch (OperationCanceledException)
        {
            Assert.Fail($"O servidor não fechou o socket em {limit.TotalSeconds:0.#} s.");
        }

        return watch.Elapsed;
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>
/// Montagem e leitura de frames FIX nos testes.
/// </summary>
internal static class FixWire
{
    private const char Soh = '\u0001';

    internal static byte[] ExecutionReport(string clOrdId, long seq) =>
        WithChecksum($"35=8{Soh}49=EXCH{Soh}56=CLIENT{Soh}34={seq}{Soh}52=20260101-00:00:00.000{Soh}11={clOrdId}{Soh}");

    internal static byte[] ResendRequest(string senderCompId, long msgSeqNum, long begin, long end) =>
        ClientMessage("2", senderCompId, msgSeqNum, $"7={begin}{Soh}16={end}{Soh}");

    internal static byte[] TestRequest(string senderCompId, long msgSeqNum, string testReqId, bool possDup = false) =>
        ClientMessage("1", senderCompId, msgSeqNum, $"112={testReqId}{Soh}", possDup);

    internal static byte[] Logout(string senderCompId, long msgSeqNum) =>
        ClientMessage("5", senderCompId, msgSeqNum, string.Empty);

    internal static byte[] ClientMessage(string msgType, string senderCompId, long msgSeqNum, string extra, bool possDup = false) =>
        WithChecksum(
            $"35={msgType}{Soh}49={senderCompId}{Soh}56=NEXUS{Soh}34={msgSeqNum}{Soh}"
            + (possDup ? $"43=Y{Soh}" : string.Empty)
            + $"52=20260101-00:00:00.000{Soh}{extra}");

    internal static byte[] WithChecksum(string body)
    {
        string withoutChecksum = $"8=FIX.4.4{Soh}9={body.Length}{Soh}" + body;
        int sum = 0;
        foreach (char ch in withoutChecksum)
        {
            sum += ch;
        }

        return Encoding.ASCII.GetBytes(withoutChecksum + "10=" + (sum % 256).ToString("000") + Soh);
    }

    internal static string Field(byte[] frame, int tag)
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

    internal static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    internal static string TempDirectory() =>
        Path.Combine(Path.GetTempPath(), "nexusrouter-retx-" + Guid.NewGuid().ToString("N"));

    internal static void DeleteDirectory(string directory)
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
