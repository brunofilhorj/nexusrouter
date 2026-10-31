using System.Buffers;
using System.Buffers.Text;
using System.Text;

namespace NexusRouter.Egress.Fix;

/// <summary>
/// Serializa um <see cref="NexusRouter.Domain.Models.FixMessage"/> de volta para o formato de wire.
/// </summary>
/// <remarks>
/// O header padrão é reescrito: BeginString, BodyLength, MsgType, SenderCompID, TargetCompID, MsgSeqNum e SendingTime.
/// Os campos de header e o CheckSum antigo não são copiados. O CheckSum novo é a soma dos bytes anteriores à tag 10, módulo 256.
/// Não há alocação de string. O corpo usa um array do <see cref="ArrayPool{T}"/>.
/// </remarks>
public static class FixMessageSerializer
{
    private const byte Soh = 0x01;

    /// <summary>
    /// Escreve a mensagem em <paramref name="destination"/>.
    /// </summary>
    /// <param name="message">Mensagem tokenizada. BeginString (tag 8) precisa estar nos campos.</param>
    /// <param name="destination">Buffer de destino.</param>
    /// <param name="seqNum">MsgSeqNum (tag 34) da sessão que envia. Precisa ser positivo.</param>
    /// <param name="senderCompId">SenderCompID (tag 49).</param>
    /// <param name="targetCompId">TargetCompID (tag 56).</param>
    /// <returns>Quantidade de bytes escritos, incluindo o CheckSum.</returns>
    /// <exception cref="ArgumentException">Falta MsgType, BeginString ou um comp ID é inválido.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="seqNum"/> não é positivo.</exception>
    /// <exception cref="InvalidOperationException">A mensagem não cabe em <paramref name="destination"/>.</exception>
    public static int Serialize(
        in NexusRouter.Domain.Models.FixMessage message,
        Span<byte> destination,
        long seqNum,
        string senderCompId,
        string targetCompId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seqNum);
        ValidateToken(senderCompId, nameof(senderCompId));
        ValidateToken(targetCompId, nameof(targetCompId));
        if (message.MsgType.IsEmpty)
        {
            throw new ArgumentException("A mensagem não tem MsgType.", nameof(message));
        }

        if (!message.TryGetField(8, out ReadOnlySpan<byte> beginString) || beginString.IsEmpty)
        {
            throw new ArgumentException("A mensagem não tem BeginString (tag 8).", nameof(message));
        }

