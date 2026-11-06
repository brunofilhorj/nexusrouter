namespace ClientProbe.Fix;

/// <summary>
/// Accumulates bytes from a stream and cuts complete FIX 4.4 frames: from <c>8=FIX.4.4\x01</c>
/// to <c>10=ddd\x01</c>. Handles frames that arrive in several chunks. Not thread-safe.
/// </summary>
public sealed class FixFrameScanner
{
    private static readonly byte[] BeginString = "8=FIX.4.4\u0001"u8.ToArray();
    private static readonly byte[] TrailerTag = "\u000110="u8.ToArray();
    private const int TrailerValueLength = 3;
    private const byte Soh = 0x01;

    private byte[] _buffer = new byte[4096];
    private int _length;

    /// <summary>Bytes currently held waiting for a complete frame.</summary>
    public int Pending => _length;

    /// <summary>Appends received bytes.</summary>
    /// <param name="data">Bytes read from the socket.</param>
    public void Append(ReadOnlySpan<byte> data)
    {
        if (_length + data.Length > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + data.Length));
        }
        data.CopyTo(_buffer.AsSpan(_length));
        _length += data.Length;
    }

    /// <summary>Extracts the next complete frame, discarding any garbage before <c>8=FIX.4.4</c>.</summary>
    /// <param name="frame">The frame bytes, including the trailing SOH.</param>
    /// <returns>True when a complete frame was available.</returns>
    public bool TryRead(out byte[] frame)
    {
        frame = [];
        var span = _buffer.AsSpan(0, _length);
        var start = span.IndexOf(BeginString);
        if (start < 0)
        {
            // Keep only a possible partial BeginString suffix.
            var keep = Math.Min(BeginString.Length - 1, _length);
            Discard(_length - keep);
            return false;
        }
        if (start > 0)
        {
            Discard(start);
            span = _buffer.AsSpan(0, _length);
        }
        var offset = BeginString.Length;
        while (offset < span.Length)
        {
            var idx = span[offset..].IndexOf(TrailerTag);
            if (idx < 0)
            {
                return false;
            }
            var valueStart = offset + idx + TrailerTag.Length;
            if (valueStart + TrailerValueLength + 1 > span.Length)
            {
                return false;
            }
            if (IsDigits(span.Slice(valueStart, TrailerValueLength)) && span[valueStart + TrailerValueLength] == Soh)
            {
                var total = valueStart + TrailerValueLength + 1;
                frame = span[..total].ToArray();
                Discard(total);
                return true;
            }
            offset += idx + 1;
        }
        return false;
    }

    private static bool IsDigits(ReadOnlySpan<byte> s)
    {
        foreach (var b in s)
        {
            if (b is < (byte)'0' or > (byte)'9')
            {
                return false;
            }
        }
        return true;
    }

    private void Discard(int count)
    {
        if (count <= 0)
        {
            return;
        }
        Buffer.BlockCopy(_buffer, count, _buffer, 0, _length - count);
        _length -= count;
    }
}
