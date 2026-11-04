using System.Buffers;

namespace DropCopySim.Core.Fix;

/// <summary>
/// Locates complete FIX 4.4 frames in a byte stream by BeginString and CheckSum trailer.
/// </summary>
/// <remarks>
/// A frame runs from <c>8=FIX.4.4\x01</c> through the trailing <c>10=XXX\x01</c> inclusive.
/// Slimmed copy of the NexusRouter <c>FixFrameScanner</c> FIX 4.x path — no zero-alloc requirement.
/// </remarks>
public static class Fix44FrameScanner
{
    private const byte Soh = 0x01;

    private static ReadOnlySpan<byte> BeginString => "8=FIX.4.4\x01"u8;

    /// <summary>
    /// Tries to extract one complete FIX 4.4 frame and advances <paramref name="buffer"/> past it.
    /// </summary>
    /// <param name="buffer">Unconsumed bytes; may span multiple segments.</param>
    /// <param name="frame">The complete frame, or default when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when a complete frame was isolated.</returns>
    public static bool TryDecode(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> frame)
    {
        frame = default;
        if (buffer.IsEmpty)
        {
            return false;
        }

        var reader = new SequenceReader<byte>(buffer);
        while (reader.TryAdvanceTo((byte)'8', advancePastDelimiter: false))
        {
            long beginStart = reader.Consumed;
            if (TryMatchBegin(buffer.Slice(beginStart), out int beginLength))
            {
                long searchStart = beginStart + beginLength;
                if (TryFindChecksumEnd(buffer.Slice(searchStart), out long checksumLength))
                {
                    long frameLength = beginLength + checksumLength;
                    if (frameLength > int.MaxValue)
                    {
                        buffer = buffer.Slice(beginStart + 1);
                        return false;
                    }

                    frame = buffer.Slice(beginStart, frameLength);
                    buffer = buffer.Slice(beginStart + frameLength);
                    return true;
                }

                buffer = buffer.Slice(beginStart);
                return false;
            }

            if (IsPartialBegin(buffer.Slice(beginStart)))
            {
                buffer = buffer.Slice(beginStart);
                return false;
            }

            reader.Advance(1);
        }

        buffer = buffer.Slice(buffer.End);
        return false;
    }

    /// <summary>
    /// Tries to extract one complete FIX 4.4 frame from a contiguous span.
    /// </summary>
    /// <param name="buffer">Unconsumed bytes.</param>
    /// <param name="frame">Slice of <paramref name="buffer"/> covering the frame, or empty on failure.</param>
    /// <param name="consumed">Bytes to advance past (leading garbage plus the frame) on success; otherwise 0.</param>
    /// <returns><see langword="true"/> when a complete frame was isolated.</returns>
    public static bool TryDecode(ReadOnlySpan<byte> buffer, out ReadOnlySpan<byte> frame, out int consumed)
    {
        frame = default;
        consumed = 0;
        if (buffer.IsEmpty)
        {
            return false;
        }

        ReadOnlySpan<byte> begin = BeginString;
        int searchFrom = 0;
        while (true)
        {
            int relative = buffer[searchFrom..].IndexOf((byte)'8');
            if (relative < 0)
            {
                return false;
            }

            int beginStart = searchFrom + relative;
            ReadOnlySpan<byte> atBegin = buffer[beginStart..];
            if (atBegin.StartsWith(begin))
            {
                int afterBegin = beginStart + begin.Length;
                if (TryFindChecksumEnd(buffer[afterBegin..], out int checksumLength))
                {
                    int frameLength = begin.Length + checksumLength;
                    frame = buffer.Slice(beginStart, frameLength);
                    consumed = beginStart + frameLength;
                    return true;
                }

                return false;
            }

            if (IsPartialBegin(atBegin))
            {
                return false;
            }

            searchFrom = beginStart + 1;
        }
    }

    private static bool TryMatchBegin(ReadOnlySequence<byte> tail, out int length)
    {
        length = 0;
        ReadOnlySpan<byte> begin = BeginString;
        if (tail.Length < begin.Length)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[10];
        tail.Slice(0, header.Length).CopyTo(header);
        if (!header.SequenceEqual(begin))
        {
            return false;
        }

        length = header.Length;
        return true;
    }

    private static bool IsPartialBegin(ReadOnlySequence<byte> tail)
    {
        if (tail.IsEmpty)
        {
            return false;
        }

        ReadOnlySpan<byte> begin = BeginString;
        if (tail.Length >= begin.Length)
        {
            return false;
        }

        Span<byte> head = stackalloc byte[9];
        int count = (int)tail.Length;
        tail.Slice(0, count).CopyTo(head);
        return head[..count].SequenceEqual(begin[..count]);
    }

    private static bool IsPartialBegin(ReadOnlySpan<byte> tail)
    {
        if (tail.IsEmpty)
        {
            return false;
        }

        ReadOnlySpan<byte> begin = BeginString;
        if (tail.Length >= begin.Length)
        {
            return false;
        }

        return begin.StartsWith(tail);
    }

    private static bool TryFindChecksumEnd(ReadOnlySequence<byte> tail, out long checksumLength)
    {
        checksumLength = 0;
        var reader = new SequenceReader<byte>(tail);
        Span<byte> window = stackalloc byte[8];
        while (reader.TryAdvanceTo(Soh, advancePastDelimiter: false))
        {
            if (reader.Remaining < window.Length)
            {
                return false;
            }

            var peek = reader;
            if (!peek.TryCopyTo(window))
            {
                return false;
            }

            if (IsChecksumTrailer(window))
            {
                checksumLength = reader.Consumed + window.Length;
                return true;
            }

            reader.Advance(1);
        }

        return false;
    }

    private static bool TryFindChecksumEnd(ReadOnlySpan<byte> tail, out int checksumLength)
    {
        checksumLength = 0;
        int index = 0;
        while (index < tail.Length)
        {
            int relative = tail[index..].IndexOf(Soh);
            if (relative < 0)
            {
                return false;
            }

            int sohAt = index + relative;
            if (tail.Length - sohAt < 8)
            {
                return false;
            }

            ReadOnlySpan<byte> window = tail.Slice(sohAt, 8);
            if (IsChecksumTrailer(window))
            {
                checksumLength = sohAt + 8;
                return true;
            }

            index = sohAt + 1;
        }

        return false;
    }

    /// <summary>Matches SOH + <c>10=</c> + three digits + SOH.</summary>
    private static bool IsChecksumTrailer(ReadOnlySpan<byte> window) =>
        window.Length >= 8
        && window[1] == (byte)'1'
        && window[2] == (byte)'0'
        && window[3] == (byte)'='
        && IsDigit(window[4])
        && IsDigit(window[5])
        && IsDigit(window[6])
        && window[7] == Soh;

    private static bool IsDigit(byte value) => value is >= (byte)'0' and <= (byte)'9';
}
