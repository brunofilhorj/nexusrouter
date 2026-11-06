namespace NexusRouter.Router;

/// <summary>
/// Reescreve MsgSeqNum (tag 34) e CheckSum (tag 10) no próprio buffer FIX.
/// </summary>
/// <remarks>
/// O cálculo segue o FIX e a B3: soma dos bytes desde o início da mensagem até, sem incluir, o campo
/// <c>10=</c>, módulo 256, gravada em três dígitos ASCII. O SOH que fecha o campo anterior entra na soma.
/// Nenhum outro campo é alterado. O MsgSeqNum é sempre gravado com os dígitos exatos, sem preenchimento.
/// </remarks>
public static class PassThroughRewriter
{
    /// <summary>
    /// Folga máxima, em bytes, que <see cref="TryRewriteTo"/> pode somar ao tamanho do frame de origem.
    /// Cobre um MsgSeqNum de até 19 dígitos no lugar de um de 1 dígito e o crescimento do BodyLength.
    /// </summary>
    public const int MaxGrowthBytes = 32;

    private const byte Soh = 0x01;
    private const int ChecksumDigits = 3;

    private static readonly byte[] BodyLengthPattern = [Soh, (byte)'9', (byte)'='];
    private static readonly byte[] SeqNumPattern = [Soh, (byte)'3', (byte)'4', (byte)'='];
    private static readonly byte[] ChecksumPattern = [Soh, (byte)'1', (byte)'0', (byte)'='];

    /// <summary>
    /// Substitui o MsgSeqNum e recalcula o CheckSum no buffer informado, sem mudar o tamanho do frame.
    /// </summary>
    /// <param name="buffer">Frame FIX completo, incluindo o trailer <c>10=</c>.</param>
    /// <param name="newSeqNum">Novo MsgSeqNum. Valor negativo não altera o buffer.</param>
    /// <returns>
    /// <see langword="true"/> quando os dois campos foram reescritos.
    /// <see langword="false"/> quando o frame foi deixado intacto: campo ausente, checksum fora de 3 dígitos
    /// ou sequence number com número de dígitos diferente da largura atual do campo.
    /// </returns>
    /// <remarks>
    /// Zero allocation. Seguro para chamada no hot path.
    /// Não preenche com espaços: FIX 4.4 define a tag 34 como inteiro. Para um valor de largura diferente,
    /// use <see cref="TryRewriteTo"/>. Quem chama registra o aviso quando o retorno é <see langword="false"/>.
    /// </remarks>
    public static bool Rewrite(Span<byte> buffer, long newSeqNum)
    {
        if (newSeqNum < 0
            || !TryLocate(buffer, SeqNumPattern, last: false, out int seqValueStart, out int seqValueLength)
            || !TryLocate(buffer, ChecksumPattern, last: true, out int checksumValueStart, out int checksumValueLength)
            || checksumValueLength != ChecksumDigits
            || checksumValueStart < seqValueStart + seqValueLength
            || CountDigits(newSeqNum) != seqValueLength)
        {
            return false;
        }

        WriteDigits(newSeqNum, buffer.Slice(seqValueStart, seqValueLength));

        // O padrão começa no SOH anterior a "10=". Esse SOH pertence ao campo de trás e entra na soma.
        int sumLength = checksumValueStart - (ChecksumPattern.Length - 1);
        int checksum = ComputeChecksum(buffer[..sumLength]);
        WriteChecksum(buffer.Slice(checksumValueStart, ChecksumDigits), checksum);
        return true;
    }

    /// <summary>
    /// Copia o frame de origem para o destino com o MsgSeqNum informado, nos dígitos exatos, e recalcula
    /// BodyLength (tag 9) e CheckSum (tag 10).
    /// </summary>
    /// <param name="source">Frame FIX completo, incluindo o trailer <c>10=</c>.</param>
    /// <param name="destination">
    /// Buffer de saída, sem sobreposição com <paramref name="source"/>. Precisa de até
    /// <c>source.Length + <see cref="MaxGrowthBytes"/></c> bytes.
    /// </param>
    /// <param name="newSeqNum">Novo MsgSeqNum. Valor negativo é recusado.</param>
    /// <param name="written">Bytes gravados em <paramref name="destination"/>. Zero quando o retorno é <see langword="false"/>.</param>
    /// <returns>
    /// <see langword="true"/> quando o frame foi gravado. <see langword="false"/> quando falta a tag 9, 34 ou 10,
    /// o checksum não tem 3 dígitos, a ordem das tags é inválida ou o destino é pequeno demais.
    /// </returns>
    /// <remarks>
    /// Zero allocation. Seguro para chamada no hot path. Quando o número de dígitos não muda, o resultado
    /// tem o mesmo tamanho da origem.
    /// </remarks>
    public static bool TryRewriteTo(ReadOnlySpan<byte> source, Span<byte> destination, long newSeqNum, out int written)
    {
        written = 0;
        if (newSeqNum < 0
            || !TryLocate(source, BodyLengthPattern, last: false, out int lengthStart, out int lengthLength)
            || !TryLocate(source, SeqNumPattern, last: false, out int seqStart, out int seqLength)
            || !TryLocate(source, ChecksumPattern, last: true, out int checksumStart, out int checksumLength)
            || checksumLength != ChecksumDigits
            || seqStart < lengthStart + lengthLength
            || checksumStart < seqStart + seqLength)
        {
            return false;
        }

        // O corpo vai do byte seguinte ao SOH do campo 9 até o SOH que antecede "10=", inclusive.
        int bodyStart = lengthStart + lengthLength + 1;
        int bodyEnd = checksumStart - (ChecksumPattern.Length - 1);
        int seqDigits = CountDigits(newSeqNum);
        long newBodyLength = (bodyEnd - bodyStart) + (seqDigits - seqLength);
        if (newBodyLength < 0)
        {
            return false;
        }

        int lengthDigits = CountDigits(newBodyLength);
        int total = source.Length + (lengthDigits - lengthLength) + (seqDigits - seqLength);
        if (destination.Length < total)
        {
            return false;
        }

        int position = 0;
        position += CopyRange(source[..lengthStart], destination, position);
        WriteDigits(newBodyLength, destination.Slice(position, lengthDigits));
        position += lengthDigits;
        position += CopyRange(source[(lengthStart + lengthLength)..seqStart], destination, position);
        WriteDigits(newSeqNum, destination.Slice(position, seqDigits));
        position += seqDigits;
        position += CopyRange(source[(seqStart + seqLength)..checksumStart], destination, position);

        int checksum = ComputeChecksum(destination[..(position - (ChecksumPattern.Length - 1))]);
        WriteChecksum(destination.Slice(position, ChecksumDigits), checksum);
        position += ChecksumDigits;
        position += CopyRange(source[(checksumStart + ChecksumDigits)..], destination, position);

        written = position;
        return true;
    }

    private static int CopyRange(ReadOnlySpan<byte> range, Span<byte> destination, int position)
    {
        range.CopyTo(destination[position..]);
        return range.Length;
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
