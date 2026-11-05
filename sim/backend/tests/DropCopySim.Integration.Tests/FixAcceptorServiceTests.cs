using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using DropCopySim.Core.Acceptor;
using DropCopySim.Core.Generation;
using DropCopySim.Core.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DropCopySim.Integration.Tests;

public sealed class FixAcceptorServiceTests
{
    [Fact]
    public async Task StartAsync_ReconnectsAfterActiveSessionIsDropped()
    {
        int port = GetAvailablePort();
        var service = new FixAcceptorService(
            Options.Create(new AcceptorOptions
            {
                ListenAddress = IPAddress.Loopback.ToString(),
                ListenPort = port,
            }),
            NullLogger<FixAcceptorService>.Instance,
            NullLogger<AcceptorSession>.Instance,
            CreateGenerator());

        await service.StartAsync(CancellationToken.None);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            await WaitForConditionAsync(() => service.ActiveSession is not null);
            Assert.NotNull(service.ActiveSession);
            await service.ActiveSession.DisposeAsync();

            await WaitForConditionAsync(() => service.ActiveSession is null);

            using var reconnect = new TcpClient();
            await reconnect.ConnectAsync(IPAddress.Loopback, port);
            await WaitForConditionAsync(() => service.ActiveSession is not null);
            Assert.NotNull(service.ActiveSession);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StartAsync_RejectsEnabledTlsInsteadOfAcceptingPlaintext()
    {
        var service = new FixAcceptorService(
            Options.Create(new AcceptorOptions
            {
                Tls = new TlsOptions
                {
                    Enabled = true,
                    CertificatePath = "server.pfx",
                },
            }),
            NullLogger<FixAcceptorService>.Instance,
            NullLogger<AcceptorSession>.Instance,
            CreateGenerator());

        await Assert.ThrowsAsync<NotSupportedException>(
            () => service.StartAsync(CancellationToken.None));

        await service.StopAsync(CancellationToken.None);
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

    private static async Task WaitForConditionAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }
}
