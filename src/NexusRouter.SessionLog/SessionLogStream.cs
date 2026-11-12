using System.Buffers;

using Microsoft.Extensions.Logging;

using NexusRouter.Domain.Abstractions;
using NexusRouter.Domain.Models;

namespace NexusRouter.SessionLog;

/// <summary>
/// Single writer of one ingress stream. The next segment is prepared off the hot path.
/// </summary>
public sealed class SessionLogStream : ISessionLogStream, IDisposable
{
    private readonly string _root;
    private readonly string _sessionKey;
    private readonly long _capacity;
    private readonly int _slots;
    private readonly ILogger _logger;
    private readonly SessionLogMetrics _metrics;
    private readonly List<LogSegment> _segments = [];
    private LogSegment[] _visible = [];
    private readonly object _rotateGate = new();
    private LogSegment? _current;
    private LogSegment? _prepared;
    private int _tradingDay;
    private int _segmentDay;
    private int _epoch;
    private int _forceNew;
    private long _committed;
    private long _syncRotations;
    private int _degraded;
    private int _failNextIo;
    private int _disposed;

    /// <summary>
    /// Opens a stream and recovers every segment already on disk.
    /// </summary>
    /// <param name="root">Session-log root.</param>
    /// <param name="sessionKey">Folder name of the ingress session.</param>
    /// <param name="streamId">Stable id stored in <see cref="LogRef"/>.</param>
    /// <param name="capacity">Segment size in bytes.</param>
    /// <param name="slots">Index slots per segment.</param>
    /// <param name="logger">Logs the transition to degraded.</param>
    /// <param name="metrics">Failure and rotation counters.</param>
    public SessionLogStream(
        string root,
        string sessionKey,
        ushort streamId,
        long capacity,
        int slots,
        int tradingDay,
        ILogger logger,
        SessionLogMetrics metrics)
    {
        _root = root;
        _sessionKey = sessionKey;
        _capacity = capacity;
        _slots = slots;
        _logger = logger;
        _metrics = metrics;
        StreamId = streamId;
        _tradingDay = tradingDay;
        RecoverExisting();
        if (_current is null)
        {
            _current = CreateSegment(_tradingDay, epoch: 0, number: 0, touch: true);
            _segmentDay = _tradingDay;
            _segments.Add(_current);
            PublishVisible();
        }
    }

    /// <inheritdoc />
    public ushort StreamId { get; }

    /// <inheritdoc />
    public int Epoch => Volatile.Read(ref _epoch);

    /// <inheritdoc />
    public long Committed => Volatile.Read(ref _committed);

    /// <inheritdoc />
    public long SyncRotationCount => Volatile.Read(ref _syncRotations);

    /// <inheritdoc />
    public bool IsDegraded => Volatile.Read(ref _degraded) != 0;

    /// <summary>
    /// Makes the next append throw <see cref="IOException"/> once, so tests can observe a failed append.
    /// </summary>
    internal void ArmIoFailure() => Volatile.Write(ref _failNextIo, 1);

    /// <inheritdoc />
    public AppendResult Append(ReadOnlySpan<byte> frame, long inSeq, long ingressTs)
    {
        if (Volatile.Read(ref _degraded) != 0)
        {
            return AppendResult.Failed;
        }

        try
        {
            if (inSeq <= 0)
            {
                return AppendResult.Failed;
            }

            if (Interlocked.Exchange(ref _failNextIo, 0) != 0)
            {
                throw new IOException("Simulated session log I/O failure.");
            }

            EnsureWritable(inSeq, frame.Length);
            LogSegment current = _current ?? throw new IOException("The session log has no segment.");
            if (!current.TryAppend(frame, inSeq, ingressTs, out _))
            {
                return AppendResult.Failed;
            }

            Volatile.Write(ref _committed, inSeq);
            return AppendResult.Ok(new LogRef(StreamId, Volatile.Read(ref _epoch), inSeq));
        }
        catch (Exception ex)
        {
            MarkDegraded(ex);
            return AppendResult.Failed;
        }
    }

    /// <inheritdoc />
    public void BeginEpoch(int epoch, long baseSeq)
    {
        if (epoch <= Volatile.Read(ref _epoch))
        {
            throw new ArgumentOutOfRangeException(nameof(epoch), epoch, "The new epoch must be greater than the current one.");
        }

        Volatile.Write(ref _epoch, epoch);
        Volatile.Write(ref _committed, 0);
        Volatile.Write(ref _forceNew, 1);
        _ = baseSeq;
    }

