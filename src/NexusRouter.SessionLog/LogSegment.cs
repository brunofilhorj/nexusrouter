using System.Buffers;
using System.Runtime.CompilerServices;

namespace NexusRouter.SessionLog;

/// <summary>
/// One pre-allocated <c>.log</c> and its dense <c>.index</c>.
/// </summary>
internal sealed unsafe class LogSegment : IDisposable
{
    /// <summary>
    /// Bytes before the payload: length, sequence, timestamp, CRC.
    /// </summary>
    public const int HeaderSize = 24;

    private const int SeqOffset = 4;
    private const int TsOffset = 12;
    private const int CrcOffset = 20;

    private readonly MappedFile _log;
    private readonly MappedFile _index;
    private readonly int _slots;
    private long _writeOffset;
    private long _baseSeq;
    private long _lastSeq;
    private int _disposed;

    private LogSegment(MappedFile log, MappedFile index, int slots, int day, int epoch, int number)
    {
        _log = log;
        _index = index;
        _slots = slots;
        Day = day;
        Epoch = epoch;
        Number = number;
    }

    /// <summary>
    /// Trading day encoded as <c>yyyyMMdd</c>.
    /// </summary>
    public int Day { get; }

    /// <summary>
    /// Epoch of this file.
    /// </summary>
    public int Epoch { get; }

    /// <summary>
    /// Segment number inside the day and epoch.
    /// </summary>
    public int Number { get; }

    /// <summary>
    /// First sequence written in this segment. Zero until the first record.
    /// </summary>
    public long BaseSeq => Volatile.Read(ref _baseSeq);

    /// <summary>
    /// Last valid sequence in this segment. Zero when empty.
    /// </summary>
    public long LastSeq => Volatile.Read(ref _lastSeq);

    /// <summary>
    /// Creates a zeroed segment and faults its pages.
    /// </summary>
    /// <param name="directory">Day and epoch directory.</param>
    /// <param name="number">Segment number.</param>
    /// <param name="capacity">Log size in bytes.</param>
    /// <param name="slots">Index slots.</param>
    /// <param name="day">Trading day as <c>yyyyMMdd</c>.</param>
    /// <param name="epoch">Epoch.</param>
    /// <param name="touch">When true, pages are faulted before return.</param>
    /// <returns>An empty segment.</returns>
    public static LogSegment Create(string directory, int number, long capacity, int slots, int day, int epoch, bool touch)
    {
        string stem = Path.Combine(directory, number.ToString("D8", System.Globalization.CultureInfo.InvariantCulture));
        var segment = new LogSegment(new MappedFile(stem + ".log", capacity), new MappedFile(stem + ".index", slots * 8L), slots, day, epoch, number);
        if (touch)
        {
            segment._log.Touch();
            segment._index.Touch();
        }

        return segment;
    }

    /// <summary>
    /// Opens an existing segment, drops a torn tail, and rebuilds the index from valid records.
    /// </summary>
    /// <param name="logPath">Path of the <c>.log</c> file.</param>
    /// <param name="capacity">Expected log size.</param>
    /// <param name="slots">Index slots.</param>
    /// <param name="day">Trading day as <c>yyyyMMdd</c>.</param>
    /// <param name="epoch">Epoch.</param>
    /// <param name="number">Segment number.</param>
    /// <returns>The recovered segment.</returns>
    public static LogSegment Recover(string logPath, long capacity, int slots, int day, int epoch, int number)
    {
        string indexPath = Path.ChangeExtension(logPath, ".index");
        var segment = new LogSegment(new MappedFile(logPath, capacity), new MappedFile(indexPath, slots * 8L), slots, day, epoch, number);
        segment.Rebuild();
        return segment;
    }

    /// <summary>
    /// True when <paramref name="payloadLength"/> fits in the bytes still free.
    /// </summary>
    /// <param name="payloadLength">Frame length.</param>
    /// <returns>False when the writer must rotate or reject the frame.</returns>
    public bool Fits(int payloadLength)
    {
        long offset = Volatile.Read(ref _writeOffset);
        return payloadLength >= 0 && offset + HeaderSize + payloadLength <= _log.Length;
    }

    /// <summary>
    /// Writes one record and its index slot. The caller publishes <c>committed</c> after this returns.
    /// </summary>
    /// <param name="payload">Original frame.</param>
    /// <param name="seq">Ingress sequence.</param>
    /// <param name="timestamp">Ingress timestamp.</param>
    /// <param name="offset">Byte offset of the record.</param>
    /// <returns>False when the record does not fit or the slot is outside the index.</returns>
    public bool TryAppend(ReadOnlySpan<byte> payload, long seq, long timestamp, out long offset)
    {
        offset = 0;
        if (!Fits(payload.Length))
        {
            return false;
        }

        long baseSeq = Volatile.Read(ref _baseSeq);
        if (baseSeq == 0)
        {
            baseSeq = seq;
            Volatile.Write(ref _baseSeq, baseSeq);
        }

        long slot = seq - baseSeq;
        if (slot < 0 || slot >= _slots)
        {
            return false;
        }

        long writeAt = Volatile.Read(ref _writeOffset);
        byte* record = _log.Pointer + writeAt;
        int length = payload.Length;
        Unsafe.WriteUnaligned(record, length);
        Unsafe.WriteUnaligned(record + SeqOffset, seq);
        Unsafe.WriteUnaligned(record + TsOffset, timestamp);
        if (length > 0)
        {
            payload.CopyTo(new Span<byte>(record + HeaderSize, length));
        }

        uint crc = Crc32.Compute(record, CrcOffset, record + HeaderSize, length);
        Unsafe.WriteUnaligned(record + CrcOffset, crc);
        Volatile.Write(ref Unsafe.AsRef<ulong>(_index.Pointer + (slot * 8)), (ulong)writeAt + 1);
        Volatile.Write(ref _writeOffset, writeAt + HeaderSize + length);
        Volatile.Write(ref _lastSeq, seq);
        offset = writeAt;
        return true;
    }