        byte[] workspace = ArrayPool<byte>.Shared.Rent(Math.Max(destination.Length, 256));
        try
        {
            int bodyLength = WriteBody(workspace, message, seqNum, senderCompId, targetCompId);
            int lengthDigits = CountDigits(bodyLength);
            int headerLength = 2 + beginString.Length + 1 + 2 + lengthDigits + 1;
            int total = headerLength + bodyLength + 7;
            if ((uint)total > (uint)destination.Length)
            {
                throw new InvalidOperationException("O buffer é menor que a mensagem FIX.");
            }

            int offset = 0;
            offset = AppendLiteral(destination, offset, "8="u8);
            offset = AppendBytes(destination, offset, beginString);
            offset = AppendSoh(destination, offset);
            offset = AppendLiteral(destination, offset, "9="u8);
            if (!Utf8Formatter.TryFormat(bodyLength, destination[offset..], out int writtenDigits) || writtenDigits != lengthDigits)
            {
                throw new InvalidOperationException("BodyLength não foi formatado.");
            }

            offset += writtenDigits;
            offset = AppendSoh(destination, offset);
            workspace.AsSpan(0, bodyLength).CopyTo(destination[offset..]);
            offset += bodyLength;

            int checksum = 0;
            for (int i = 0; i < offset; i++)
            {
                checksum += destination[i];
            }

            checksum &= 0xFF;
            offset = AppendLiteral(destination, offset, "10="u8);
            offset = AppendDigit(destination, offset, checksum / 100);
            offset = AppendDigit(destination, offset, (checksum / 10) % 10);
            offset = AppendDigit(destination, offset, checksum % 10);
            offset = AppendSoh(destination, offset);
            return offset;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(workspace, clearArray: true);
        }
    }

    private static int WriteBody(
        Span<byte> body,
        in NexusRouter.Domain.Models.FixMessage message,
        long seqNum,
        string senderCompId,
        string targetCompId)
    {
        int offset = 0;
        offset = AppendLiteral(body, offset, "35="u8);
        offset = AppendBytes(body, offset, message.MsgType);
        offset = AppendSoh(body, offset);
        offset = AppendLiteral(body, offset, "49="u8);
        offset = AppendAscii(body, offset, senderCompId);
        offset = AppendSoh(body, offset);
        offset = AppendLiteral(body, offset, "56="u8);
        offset = AppendAscii(body, offset, targetCompId);
        offset = AppendSoh(body, offset);
        offset = AppendLiteral(body, offset, "34="u8);
        if (!Utf8Formatter.TryFormat(seqNum, body[offset..], out int sequenceWidth))
        {
            throw new InvalidOperationException("MsgSeqNum não coube na mensagem FIX.");
        }

        offset += sequenceWidth;
        offset = AppendSoh(body, offset);
        offset = AppendLiteral(body, offset, "52="u8);
        offset = AppendSendingTime(body, offset);
        offset = AppendSoh(body, offset);

        ReadOnlySpan<NexusRouter.Domain.Models.FixField> fields = message.Fields;
        for (int i = 0; i < fields.Length; i++)
        {
            if (IsRewritten(fields[i].Tag))
            {
                continue;
            }

            offset = AppendTag(body, offset, fields[i].Tag);
            ReadOnlySpan<byte> value = fields[i].Value.Span;
            if (value.IndexOf(Soh) >= 0)
            {
                throw new ArgumentException("Um campo da mensagem contém SOH.", nameof(message));
            }

            offset = AppendBytes(body, offset, value);
            offset = AppendSoh(body, offset);
        }

        return offset;
    }

    private static bool IsRewritten(int tag) => tag is 8 or 9 or 10 or 35 or 49 or 56 or 34 or 52;

    private static int AppendSendingTime(Span<byte> destination, int offset)
    {
        Span<char> chars = stackalloc char[21];
        if (!DateTime.UtcNow.TryFormat(chars, out int charCount, "yyyyMMdd-HH:mm:ss.fff"))
        {
            throw new InvalidOperationException("SendingTime não foi formatado.");
        }

        if (offset > destination.Length - charCount)
        {
            throw new InvalidOperationException("A mensagem FIX não coube no buffer de trabalho.");
        }

        for (int i = 0; i < charCount; i++)
        {
            destination[offset++] = (byte)chars[i];
        }

        return offset;
    }

    private static int AppendTag(Span<byte> destination, int offset, int tag)
    {
        if (!Utf8Formatter.TryFormat(tag, destination[offset..], out int width))
        {
            throw new InvalidOperationException("A tag FIX não coube no buffer.");
        }

        offset += width;
        return AppendLiteral(destination, offset, "="u8);
    }

    private static int AppendAscii(Span<byte> destination, int offset, string value)
    {
        if (offset > destination.Length - value.Length)
        {
            throw new InvalidOperationException("A mensagem FIX não coube no buffer de trabalho.");
        }

        for (int i = 0; i < value.Length; i++)
        {
            destination[offset++] = (byte)value[i];
        }

        return offset;
    }

    private static void ValidateToken(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!Ascii.IsValid(value) || value.Contains('=') || value.Contains((char)Soh))
        {
            throw new ArgumentException("O valor deve ser ASCII e não pode conter SOH nem '='.", name);
        }
    }

    private static int AppendSoh(Span<byte> destination, int offset)
    {
        if ((uint)offset >= (uint)destination.Length)
        {
            throw new InvalidOperationException("A mensagem FIX não coube no buffer de trabalho.");
        }

        destination[offset] = Soh;
        return offset + 1;
    }

    private static int AppendDigit(Span<byte> destination, int offset, int digit)
    {
        if ((uint)offset >= (uint)destination.Length)
        {
            throw new InvalidOperationException("A mensagem FIX não coube no buffer de trabalho.");
        }

        destination[offset] = (byte)('0' + digit);
        return offset + 1;
    }

    private static int AppendLiteral(Span<byte> destination, int offset, ReadOnlySpan<byte> literal) =>
        AppendBytes(destination, offset, literal);

    private static int AppendBytes(Span<byte> destination, int offset, ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
        {
            return offset;
        }

        if ((uint)offset > (uint)(destination.Length - value.Length))
        {
            throw new InvalidOperationException("A mensagem FIX não coube no buffer de trabalho.");
        }

        value.CopyTo(destination[offset..]);
        return offset + value.Length;
    }

    private static int CountDigits(int value)
    {
        int digits = 1;
        while (value >= 10)
        {
            value /= 10;
            digits++;
        }

        return digits;
    }
}
