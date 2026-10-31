using System.Text;

namespace NexusRouter.Egress.Fix;

/// <summary>
/// Lê um frame FIX delimitado pelo checksum e o valor de uma tag, sem alocar.
/// </summary>
internal static class FixWireFrame
{
    private const byte Soh = 0x01;

    /// <summary>
    /// Localiza o fim do primeiro frame completo no buffer.
    /// </summary>
    /// <param name="buffer">Bytes já lidos do socket.</param>
    /// <param name="frameLength">Tamanho do frame, incluindo o SOH da tag 10, quando o método retorna <see langword="true"/>.</param>
    /// <returns><see langword="false"/> quando o checksum ainda não chegou.</returns>
    internal static bool TryTakeFrame(ReadOnlySpan<byte> buffer, out int frameLength)
    {
        ReadOnlySpan<byte> marker = "10="u8;
        int searchFrom = 0;
        while (searchFrom < buffer.Length)
        {
            int relative = buffer[searchFrom..].IndexOf(marker);
            if (relative < 0)
            {
                break;
            }

            int at = searchFrom + relative;
            bool boundary = at == 0 || buffer[at - 1] == Soh;
            if (boundary
                && at + 6 < buffer.Length
                && IsDigit(buffer[at + 3])
                && IsDigit(buffer[at + 4])
                && IsDigit(buffer[at + 5])
                && buffer[at + 6] == Soh)
            {
                frameLength = at + 7;
                return true;
            }

            searchFrom = at + 3;
        }

        frameLength = 0;
        return false;
    }

    /// <summary>
    /// Lê o valor de uma tag no frame.
    /// </summary>
    /// <param name="frame">Frame completo.</param>
    /// <param name="tag">Número da tag.</param>
    /// <param name="value">Valor sem o SOH quando o método retorna <see langword="true"/>.</param>
    /// <returns><see langword="false"/> quando a tag não está no frame.</returns>
    internal static bool TryReadTag(ReadOnlySpan<byte> frame, int tag, out ReadOnlySpan<byte> value)
    {
        int index = 0;
        while (index < frame.Length)
        {
            int parsed = 0;
            bool any = false;
            while (index < frame.Length && IsDigit(frame[index]))
            {
                any = true;
                parsed = (parsed * 10) + (frame[index] - (byte)'0');
                index++;
            }

            if (!any || index >= frame.Length || frame[index] != (byte)'=')
            {
                break;
            }

            index++;
            int valueStart = index;
            int end = frame[index..].IndexOf(Soh);
            if (end < 0)
            {
                break;
            }

            if (parsed == tag)
            {
                value = frame.Slice(valueStart, end);
                return true;
            }

            index = valueStart + end + 1;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Compara o valor da tag com um token ASCII.
    /// </summary>
    /// <param name="value">Valor lido do frame.</param>
    /// <param name="expected">Texto esperado.</param>
    /// <returns><see langword="true"/> quando os bytes coincidem.</returns>
    internal static bool EqualsAscii(ReadOnlySpan<byte> value, string expected)
    {
        if (value.Length != expected.Length)
        {
            return false;
        }

        for (int i = 0; i < expected.Length; i++)
        {
            if (value[i] != (byte)expected[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Copia um token ASCII para string.
    /// </summary>
    /// <param name="value">Valor lido do frame.</param>
    /// <param name="text">Token quando o método retorna <see langword="true"/>.</param>
    /// <returns><see langword="false"/> quando o valor é vazio ou não é um token ASCII seguro para nome de arquivo.</returns>
    internal static bool TryDecodeSessionKey(ReadOnlySpan<byte> value, out string text)
    {
        if (value.IsEmpty)
        {
            text = string.Empty;
            return false;
        }

        foreach (byte item in value)
        {
            bool allowed = item is (>= (byte)'a' and <= (byte)'z')
                or (>= (byte)'A' and <= (byte)'Z')
                or (>= (byte)'0' and <= (byte)'9')
                or (byte)'-'
                or (byte)'_'
                or (byte)'.';
            if (!allowed)
            {
                text = string.Empty;
                return false;
            }
        }

        text = Encoding.ASCII.GetString(value);
        return true;
    }

    /// <summary>
    /// Lê um inteiro não negativo.
    /// </summary>
    /// <param name="value">Dígitos ASCII.</param>
    /// <param name="number">Valor quando o método retorna <see langword="true"/>.</param>
    /// <returns><see langword="false"/> quando o texto não é um inteiro não negativo.</returns>
    internal static bool TryParseNonNegative(ReadOnlySpan<byte> value, out long number)
    {
        number = 0;
        if (value.IsEmpty)
        {
            return false;
        }

        foreach (byte item in value)
        {
            if (!IsDigit(item))
            {
                number = 0;
                return false;
            }

            number = (number * 10) + (item - (byte)'0');
        }

        return true;
    }

    private static bool IsDigit(byte value) => value is >= (byte)'0' and <= (byte)'9';
}
