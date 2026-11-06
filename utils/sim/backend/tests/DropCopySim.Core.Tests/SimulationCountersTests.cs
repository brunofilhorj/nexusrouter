using DropCopySim.Core.Telemetry;

namespace DropCopySim.Core.Tests;

public class SimulationCountersTests
{
    [Fact]
    public void StartRound_ResetsCounters_AndTracksElapsed()
    {
        var counters = new SimulationCounters();
        var started = DateTimeOffset.Parse("2026-11-04T12:00:00Z");

        counters.StartRound(started);
        counters.IncrementTotalSent();
        counters.IncrementTotalSent();
        counters.IncrementExecType('F');
        counters.IncrementErrors();

        Assert.Equal(2, counters.TotalSent);
        Assert.Equal(1, counters.Errors);
        Assert.Equal(started, counters.RoundStartedUtc);
        Assert.Equal(1000, counters.ElapsedMs(started.AddMilliseconds(1000)));
        Assert.Equal(1, counters.SnapshotByExecType()["F"]);
    }

    [Fact]
    public void Reset_ClearsRound()
    {
        var counters = new SimulationCounters();
        counters.StartRound(DateTimeOffset.UtcNow);
        counters.IncrementTotalSent();

        counters.Reset();

        Assert.Equal(0, counters.TotalSent);
        Assert.Null(counters.RoundStartedUtc);
        Assert.Empty(counters.SnapshotByExecType());
    }
}
