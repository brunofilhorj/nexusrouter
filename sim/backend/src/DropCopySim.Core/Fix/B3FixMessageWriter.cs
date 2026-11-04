using System.Buffers.Text;

namespace DropCopySim.Core.Fix;

/// <summary>
/// Writes FIX 4.4 fields into a caller-owned buffer and finalizes BodyLength and CheckSum.
/// </summary>
/// <remarks>
/// <para>
/// CheckSum (tag 10) follows the B3 rule in <c>.kiro/steering/b3-guidelines.md</c> §15:
/// the sum of every byte from offset 0 up to, but not including, <c>10=</c>, modulo 256,
/// formatted as exactly three ASCII digits with leading zeros.
/// </para>
/// <para>
/// BodyLength (tag 9) is the number of bytes between the end of the BodyLength field and
/// the start of tag 10. The field is reserved as six zero-padded digits so the value can
/// be patched in place. The length does not include the BodyLength field itself.
/// </para>
/// </remarks>
public ref struct B3FixMessageWriter
{
    private const byte Soh = 0x01;
    private const int BodyLengthDigits = 6;
    private const int MaxBodyLength = 999_999;
    private const int CheckSumFieldLength = 7;
    private const int HeaderLength = 19;

    private readonly Span<byte> _destination;
    private readonly int _bodyStart;
    private readonly int _bodyLengthDigitsAt;
    private int _cursor;
    private bool _finished;

    /// <summary>
    /// Begins a FIX 4.4 frame with <c>8=FIX.4.4</c> and a reserved BodyLength field.
    /// </summary>
    /// <param name="destination">Buffer that receives the frame.</param>
    /// <exception cref="ArgumentException">The buffer cannot hold the header and the CheckSum trailer.</exception>
    public B3FixMessageWriter(Span<byte> destination)
    {
        if (destination.Length < HeaderLength + CheckSumFieldLength)
        {
            throw new ArgumentException(
                "Destination cannot hold a FIX frame header and trailer.",
                nameof(destination));
        }

        int cursor = 0;
        Append(destination, ref cursor, "8=FIX.4.4"u8);
        destination[cursor++] = Soh;
        Append(destination, ref cursor, "9="u8);
        int digitsAt = cursor;
        for (int i = 0; i < BodyLengthDigits; i++)
        {
            destination[cursor++] = (byte)'0';
        }

        destination[cursor++] = Soh;

        _destination = destination;
        _bodyLengthDigitsAt = digitsAt;
        _bodyStart = cursor;
        _cursor = cursor;
        _finished = false;
    }

    /// <summary>Bytes written so far, including the reserved header.</summary>
    public int Written => _cursor;

    /// <summary>
    /// Appends <c>tag=value</c> followed by SOH. <paramref name="value"/> is copied as raw bytes.
    /// </summary>
    /// <param name="tag">FIX tag number. Must be positive.</param>
    /// <param name="value">Field value bytes.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tag"/> is not positive.</exception>
    /// <exception cref="InvalidOperationException">The writer is already finalized, or the buffer is full.</exception>
    public void WriteField(int tag, scoped ReadOnlySpan<byte> value)
    {
        EnsureOpen();
        WriteTag(tag);
        EnsureCapacity(value.Length + 1);
        value.CopyTo(_destination[_cursor..]);
        _cursor += value.Length;
        _destination[_cursor++] = Soh;
    }

    /// <summary>
    /// Appends <c>tag=value</c> for a signed integer, followed by SOH.
    /// </summary>
    /// <param name="tag">FIX tag number. Must be positive.</param>
    /// <param name="value">Field value.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tag"/> is not positive.</exception>
    /// <exception cref="InvalidOperationException">The writer is already finalized, or the buffer is full.</exception>
    public void WriteField(int tag, long value)
    {
        EnsureOpen();
        WriteTag(tag);
        Span<byte> formatted = stackalloc byte[20];
        if (!Utf8Formatter.TryFormat(value, formatted, out int written))
        {
            throw new InvalidOperationException("Integer field did not fit in the destination.");
        }

        EnsureCapacity(written + 1);
        formatted[..written].CopyTo(_destination[_cursor..]);
        _cursor += written;
        _destination[_cursor++] = Soh;
    }

    /// <summary>
    /// Appends <c>tag=value</c> for an ASCII string, followed by SOH.
    /// </summary>
    /// <param name="tag">FIX tag number. Must be positive.</param>
    /// <param name="value">ASCII field value.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> contains a non-ASCII character.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tag"/> is not positive.</exception>
    /// <exception cref="InvalidOperationException">The writer is already finalized, or the buffer is full.</exception>
    public void WriteField(int tag, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] > 0x7F)
            {
                throw new ArgumentException("Field value must be ASCII.", nameof(value));
            }
        }

        EnsureOpen();
        WriteTag(tag);
        EnsureCapacity(value.Length + 1);
        for (int i = 0; i < value.Length; i++)
        {
            _destination[_cursor++] = (byte)value[i];
        }

        _destination[_cursor++] = Soh;
    }

    /// <summary>
    /// Appends a UTC timestamp field formatted as <c>YYYYMMDD-HH:MM:SS.sss</c>.
    /// </summary>
    /// <param name="tag">FIX tag, typically SendingTime (52) or TransactTime (60).</param>
    /// <param name="timestamp">
    /// Instant to format. <see cref="DateTimeKind.Local"/> is converted to UTC.
    /// <see cref="DateTimeKind.Utc"/> and <see cref="DateTimeKind.Unspecified"/> are written as their clock fields.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tag"/> is not positive.</exception>
    /// <exception cref="InvalidOperationException">The writer is already finalized, or the buffer is full.</exception>
    public void WriteUtcTimestamp(int tag, DateTime timestamp)
    {
        DateTime utc = timestamp.Kind == DateTimeKind.Local
            ? timestamp.ToUniversalTime()
            : timestamp;

        Span<byte> formatted = stackalloc byte[21];
        WriteFixedDigits(formatted, 0, utc.Year, 4);
        WriteFixedDigits(formatted, 4, utc.Month, 2);
        WriteFixedDigits(formatted, 6, utc.Day, 2);
        formatted[8] = (byte)'-';
        WriteFixedDigits(formatted, 9, utc.Hour, 2);
        formatted[11] = (byte)':';
        WriteFixedDigits(formatted, 12, utc.Minute, 2);
        formatted[14] = (byte)':';
        WriteFixedDigits(formatted, 15, utc.Second, 2);
        formatted[17] = (byte)'.';
        WriteFixedDigits(formatted, 18, utc.Millisecond, 3);
        WriteField(tag, formatted);
    }

    /// <summary>
    /// Patches BodyLength and appends CheckSum per <c>.kiro/steering/b3-guidelines.md</c> §15.
    /// </summary>
    /// <returns>Total bytes written, including the trailer.</returns>
    /// <exception cref="InvalidOperationException">
    /// The writer was already finalized, the body exceeds six digits, or the buffer cannot hold the trailer.
    /// </exception>
    public int Finish()
    {
        EnsureOpen();
        int bodyLength = _cursor - _bodyStart;
        if ((uint)bodyLength > MaxBodyLength)
        {
            throw new InvalidOperationException("BodyLength exceeds the six reserved digits.");
        }

        EnsureCapacity(CheckSumFieldLength);
        WriteFixedDigits(_destination.Slice(_bodyLengthDigitsAt, BodyLengthDigits), 0, bodyLength, BodyLengthDigits);

        int checksum = 0;
        for (int i = 0; i < _cursor; i++)
        {
            checksum += _destination[i];
        }

        checksum &= 0xFF;
        Append(_destination, ref _cursor, "10="u8);
        _destination[_cursor++] = (byte)('0' + (checksum / 100));
        _destination[_cursor++] = (byte)('0' + ((checksum / 10) % 10));
        _destination[_cursor++] = (byte)('0' + (checksum % 10));
        _destination[_cursor++] = Soh;
        _finished = true;
        return _cursor;
    }

    private void WriteTag(int tag)
    {
        if (tag <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tag), tag, "FIX tag must be positive.");
        }

        Span<byte> formatted = stackalloc byte[10];
        if (!Utf8Formatter.TryFormat(tag, formatted, out int written))
        {
            throw new InvalidOperationException("Tag did not fit in the destination.");
        }

        EnsureCapacity(written + 1);
        formatted[..written].CopyTo(_destination[_cursor..]);
        _cursor += written;
        _destination[_cursor++] = (byte)'=';
    }

    private void EnsureOpen()
    {
        if (_finished)
        {
            throw new InvalidOperationException("FIX frame is already finalized.");
        }
    }

    private void EnsureCapacity(int additionalBytes)
    {
        if (_cursor > _destination.Length - additionalBytes)
        {
            throw new InvalidOperationException("Destination is smaller than the FIX frame.");
        }
    }

    private static void Append(Span<byte> destination, ref int cursor, ReadOnlySpan<byte> literal)
    {
        literal.CopyTo(destination[cursor..]);
        cursor += literal.Length;
    }

    private static void WriteFixedDigits(Span<byte> destination, int offset, int value, int digits)
    {
        for (int i = digits - 1; i >= 0; i--)
        {
            destination[offset + i] = (byte)('0' + (value % 10));
            value /= 10;
        }
    }
}
