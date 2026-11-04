using System.Collections.Concurrent;

namespace DropCopySim.Core.Telemetry;

/// <summary>
/// Thread-safe counters for the active simulation round.
/// All mutations go through <see cref="Interlocked"/> / concurrent collections
/// so the generator hot path never takes a lock.
/// </summary>
public sealed class SimulationCounters
{
    private long _totalSent;
    private long _errors;
    private long _roundStartedUtcTicks;

    private readonly ConcurrentDictionary<char, long> _byExecType = new();

    /// <summary>Total application messages successfully handed to the session writer.</summary>
    public long TotalSent => Interlocked.Read(ref _totalSent);

    /// <summary>Total generation or send failures in the current round.</summary>
    public long Errors => Interlocked.Read(ref _errors);

    /// <summary>
    /// UTC start of the current round, or <c>null</c> if the round has not started.
    /// </summary>
    public DateTimeOffset? RoundStartedUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _roundStartedUtcTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>Marks the beginning of a new round and resets all counters.</summary>
    public void StartRound(DateTimeOffset startedUtc)
    {
        Interlocked.Exchange(ref _totalSent, 0);
        Interlocked.Exchange(ref _errors, 0);
        Interlocked.Exchange(ref _roundStartedUtcTicks, startedUtc.UtcTicks);
        _byExecType.Clear();
    }

    /// <summary>Clears counters when the round returns to Idle.</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _totalSent, 0);
        Interlocked.Exchange(ref _errors, 0);
        Interlocked.Exchange(ref _roundStartedUtcTicks, 0);
        _byExecType.Clear();
    }

    /// <summary>Increments the total-sent counter after a successful send.</summary>
    public long IncrementTotalSent() => Interlocked.Increment(ref _totalSent);

    /// <summary>Increments the error counter.</summary>
    public long IncrementErrors() => Interlocked.Increment(ref _errors);

    /// <summary>Increments the per-ExecType counter for the given FIX tag 150 value.</summary>
    public long IncrementExecType(char execType) =>
        _byExecType.AddOrUpdate(execType, 1, static (_, current) => current + 1);

    /// <summary>
    /// Returns a stable snapshot of per-ExecType counts keyed by the ExecType character as a string.
    /// </summary>
    public IReadOnlyDictionary<string, long> SnapshotByExecType()
    {
        var snapshot = new Dictionary<string, long>(_byExecType.Count);
        foreach (var pair in _byExecType)
        {
            snapshot[pair.Key.ToString()] = pair.Value;
        }

        return snapshot;
    }

    /// <summary>
    /// Elapsed milliseconds since <see cref="StartRound"/>, or <c>0</c> if no round is active.
    /// </summary>
    public long ElapsedMs(DateTimeOffset nowUtc)
    {
        var started = RoundStartedUtc;
        if (started is null)
        {
            return 0;
        }

        var elapsed = nowUtc - started.Value;
        return elapsed < TimeSpan.Zero ? 0 : (long)elapsed.TotalMilliseconds;
    }
}