    /// <summary>
    /// Copies one committed record of <paramref name="epoch"/>.
    /// </summary>
    /// <param name="epoch">Epoch to read.</param>
    /// <param name="inSeq">Ingress sequence.</param>
    /// <param name="destination">Receives the frame.</param>
    /// <returns>False when the sequence is not committed in that epoch.</returns>
    public bool TryRead(int epoch, long inSeq, IBufferWriter<byte> destination)
    {
        if (inSeq <= 0 || inSeq > GetCommitted(epoch))
        {
            return false;
        }

        LogSegment[] segments = Volatile.Read(ref _visible);
        for (int i = segments.Length - 1; i >= 0; i--)
        {
            LogSegment segment = segments[i];
            if (segment.Epoch == epoch && segment.TryRead(inSeq, destination))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Copies the record a client-index slot points at.
    /// </summary>
    /// <param name="segment">Segment number stored in the slot.</param>
    /// <param name="offset">Byte offset stored in the slot.</param>
    /// <param name="destination">Receives the frame.</param>
    /// <returns>False when no visible segment holds a valid record at that offset.</returns>
    /// <remarks>
    /// The slot has no epoch or day. The newest segment with that number is tried first and the CRC rejects a wrong match.
    /// </remarks>
    public bool TryReadAt(uint segment, ulong offset, IBufferWriter<byte> destination)
    {
        LogSegment[] segments = Volatile.Read(ref _visible);
        for (int i = segments.Length - 1; i >= 0; i--)
        {
            LogSegment candidate = segments[i];
            if ((uint)candidate.Number == segment && candidate.TryReadAt((long)offset, destination))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Last committed sequence of one epoch.
    /// </summary>
    /// <param name="epoch">Epoch to query.</param>
    /// <returns>Zero when the epoch has no record.</returns>
    public long GetCommitted(int epoch)
    {
        if (epoch == Volatile.Read(ref _epoch))
        {
            return Committed;
        }

        long max = 0;
        LogSegment[] segments = Volatile.Read(ref _visible);
        for (int i = 0; i < segments.Length; i++)
        {
            if (segments[i].Epoch == epoch && segments[i].LastSeq > max)
            {
                max = segments[i].LastSeq;
            }
        }

        return max;
    }

    /// <summary>
    /// Resolves a reference this stream owns.
    /// </summary>
    /// <param name="logRef">Ingress reference.</param>
    /// <returns>The segment and offset, or null when it is not this stream or not visible yet.</returns>
    internal LogPosition? Locate(LogRef logRef)
    {
        if (logRef.Stream != StreamId || logRef.InSeq <= 0 || logRef.InSeq > GetCommitted(logRef.Epoch))
        {
            return null;
        }

        LogSegment[] segments = Volatile.Read(ref _visible);
        for (int i = segments.Length - 1; i >= 0; i--)
        {
            LogSegment segment = segments[i];
            if (segment.Epoch == logRef.Epoch && segment.TryLocate(logRef.InSeq, out long offset))
            {
                return new LogPosition((uint)segment.Number, (ulong)offset);
            }
        }

        return null;
    }

    /// <summary>
    /// Updates the requested trading day and pre-allocates the next segment off the writer thread.
    /// </summary>
    /// <param name="tradingDay">Trading day as <c>yyyyMMdd</c>.</param>
    public void Maintain(int tradingDay)
    {
        Volatile.Write(ref _tradingDay, tradingDay);
        PrepareNext();
    }

    /// <summary>
    /// Releases every mapping.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (LogSegment segment in _segments)
        {
            segment.Dispose();
        }

        LogSegment? prepared = Interlocked.Exchange(ref _prepared, null);
        if (prepared is not null && !_segments.Contains(prepared))
        {
            prepared.Dispose();
        }
    }

    private void EnsureWritable(long seq, int payloadLength)
    {
        int day = Volatile.Read(ref _tradingDay);
        bool newEpoch = Interlocked.Exchange(ref _forceNew, 0) == 1;
        bool dayChanged = _current is not null && day != _segmentDay;
        if (dayChanged && !newEpoch)
        {
            int epoch = Volatile.Read(ref _epoch) + 1;
            Volatile.Write(ref _epoch, epoch);
            Volatile.Write(ref _committed, 0);
            newEpoch = true;
        }

        bool missing = _current is null;
        bool full = _current is not null && !_current.Fits(payloadLength);
        if (missing || newEpoch || dayChanged || full)
        {
            Rotate(day, seq, payloadLength);
        }
    }

    private void Rotate(int day, long seq, int payloadLength)
    {
        lock (_rotateGate)
        {
            LogSegment? prepared = Interlocked.Exchange(ref _prepared, null);
            bool usable = prepared is not null
                && prepared.Day == day
                && prepared.Epoch == Volatile.Read(ref _epoch)
                && prepared.Fits(payloadLength);
            LogSegment segment;
            if (usable)
            {
                segment = prepared!;
            }
            else
            {
                prepared?.Dispose();
                int epoch = Volatile.Read(ref _epoch);
                segment = CreateSegment(day, epoch, NextNumber(day, epoch), touch: true);
                Volatile.Write(ref _syncRotations, Volatile.Read(ref _syncRotations) + 1);
                _metrics.SyncRotation.Add(1);
            }

            _current = segment;
            _segmentDay = day;
            _segments.Add(segment);
            PublishVisible();
        }

        _ = seq;
    }

    private void PrepareNext()
    {
        LogSegment? current = _current;
        if (current is null)
        {
            return;
        }

        int day = Volatile.Read(ref _tradingDay);
        bool dayChanged = day != current.Day;
        int epoch = dayChanged ? current.Epoch + 1 : Volatile.Read(ref _epoch);
        int number = dayChanged ? NextNumber(day, epoch) : current.Number + 1;
        int currentNumber = current.Number;
        int currentDay = current.Day;
        int currentEpoch = current.Epoch;
        LogSegment? existing = Volatile.Read(ref _prepared);
        if (existing is not null)
        {
            if (existing.Day == day && existing.Epoch == epoch)
            {
                return;
            }

            existing = Interlocked.Exchange(ref _prepared, null);
            existing?.Dispose();
        }

        LogSegment segment = CreateSegment(day, epoch, number, touch: true);
        lock (_rotateGate)
        {
            if (_prepared is null
                && _current is not null
                && _current.Number == currentNumber
                && _current.Day == currentDay
                && _current.Epoch == currentEpoch)
            {
                Volatile.Write(ref _prepared, segment);
                return;
            }
        }

        segment.Dispose();
    }

    private int NextNumber(int day, int epoch)
    {
        int max = -1;
        foreach (LogSegment segment in _segments)
        {
            if (segment.Day == day && segment.Epoch == epoch && segment.Number > max)
            {
                max = segment.Number;
            }
        }

        return max + 1;
    }

    private LogSegment CreateSegment(int day, int epoch, int number, bool touch)
    {
        string directory = DayDirectory(day, epoch);
        return LogSegment.Create(directory, number, _capacity, _slots, day, epoch, touch);
    }

    private void RecoverExisting()
    {
        string streamDirectory = SessionLogPaths.Under(_root, "streams", _sessionKey);
        if (!Directory.Exists(streamDirectory))
        {
            return;
        }

        var found = new List<(int Day, int Epoch, int Number, string Path)>();
        foreach (string directory in Directory.GetDirectories(streamDirectory))
        {
            string name = Path.GetFileName(directory);
            if (!SessionLogPaths.TryParseTradingDay(name, out DateOnly day))
            {
                continue;
            }

            int epoch = ReadEpoch(name);
            foreach (string log in Directory.GetFiles(directory, "*.log"))
            {
                string stem = Path.GetFileNameWithoutExtension(log);
                if (int.TryParse(stem, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int number))
                {
                    found.Add((day.Year * 10000 + day.Month * 100 + day.Day, epoch, number, log));
                }
            }
        }

        found.Sort((left, right) =>
        {
            int byDay = left.Day.CompareTo(right.Day);
            if (byDay != 0)
            {
                return byDay;
            }

            int byEpoch = left.Epoch.CompareTo(right.Epoch);
            return byEpoch != 0 ? byEpoch : left.Number.CompareTo(right.Number);
        });

        foreach ((int day, int epoch, int number, string path) in found)
        {
            LogSegment segment = LogSegment.Recover(path, _capacity, _slots, day, epoch, number);
            _segments.Add(segment);
            _current = segment;
            _segmentDay = day;
            Volatile.Write(ref _epoch, epoch);
        }

        if (_current is not null)
        {
            long committed = 0;
            int epoch = Volatile.Read(ref _epoch);
            foreach (LogSegment segment in _segments)
            {
                if (segment.Epoch == epoch && segment.LastSeq > committed)
                {
                    committed = segment.LastSeq;
                }
            }

            Volatile.Write(ref _committed, committed);
            PublishVisible();
        }
    }

    private void PublishVisible() => Volatile.Write(ref _visible, _segments.ToArray());

    private string DayDirectory(int day, int epoch) =>
        SessionLogPaths.Under(
            _root,
            "streams",
            _sessionKey,
            day.ToString("D8", System.Globalization.CultureInfo.InvariantCulture) + "-e" + epoch.ToString("D3", System.Globalization.CultureInfo.InvariantCulture));

    private void MarkDegraded(Exception exception)
    {
        if (Interlocked.Exchange(ref _degraded, 1) == 0)
        {
            _logger.LogError(exception, "Session log stream {SessionKey} is degraded. Routing continues.", _sessionKey);
        }

        _metrics.AppendFailed.Add(1);
    }

    private static int ReadEpoch(string folderName)
    {
        int marker = folderName.IndexOf("-e", StringComparison.Ordinal);
        if (marker < 0 || marker + 2 >= folderName.Length)
        {
            return 0;
        }

        return int.TryParse(folderName[(marker + 2)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int epoch)
            ? epoch
            : 0;
    }

}