    /// <summary>
    /// Copies a record that is already visible in the index.
    /// </summary>
    /// <param name="seq">Ingress sequence.</param>
    /// <param name="destination">Receives the payload.</param>
    /// <returns>False when the slot is empty or the CRC does not match.</returns>
    public bool TryRead(long seq, IBufferWriter<byte> destination)
    {
        if (!TryLocate(seq, out long offset))
        {
            return false;
        }

        return TryCopyRecord(offset, seq, destination);
    }

    /// <summary>
    /// Copies the record that starts at <paramref name="offset"/>, as published by a client-index slot.
    /// </summary>
    /// <param name="offset">Byte offset of the record, as stored in the client index.</param>
    /// <param name="destination">Receives the payload.</param>
    /// <returns>False when the offset is outside the log, the record is torn or the CRC does not match.</returns>
    public bool TryReadAt(long offset, IBufferWriter<byte> destination) => TryCopyRecord(offset, expectedSeq: 0, destination);

    private bool TryCopyRecord(long offset, long expectedSeq, IBufferWriter<byte> destination)
    {
        if (offset < 0 || offset + HeaderSize > _log.Length)
        {
            return false;
        }

        byte* record = _log.Pointer + offset;
        int length = Unsafe.ReadUnaligned<int>(record);
        if (length < 0 || offset + HeaderSize + length > _log.Length)
        {
            return false;
        }

        uint stored = Unsafe.ReadUnaligned<uint>(record + CrcOffset);
        uint actual = Crc32.Compute(record, CrcOffset, record + HeaderSize, length);
        if (stored != actual || (expectedSeq != 0 && Unsafe.ReadUnaligned<long>(record + SeqOffset) != expectedSeq))
        {
            return false;
        }

        Span<byte> target = destination.GetSpan(length);
        if (length > 0)
        {
            new ReadOnlySpan<byte>(record + HeaderSize, length).CopyTo(target);
        }

        destination.Advance(length);
        return true;
    }

    /// <summary>
    /// Resolves a sequence to its byte offset.
    /// </summary>
    /// <param name="seq">Ingress sequence.</param>
    /// <param name="offset">Offset of the record.</param>
    /// <returns>False when the index slot is empty.</returns>
    public bool TryLocate(long seq, out long offset)
    {
        offset = 0;
        long baseSeq = Volatile.Read(ref _baseSeq);
        if (baseSeq == 0 || seq < baseSeq)
        {
            return false;
        }

        long slot = seq - baseSeq;
        if (slot < 0 || slot >= _slots)
        {
            return false;
        }

        ulong stored = Volatile.Read(ref Unsafe.AsRef<ulong>(_index.Pointer + (slot * 8)));
        if (stored == 0)
        {
            return false;
        }

        offset = (long)(stored - 1);
        return true;
    }

    /// <summary>
    /// Faults the pages of a segment that was created ahead of time.
    /// </summary>
    public void Touch()
    {
        _log.Touch();
        _index.Touch();
    }

    /// <summary>
    /// Releases both mappings.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _log.Dispose();
        _index.Dispose();
    }

    private void Rebuild()
    {
        byte* pointer = _log.Pointer;
        long offset = 0;
        long baseSeq = 0;
        long last = 0;
        while (offset + HeaderSize <= _log.Length)
        {
            int length = Unsafe.ReadUnaligned<int>(pointer + offset);
            if (length == 0)
            {
                break;
            }

            if (length < 0 || offset + HeaderSize + length > _log.Length)
            {
                break;
            }

            byte* record = pointer + offset;
            uint stored = Unsafe.ReadUnaligned<uint>(record + CrcOffset);
            uint actual = Crc32.Compute(record, CrcOffset, record + HeaderSize, length);
            if (stored != actual)
            {
                break;
            }

            long seq = Unsafe.ReadUnaligned<long>(record + SeqOffset);
            if (baseSeq == 0)
            {
                baseSeq = seq;
            }

            long slot = seq - baseSeq;
            if (slot >= 0 && slot < _slots)
            {
                Unsafe.WriteUnaligned(_index.Pointer + (slot * 8), (ulong)offset + 1);
            }

            last = seq;
            offset += HeaderSize + length;
        }

        Volatile.Write(ref _baseSeq, baseSeq);
        Volatile.Write(ref _lastSeq, last);
        Volatile.Write(ref _writeOffset, offset);
    }
}
