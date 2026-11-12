using System.Runtime.CompilerServices;

using NexusRouter.Domain.Abstractions;
using NexusRouter.Domain.Enums;
using NexusRouter.Domain.Models;

namespace NexusRouter.SessionLog;

/// <summary>
/// Segment number and file offset of an ingress record.
/// </summary>
/// <param name="Segment">Segment number.</param>
/// <param name="Offset">Byte offset in the <c>.log</c>.</param>
internal readonly record struct LogPosition(uint Segment, ulong Offset);

/// <summary>
/// Mapped <c>.cidx</c>. Two writers may publish different slots. <c>kind</c> is stored last.
/// </summary>
internal sealed unsafe class ClientIndex : IClientIndex, IDisposable
{
    private readonly Func<LogRef, LogPosition?> _locate;
    private readonly int _slots;
    private readonly string _directory;
    private readonly Func<int> _tradingDay;
    private MappedFile _file;
    private long _issued;
    private long _baseSeq = 1;
    private int _epoch;
    private int _disposed;

    /// <summary>
    /// Opens or creates the index of the current epoch.
    /// </summary>
    /// <param name="clientKey">Dispatcher id and client key, joined for diagnostics.</param>
    /// <param name="directory">Client directory.</param>
    /// <param name="slots">Pre-allocated slots.</param>
    /// <param name="epoch">Epoch whose file is opened.</param>
    /// <param name="nextOut">Next sequence to reserve.</param>
    /// <param name="tradingDay">Current trading day as <c>yyyyMMdd</c>.</param>
    /// <param name="locate">Resolves an ingress reference to a segment and offset.</param>
    public ClientIndex(
        string clientKey,
        string directory,
        int slots,
        int epoch,
        long nextOut,
        Func<int> tradingDay,
        Func<LogRef, LogPosition?> locate)
    {
        ClientKey = clientKey;
        _directory = directory;
        _slots = slots;
        _epoch = epoch;
        _tradingDay = tradingDay;
        _locate = locate;
        _issued = Math.Max(0, nextOut - 1);
        _file = OpenFile(epoch);
    }

    /// <inheritdoc />
    public string ClientKey { get; }

    /// <inheritdoc />
    public long NextOut => Interlocked.Read(ref _issued) + 1;

    /// <summary>
    /// Current epoch of this index.
    /// </summary>
    public int Epoch => Volatile.Read(ref _epoch);

    /// <summary>
    /// Highest published sequence in any <c>.cidx</c> under <paramref name="directory"/>.
    /// </summary>
    /// <param name="directory">Client directory.</param>
    /// <param name="slots">Slots per file.</param>
    /// <returns>The sequence, or zero when nothing is published.</returns>
    public static long ScanLastPublished(string directory, int slots)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        long last = 0;
        foreach (string file in Directory.GetFiles(directory, "*.cidx"))
        {
            using var mapped = new MappedFile(file, slots * 16L);
            last = Math.Max(last, Scan(mapped, slots));
        }

