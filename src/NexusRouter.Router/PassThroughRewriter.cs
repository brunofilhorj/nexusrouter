namespace NexusRouter.Router;

/// <summary>
/// Reescreve MsgSeqNum (tag 34) e CheckSum (tag 10) no próprio buffer FIX.
/// </summary>
/// <remarks>
/// O cálculo segue o FIX e a B3: soma dos bytes desde o início da mensagem até, sem incluir, o campo
/// <c>10=</c>, módulo 256, gravada em três dígitos ASCII. O SOH que fecha o campo anterior entra na soma.
/// Nenhum outro campo é alterado. Se o novo sequence number não cabe na largura atual, o buffer fica intacto.
/// </remarks>
public static class PassThroughRewriter
{
    private const byte Soh = 0x01;
    private const int ChecksumDigits = 3;

    private static readonly byte[] SeqNumPattern = [Soh, (byte)'3', (byte)'4', (byte)'='];
    private static readonly byte[] ChecksumPattern = [Soh, (byte)'1', (byte)'0', (byte)'='];

    /// <summary>
    /// Substitui o MsgSeqNum e recalcula o CheckSum no buffer informado.
    /// </summary>
    /// <param name="buffer">Frame FIX completo, incluindo o trailer <c>10=</c>.</param>
    /// <param name="newSeqNum">Novo MsgSeqNum. Valor negativo não altera o buffer.</param>
    /// <returns>
    /// <see langword="true"/> quando os dois campos foram reescritos.
    /// <see langword="false"/> quando o frame foi deixado intacto: campo ausente, checksum fora de 3 dígitos
    /// ou sequence number com mais dígitos do que a largura original.
    /// </returns>
    /// <remarks>
    /// Zero allocation. Seguro para chamada no hot path.
    /// Um valor mais curto é preenchido com espaços à esquerda. Um valor mais longo não é escrito.
    /// Quem chama registra o aviso quando o retorno é <see langword="false"/>.
    /// </remarks>
    public static bool Rewrite(Span<byte> buffer, long newSeqNum)
    {
        if (newSeqNum < 0
            || !TryLocate(buffer, SeqNumPattern, last: false, out int seqValueStart, out int seqValueLength)
            || !TryLocate(buffer, ChecksumPattern, last: true, out int checksumValueStart, out int checksumValueLength)
            || checksumValueLength != ChecksumDigits
            || checksumValueStart < seqValueStart + seqValueLength)
        {
            return false;
        }

        int digitCount = CountDigits(newSeqNum);
        if (digitCount > seqValueLength)
        {
            return false;
        }

        int pad = seqValueLength - digitCount;
        if (pad > 0)
        {
            buffer.Slice(seqValueStart, pad).Fill((byte)' ');
        }

        WriteDigits(newSeqNum, buffer.Slice(seqValueStart + pad, digitCount));

        // O padrão começa no SOH anterior a "10=". Esse SOH pertence ao campo de trás e entra na soma.
        int sumLength = checksumValueStart - (ChecksumPattern.Length - 1);
        int checksum = ComputeChecksum(buffer[..sumLength]);
        WriteChecksum(buffer.Slice(checksumValueStart, ChecksumDigits), checksum);
        return true;
    }

    /// <summary>
    /// Lê o MsgSeqNum já presente no frame.
    /// </summary>
    /// <param name="buffer">Frame FIX.</param>
    /// <param name="sequence">Valor numérico da tag 34 quando o método retorna <see langword="true"/>.</param>
    /// <returns><see langword="false"/> quando a tag 34 não existe ou o valor não é um inteiro não negativo.</returns>
    /// <remarks>Zero allocation. Seguro para chamada no hot path. Espaços à esquerda são ignorados na leitura.</remarks>
    internal static bool TryReadMsgSeqNum(ReadOnlySpan<byte> buffer, out long sequence)
    {
        sequence = 0;
        if (!TryLocate(buffer, SeqNumPattern, last: false, out int valueStart, out int valueLength))
        {
            return false;
        }

        ReadOnlySpan<byte> value = buffer.Slice(valueStart, valueLength);
        int index = 0;
        while (index < value.Length && value[index] == (byte)' ')
        {
            index++;
        }

        if (index == value.Length)
        {
            return false;
        }

        long parsed = 0;
        for (; index < value.Length; index++)
        {
            byte digit = value[index];
            if (digit < (byte)'0' || digit > (byte)'9')
            {
                return false;
            }

            int numeric = digit - (byte)'0';
            if (parsed > (long.MaxValue - numeric) / 10)
            {
                return false;
            }

            parsed = (parsed * 10) + numeric;
        }

        sequence = parsed;
        return true;
    }

    /// <summary>
    /// Soma os bytes de <paramref name="buffer"/> e reduz módulo 256.
    /// </summary>
    /// <param name="buffer">Prefixo da mensagem, sem o campo <c>10=</c>.</param>
    /// <returns>Checksum no intervalo 0–255.</returns>
    private static int ComputeChecksum(ReadOnlySpan<byte> buffer)
    {
        int sum = 0;
        for (int i = 0; i < buffer.Length; i++)
        {
            sum += buffer[i];
        }

        return sum & 0xFF;
    }

    private static bool TryLocate(
        ReadOnlySpan<byte> buffer,
        ReadOnlySpan<byte> pattern,
        bool last,
        out int valueStart,
        out int valueLength)
    {
        valueStart = 0;
        valueLength = 0;
        int tag = last ? buffer.LastIndexOf(pattern) : buffer.IndexOf(pattern);
        if (tag < 0)
        {
            return false;
        }

        int start = tag + pattern.Length;
        if ((uint)start >= (uint)buffer.Length)
        {
            return false;
        }

        int end = buffer[start..].IndexOf(Soh);
        if (end < 0)
        {
            return false;
        }

        valueStart = start;
        valueLength = end;
        return true;
    }

    private static int CountDigits(long value)
    {
        int count = 1;
        while (value >= 10)
        {
            value /= 10;
            count++;
        }

        return count;
    }

    private static void WriteDigits(long value, Span<byte> destination)
    {
        for (int i = destination.Length - 1; i >= 0; i--)
        {
            destination[i] = (byte)('0' + (value % 10));
            value /= 10;
        }
    }

    private static void WriteChecksum(Span<byte> destination, int checksum)
    {
        destination[0] = (byte)('0' + (checksum / 100));
        destination[1] = (byte)('0' + ((checksum / 10) % 10));
        destination[2] = (byte)('0' + (checksum % 10));
    }
}
