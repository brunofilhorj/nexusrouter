namespace ClientProbe.Probe;

/// <summary>
/// Lock-free counters for one adapter. The rate is derived from <see cref="Received"/> on each
/// <see cref="GetRate"/> call and is not stored by the adapter.
/// </summary>
public sealed class ProbeCounters
{
    private const long MinRateWindowTicks = TimeSpan.TicksPerMillisecond * 500;

    private long _received;
    private long _parseFailures;
    private long _sampled;
    private long _dropped;
    private long _lastReceivedTicks;
    private readonly Lock _rateGate = new();
    private long _rateReceived;
    private long _rateTicks;
    private double _rate;

    /// <summary>Frames that arrived from the broker or session, before sampling.</summary>
    public long Received => Interlocked.Read(ref _received);

    /// <summary>Frames without a tag 35.</summary>
    public long ParseFailures => Interlocked.Read(ref _parseFailures);

    /// <summary>Events skipped by the hub because the rate was above the sampling threshold.</summary>
    public long Sampled => Interlocked.Read(ref _sampled);

    /// <summary>Events discarded because the telemetry channel was full.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Timestamp of the last received frame, or null when none arrived.</summary>
    public DateTimeOffset? LastReceivedAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastReceivedTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>Counts one received frame.</summary>
    /// <param name="at">Receive time.</param>
    public void RecordReceived(DateTimeOffset at)
    {
        Interlocked.Increment(ref _received);
        Interlocked.Exchange(ref _lastReceivedTicks, at.UtcTicks);
    }

    /// <summary>Counts one frame that failed to parse.</summary>
    public void RecordParseFailure() => Interlocked.Increment(ref _parseFailures);

    /// <summary>Counts one event skipped by sampling.</summary>
    public void RecordSampled() => Interlocked.Increment(ref _sampled);

    /// <summary>Counts one event discarded by a full channel.</summary>
    public void RecordDropped() => Interlocked.Increment(ref _dropped);

    /// <summary>Clears every counter. Used when a new connection replaces the previous one.</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _received, 0);
        Interlocked.Exchange(ref _parseFailures, 0);
        Interlocked.Exchange(ref _sampled, 0);
        Interlocked.Exchange(ref _dropped, 0);
        Interlocked.Exchange(ref _lastReceivedTicks, 0);
        lock (_rateGate)
        {
            _rateReceived = 0;
            _rateTicks = 0;
            _rate = 0;
        }
    }

    /// <summary>Returns messages per second since the previous call. Calls closer than 500 ms reuse the last value.</summary>
    /// <param name="now">Current time.</param>
    /// <returns>Messages per second.</returns>
    public double GetRate(DateTimeOffset now)
    {
        lock (_rateGate)
        {
            var received = Received;
            if (_rateTicks == 0 || received < _rateReceived)
            {
                _rateTicks = now.UtcTicks;
                _rateReceived = received;
                _rate = 0;
                return 0;
            }

            var elapsed = now.UtcTicks - _rateTicks;
            if (elapsed < MinRateWindowTicks)
            {
                return _rate;
            }

            _rate = (received - _rateReceived) / (elapsed / (double)TimeSpan.TicksPerSecond);
            _rateReceived = received;
            _rateTicks = now.UtcTicks;
            return _rate;
        }
    }
}
