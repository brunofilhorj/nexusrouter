namespace NexusRouter.SessionLog;

/// <summary>
/// CRC-32/ISO-HDLC over a record header and its payload.
/// </summary>
internal static class Crc32
{
    private static readonly uint[] Table = CreateTable();

    /// <summary>
    /// Computes the checksum of <paramref name="headerLength"/> bytes followed by the payload.
    /// </summary>
    /// <param name="header">Start of the header. The CRC field itself is not included.</param>
    /// <param name="headerLength">Header bytes that participate. The record uses 20.</param>
    /// <param name="payload">Payload bytes.</param>
    /// <param name="payloadLength">Payload length.</param>
    /// <returns>The CRC-32 value stored in the record.</returns>
    public static unsafe uint Compute(byte* header, int headerLength, byte* payload, int payloadLength)
    {
        uint crc = 0xFFFF_FFFFu;
        crc = Update(crc, header, headerLength);
        crc = Update(crc, payload, payloadLength);
        return crc ^ 0xFFFF_FFFFu;
    }

    private static unsafe uint Update(uint crc, byte* data, int length)
    {
        uint[] table = Table;
        for (int i = 0; i < length; i++)
        {
            crc = table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] CreateTable()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) == 1 ? 0xEDB8_8320u ^ (value >> 1) : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }
}