        return last;
    }

    /// <summary>
    /// Finds the newest daily index and its epoch under a client directory.
    /// </summary>
    /// <param name="directory">Client directory.</param>
    /// <param name="day">Trading day of the newest index.</param>
    /// <param name="epoch">Epoch encoded in the newest index name.</param>
    /// <returns>True when at least one valid daily index exists.</returns>
    public static bool TryGetLatestIndex(string directory, out DateOnly day, out int epoch)
    {
        day = default;
        epoch = 0;
        if (!Directory.Exists(directory))
        {
            return false;
        }

        bool found = false;
        foreach (string file in Directory.GetFiles(directory, "*.cidx"))
        {
            string name = Path.GetFileName(file);
            if (!SessionLogPaths.TryParseTradingDay(name, out DateOnly candidateDay)
                || !TryReadEpoch(name, out int candidateEpoch))
            {
                continue;
            }

            if (!found || candidateDay > day || (candidateDay == day && candidateEpoch > epoch))
            {
                found = true;
                day = candidateDay;
                epoch = candidateEpoch;
            }
        }

        return found;
    }

    /// <summary>
    /// Highest sequence whose <c>kind</c> is already non-zero. Zero when none is published.
    /// </summary>
    /// <returns>The sequence, or zero.</returns>
    public long LastPublishedSeq() => Scan(Volatile.Read(ref _file), _slots);

    private static long Scan(MappedFile file, int slots)
    {
        byte* pointer = file.Pointer;
        for (long slot = slots - 1; slot >= 0; slot--)
        {
            if (Volatile.Read(ref Unsafe.AsRef<byte>(pointer + (slot * 16))) != 0)
            {
                return 1 + slot;
            }
        }

        return 0;
    }

    /// <summary>
    /// Sets the next sequence after recovery. <paramref name="nextOut"/> is the next value <see cref="ReserveSeq"/> returns.
    /// </summary>
    /// <param name="nextOut">Next outbound sequence.</param>
    public void SetNextOut(long nextOut)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(nextOut, 1);
        Interlocked.Exchange(ref _issued, nextOut - 1);
    }

    /// <inheritdoc />
    public long ReserveSeq() => Interlocked.Increment(ref _issued);

    /// <inheritdoc />
    public void PublishApp(long seq, LogRef logRef)
    {
        LogPosition? position = _locate(logRef);
        if (position is null)
        {
            return;
        }

        WriteSlot(seq, kind: 1, logRef.Stream, position.Value.Segment, position.Value.Offset);
    }

    /// <inheritdoc />
    public void PublishAdmin(long seq) => WriteSlot(seq, kind: 2, stream: 0, segment: 0, offset: 0);

    /// <inheritdoc />
    public IndexReadResult TryGet(long seq, out IndexEntry entry)
    {
        entry = default;
        long baseSeq = Volatile.Read(ref _baseSeq);
        if (seq < baseSeq)
        {
            return IndexReadResult.Missing;
        }

        long slot = seq - baseSeq;
        if (slot < 0 || slot >= _slots || seq >= NextOut)
        {
            return IndexReadResult.Missing;
        }

        byte* start = FilePointer();
        if (start is null)
        {
            return IndexReadResult.Missing;
        }

        byte* pointer = start + (slot * 16);
        byte kind = Volatile.Read(ref Unsafe.AsRef<byte>(pointer));
        if (kind == 0)
        {
            return IndexReadResult.NotYet;
        }

        entry = new IndexEntry(
            kind,
            pointer[1],
            Unsafe.ReadUnaligned<ushort>(pointer + 2),
            Unsafe.ReadUnaligned<uint>(pointer + 4),
            Unsafe.ReadUnaligned<ulong>(pointer + 8));
        return IndexReadResult.Published;
    }

    /// <summary>
    /// Opens a new epoch file and makes the next reserved sequence 1.
    /// </summary>
    /// <returns>The new epoch number.</returns>
    public int ResetEpoch()
    {
        int epoch = Volatile.Read(ref _epoch) + 1;
        MappedFile next = OpenFile(epoch);
        MappedFile previous = Interlocked.Exchange(ref _file, next);
        Volatile.Write(ref _epoch, epoch);
        Volatile.Write(ref _baseSeq, 1);
        Interlocked.Exchange(ref _issued, 0);
        previous.Dispose();
        return epoch;
    }

    /// <summary>
    /// Releases the mapping.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Volatile.Read(ref _file).Dispose();
    }

    private byte* FilePointer() => Volatile.Read(ref _file).Pointer;

    private MappedFile OpenFile(int epoch)
    {
        string name = _tradingDay().ToString("D8", System.Globalization.CultureInfo.InvariantCulture)
            + "-e"
            + epoch.ToString("D3", System.Globalization.CultureInfo.InvariantCulture)
            + ".cidx";
        return new MappedFile(Path.Combine(_directory, name), _slots * 16L);
    }

    private static bool TryReadEpoch(string fileName, out int epoch)
    {
        ReadOnlySpan<char> name = fileName;
        int marker = name.LastIndexOf("-e", StringComparison.Ordinal);
        if (marker < 0 || !int.TryParse(name[(marker + 2)..^5], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out epoch))
        {
            epoch = 0;
            return false;
        }

        return true;
    }

    private void WriteSlot(long seq, byte kind, ushort stream, uint segment, ulong offset)
    {
        long baseSeq = Volatile.Read(ref _baseSeq);
        long slot = seq - baseSeq;
        if (slot < 0 || slot >= _slots)
        {
            return;
        }

        byte* start = FilePointer();
        if (start is null)
        {
            // O mapeamento já foi liberado (shutdown). Gravar aqui seria acesso a memória inválida.
            return;
        }

        byte* pointer = start + (slot * 16);
        pointer[1] = 0;
        Unsafe.WriteUnaligned(pointer + 2, stream);
        Unsafe.WriteUnaligned(pointer + 4, segment);
        Unsafe.WriteUnaligned(pointer + 8, offset);
        Volatile.Write(ref Unsafe.AsRef<byte>(pointer), kind);
    }
}
