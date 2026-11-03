using System.Text;

namespace NexusRouter.Router.Tests;

internal static class FixMessageTestData
{
    private const char Soh = '\x01';

    public static byte[] CreateValidFrame(long? sequenceNumber = 12)
    {
        string body = string.Concat(
            $"35=D{Soh}",
            sequenceNumber is null ? string.Empty : $"34={sequenceNumber.Value}{Soh}",
            $"49=CLIENT{Soh}",
            $"56=SERVER{Soh}",
            $"52=20260101-00:00:00{Soh}",
            $"11=ORDER-1{Soh}",
            $"55=IBM{Soh}");
        string bodyLength = Encoding.ASCII.GetByteCount(body).ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        string prefix = $"8=FIX.4.4{Soh}9={bodyLength}{Soh}{body}";
        byte[] prefixBytes = Encoding.ASCII.GetBytes(prefix);
        int checksum = ComputeChecksum(prefixBytes);
        string trailer = $"10={checksum:D3}{Soh}";

        return Encoding.ASCII.GetBytes(prefix + trailer);
    }

    public static int ComputeChecksum(ReadOnlySpan<byte> bytes)
    {
        int sum = 0;
        foreach (byte value in bytes)
        {
            sum += value;
        }

        return sum & 0xFF;
    }

    public static bool HasValidChecksum(ReadOnlySpan<byte> frame)
    {
        ReadOnlySpan<byte> checksumTag = "\u000110="u8;
        int checksumTagStart = frame.LastIndexOf(checksumTag);
        if (checksumTagStart < 0
            || checksumTagStart + checksumTag.Length + 4 > frame.Length
            || frame[checksumTagStart + checksumTag.Length + 3] != Soh)
        {
            return false;
        }

        ReadOnlySpan<byte> checksumDigits = frame.Slice(checksumTagStart + checksumTag.Length, 3);
        if (!byte.TryParse(checksumDigits, out byte expected))
        {
            return false;
        }

        int actual = ComputeChecksum(frame[..(checksumTagStart + 1)]);
        return actual == expected;
    }

    public static bool HasValidBodyLength(ReadOnlySpan<byte> frame)
    {
        ReadOnlySpan<byte> bodyLengthTag = "9="u8;
        int bodyLengthTagStart = frame.IndexOf(bodyLengthTag);
        int checksumTagStart = frame.LastIndexOf("\u000110="u8);
        if (bodyLengthTagStart < 0 || checksumTagStart < 0)
        {
            return false;
        }

        int bodyStart = frame[(bodyLengthTagStart + bodyLengthTag.Length)..].IndexOf((byte)Soh);
        if (bodyStart < 0)
        {
            return false;
        }

        int bodyStartIndex = bodyLengthTagStart + bodyLengthTag.Length + bodyStart + 1;
        ReadOnlySpan<byte> bodyLengthDigits = frame.Slice(
            bodyLengthTagStart + bodyLengthTag.Length,
            bodyStart);
        return int.TryParse(bodyLengthDigits, out int bodyLength)
            && bodyLength == checksumTagStart + 1 - bodyStartIndex;
    }
}
