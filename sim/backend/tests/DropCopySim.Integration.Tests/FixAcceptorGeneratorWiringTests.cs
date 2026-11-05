using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using DropCopySim.Core.Acceptor;
using DropCopySim.Core.Fix;
using DropCopySim.Core.Generation;
using DropCopySim.Core.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DropCopySim.Integration.Tests;

public sealed class FixAcceptorGeneratorWiringTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task AcceptedSession_BecomesActive_GeneratorSendsExecutionReportToClient()
    {
        int port = GetAvailablePort();
        var options = new AcceptorOptions
        {
            ListenAddress = IPAddress.Loopback.ToString(),
            ListenPort = port,
        };

        await using var generator = CreateGenerator();
        var service = new FixAcceptorService(
            Options.Create(options),
            NullLogger<FixAcceptorService>.Instance,
            NullLogger<AcceptorSession>.Instance,
            generator);

        await service.StartAsync(CancellationToken.None);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            NetworkStream stream = client.GetStream();

            byte[] logon = BuildLogon(options);
            await stream.WriteAsync(logon);
            await stream.FlushAsync();

            await WaitUntilAsync(
                () => service.ActiveSession?.Fsm.State == AcceptorSessionState.Active,
                WaitTimeout);

            generator.Start();
            await WaitUntilAsync(() => generator.Counters.TotalSent > 0, WaitTimeout);

            bool sawExecutionReport = await ReadUntilExecutionReportAsync(client, WaitTimeout);
            Assert.True(sawExecutionReport, "No ExecutionReport (35=8) frame was received by the client.");
            Assert.True(generator.Counters.TotalSent > 0);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static byte[] BuildLogon(AcceptorOptions options)
    {
        byte[] buffer = new byte[512];
        var writer = new B3FixMessageWriter(buffer);
        writer.WriteB3Header(
            msgType: "A",
            senderCompId: options.TargetCompId,
            targetCompId: options.SenderCompId,
            msgSeqNum: 1,
            sendingTime: DateTime.UtcNow,
            businessSeqNum: 1);
        writer.WriteField(FixTags.HeartBtInt, 30);
        writer.WriteField(FixTags.ResetSeqNumFlag, "Y");
        int length = writer.Finish();
        return buffer[..length];
    }

    private static async Task<bool> ReadUntilExecutionReportAsync(TcpClient client, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        NetworkStream stream = client.GetStream();
        var buffer = new byte[256 * 1024];
        var total = 0;
        while (total < buffer.Length)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer.AsMemory(total), deadline.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (read == 0)
            {
                break;
            }

            total += read;

            ReadOnlySequence<byte> remaining = new(buffer.AsMemory(0, total));
            while (Fix44FrameScanner.TryDecode(ref remaining, out ReadOnlySequence<byte> frame))
            {
                string message = Encoding.ASCII.GetString(frame.ToArray());
                if (string.Equals(Field(message, FixTags.MsgType), "8", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string? Field(string message, int tag)
    {
        var prefix = tag.ToString() + "=";
        foreach (var field in message.Split('\u0001'))
        {
            if (field.StartsWith(prefix, StringComparison.Ordinal))
            {
                return field[prefix.Length..];
            }
        }

        return null;
    }

    private static OrderFlowGenerator CreateGenerator() => new(
        TestSimulationConfig(),
        new SimulationCounters(),
        Channel.CreateUnbounded<TelemetryEvent>().Writer,
        NullLogger<OrderFlowGenerator>.Instance);

    private static SimulationConfig TestSimulationConfig() => new()
    {
        TotalOrders = 10,
        RatePerSecond = 100,
        Accounts = ["ACC1"],
        Instruments =
        [
            new Instrument
            {
                SecurityId = 200001,
                Symbol = "PETR4",
                SecurityExchange = "BVMF",
            },
        ],
        Outcome = new OutcomeMix { FillPct = 100 },
        EnabledMessageTypes = ["8"],
    };

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > timeout)
            {
                throw new TimeoutException("Condition was not reached before the timeout.");
            }

            await Task.Delay(10);
        }
    }
}
