using System.Diagnostics;
using ClientProbe.Probe;
using ClientProbe.Telemetry;
using Microsoft.Extensions.Options;

namespace ClientProbe.Tests.Telemetry;

public sealed class TelemetryHubTests
{
    private static TelemetryHub CreateHub(int capacity, int sampleAbove)
    {
        var options = new ClientProbeOptions();
        options.Telemetry.ChannelCapacity = capacity;
        options.Telemetry.SampleAbovePerSecond = sampleAbove;
        return new TelemetryHub(Options.Create(options), TimeProvider.System);
    }

    private static ProbeEvent Event(int i) =>
        ShortFieldExtractor.Extract(ProbeTransport.Kafka, "8=FIX.4.4\u000135=8\u000111=ORD1\u0001"u8, DateTimeOffset.UtcNow, 240);

    [Fact]
    public void TryPublish_ChannelFullAndNoReader_DropsWithoutBlocking()
    {
        var hub = CreateHub(capacity: 10, sampleAbove: 50);
        var counters = new ProbeCounters();

        var stopwatch = Stopwatch.StartNew();
        var accepted = 0;
        for (var i = 0; i < 10_000; i++)
        {
            if (hub.TryPublish(Event(i), counters))
            {
                accepted++;
            }
        }

        stopwatch.Stop();
        Assert.Equal(10, accepted);
        Assert.Equal(9_990, counters.Dropped);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Hub_RateAboveThreshold_SamplesAndCountsSkippedEvents()
    {
        var hub = CreateHub(capacity: 4096, sampleAbove: 10);
        var counters = new ProbeCounters();
        using var subscription = hub.Subscribe();
        await hub.StartAsync(CancellationToken.None);

        const int total = 200;
        for (var i = 0; i < total; i++)
        {
            Assert.True(hub.TryPublish(Event(i), counters));
        }

        // Wait for the reader to drain: forwarded + sampled must reach the number written.
        var forwarded = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (forwarded + counters.Sampled < total)
        {
            while (subscription.Reader.TryRead(out _))
            {
                forwarded++;
            }

            await Task.Delay(10, timeout.Token);
        }

        await hub.StopAsync(CancellationToken.None);
        Assert.True(counters.Sampled > 0);
        Assert.True(forwarded < total);
        Assert.Equal(total, forwarded + counters.Sampled);
        Assert.Equal(0, counters.Dropped);
    }
}
