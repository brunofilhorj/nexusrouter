using System.Buffers.Text;

namespace NexusRouter.Ingress.Sessions;

/// <summary>
/// Monta mensagens administrativas FIX direto no buffer de quem chama.
/// </summary>
/// <remarks>
/// Não aloca no heap. O checksum é a soma dos bytes anteriores à tag 10, módulo 256, em três dígitos ASCII.
/// Toda mensagem leva SendingTime (tag 52), exigido no header FIX. O Logon inclui EncryptMethod (98) e HeartBtInt (108).
/// </remarks>
public static class FixMessageBuilder
{
    private const byte Soh = 0x01;
    private const int StackBodyCapacity = 768;

    /// <summary>
    /// Escreve um Logon (MsgType=A) em <paramref name="buffer"/>.
    /// </summary>
    /// <param name="buffer">Destino. O slice retornado aponta para o início deste buffer.</param>
    /// <param name="options">BeginString, comp IDs e HeartBtInt.</param>
    /// <param name="msgSeqNum">MsgSeqNum (tag 34), a partir de 1.</param>
    /// <returns>Slice preenchido, incluindo o checksum.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> é nulo.</exception>
    /// <exception cref="ArgumentException">BeginString ou comp ID é inválido.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="msgSeqNum"/> ou <see cref="SessionOptions.HeartBtInt"/> não é positivo.
    /// </exception>
    /// <exception cref="InvalidOperationException">A mensagem não cabe no buffer.</exception>
    /// <remarks>Zero allocation. O retorno é um slice de <paramref name="buffer"/>.</remarks>
    public static Span<byte> BuildLogon(Span<byte> buffer, SessionOptions options, long msgSeqNum)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.HeartBtInt);

        Span<byte> extra = stackalloc byte[32];
        int length = AppendLiteral(extra, 0, "98=0\x01"u8);
        length = AppendLiteral(extra, length, "108="u8);
        if (!Utf8Formatter.TryFormat(options.HeartBtInt, extra[length..], out int width))
        {
            throw new InvalidOperationException("HeartBtInt não coube no Logon.");
        }

        length += width;
        length = AppendSoh(extra, length);
        int written = WriteMessage(buffer, options, msgSeqNum, "A"u8, extra[..length]);
        return buffer[..written];
    }

    /// <summary>
    /// Escreve um Heartbeat (MsgType=0) em <paramref name="buffer"/>.
    /// </summary>
    /// <param name="buffer">Destino. O slice retornado aponta para o início deste buffer.</param>
    /// <param name="options">BeginString e comp IDs.</param>
    /// <param name="msgSeqNum">MsgSeqNum (tag 34), a partir de 1.</param>
    /// <param name="testReqId">
    /// TestReqID (tag 112) a ecoar. Vazio quando o heartbeat é periódico e não responde a um TestRequest.
    /// </param>
    /// <returns>Slice preenchido, incluindo o checksum.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> é nulo.</exception>
    /// <exception cref="ArgumentException">BeginString, comp ID ou <paramref name="testReqId"/> é inválido.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="msgSeqNum"/> não é positivo ou o TestReqID passa de 64 bytes.</exception>
    /// <exception cref="InvalidOperationException">A mensagem não cabe no buffer.</exception>
    /// <remarks>Zero allocation. O retorno é um slice de <paramref name="buffer"/>.</remarks>
    public static Span<byte> BuildHeartbeat(
        Span<byte> buffer,
        SessionOptions options,
        long msgSeqNum,
        ReadOnlySpan<byte> testReqId)
    {
        if (testReqId.IsEmpty)
        {
            int heartbeatLength = WriteMessage(buffer, options, msgSeqNum, "0"u8, ReadOnlySpan<byte>.Empty);
            return buffer[..heartbeatLength];
        }

        if (testReqId.Length > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(testReqId), testReqId.Length, "TestReqID aceita no máximo 64 bytes.");
        }

        if (testReqId.IndexOf(Soh) >= 0 || testReqId.IndexOf((byte)'=') >= 0)
        {
            throw new ArgumentException("TestReqID não pode conter SOH nem '='.", nameof(testReqId));
        }

        Span<byte> extra = stackalloc byte[4 + 64 + 1];
        int length = AppendLiteral(extra, 0, "112="u8);
        length = AppendBytes(extra, length, testReqId);
        length = AppendSoh(extra, length);
        int written = WriteMessage(buffer, options, msgSeqNum, "0"u8, extra[..length]);
        return buffer[..written];
    }

    /// <summary>
    /// Escreve um TestRequest (MsgType=1) em <paramref name="buffer"/>.
    /// </summary>
    /// <param name="buffer">Destino. O slice retornado aponta para o início deste buffer.</param>
    /// <param name="options">BeginString e comp IDs.</param>
    /// <param name="msgSeqNum">MsgSeqNum (tag 34), a partir de 1. Também vira o TestReqID.</param>
    /// <returns>Slice preenchido, incluindo o checksum.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> é nulo.</exception>
    /// <exception cref="ArgumentException">BeginString ou comp ID é inválido.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="msgSeqNum"/> não é positivo.</exception>
    /// <exception cref="InvalidOperationException">A mensagem não cabe no buffer.</exception>
    /// <remarks>Zero allocation. O retorno é um slice de <paramref name="buffer"/>.</remarks>
    public static Span<byte> BuildTestRequest(Span<byte> buffer, SessionOptions options, long msgSeqNum)
    {
        Span<byte> extra = stackalloc byte[32];
        int length = AppendLiteral(extra, 0, "112="u8);
        if (!Utf8Formatter.TryFormat(msgSeqNum, extra[length..], out int width))
        {
            throw new InvalidOperationException("TestReqID não coube no TestRequest.");
        }

        length += width;
        length = AppendSoh(extra, length);
        int written = WriteMessage(buffer, options, msgSeqNum, "1"u8, extra[..length]);
        return buffer[..written];
    }

    /// <summary>
    /// Escreve um Logout (MsgType=5) em <paramref name="buffer"/>.
    /// </summary>
    /// <param name="buffer">Destino. O slice retornado aponta para o início deste buffer.</param>
    /// <param name="options">BeginString e comp IDs.</param>
    /// <param name="msgSeqNum">MsgSeqNum (tag 34), a partir de 1.</param>
    /// <returns>Slice preenchido, incluindo o checksum.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> é nulo.</exception>
    /// <exception cref="ArgumentException">BeginString ou comp ID é inválido.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="msgSeqNum"/> não é positivo.</exception>
    /// <exception cref="InvalidOperationException">A mensagem não cabe no buffer.</exception>
    /// <remarks>Zero allocation. O retorno é um slice de <paramref name="buffer"/>.</remarks>
    public static Span<byte> BuildLogout(Span<byte> buffer, SessionOptions options, long msgSeqNum)
    {
        int written = WriteMessage(buffer, options, msgSeqNum, "5"u8, ReadOnlySpan<byte>.Empty);
        return buffer[..written];
    }

    /// <summary>
    /// Escreve um SequenceReset (MsgType=4) em <paramref name="buffer"/>.
    /// </summary>
    /// <param name="buffer">Destino. O slice retornado aponta para o início deste buffer.</param>
    /// <param name="options">BeginString e comp IDs.</param>
    /// <param name="msgSeqNum">MsgSeqNum (tag 34) desta mensagem. No GapFill é o início do buraco, não o próximo número da sessão.</param>
    /// <param name="newSeqNo">NewSeqNo (tag 36). O próximo MsgSeqNum que a sessão vai enviar.</param>
    /// <param name="gapFill">Quando <see langword="true"/>, GapFillFlag (tag 123) sai como Y.</param>
    /// <returns>Slice preenchido, incluindo o checksum.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> é nulo.</exception>
    /// <exception cref="ArgumentException">BeginString ou comp ID é inválido.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="msgSeqNum"/> ou <paramref name="newSeqNo"/> não é positivo.</exception>
    /// <exception cref="InvalidOperationException">A mensagem não cabe no buffer.</exception>
    /// <remarks>Zero allocation. O retorno é um slice de <paramref name="buffer"/>.</remarks>
    public static Span<byte> BuildSequenceReset(
        Span<byte> buffer,
        SessionOptions options,
        long msgSeqNum,
        long newSeqNo,
        bool gapFill)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(newSeqNo);

        Span<byte> extra = stackalloc byte[48];
        int length = AppendLiteral(extra, 0, gapFill ? "123=Y\x01"u8 : "123=N\x01"u8);
        length = AppendLiteral(extra, length, "36="u8);
        if (!Utf8Formatter.TryFormat(newSeqNo, extra[length..], out int width))
        {
            throw new InvalidOperationException("NewSeqNo não coube no SequenceReset.");
        }

        length += width;
        length = AppendSoh(extra, length);
        int written = WriteMessage(buffer, options, msgSeqNum, "4"u8, extra[..length]);
        return buffer[..written];
    }

    private static int WriteMessage(
        Span<byte> buffer,
        SessionOptions options,
        long msgSeqNum,
        ReadOnlySpan<byte> msgType,
        ReadOnlySpan<byte> extra)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(msgSeqNum);

        Span<byte> body = stackalloc byte[StackBodyCapacity];
        int bodyLength = WriteBody(body, options, msgSeqNum, msgType, extra);
        int lengthDigits = CountDigits(bodyLength);
        int headerLength = 2 + options.BeginString.Length + 1 + 2 + lengthDigits + 1;
        int total = headerLength + bodyLength + 7;
        if ((uint)total > (uint)buffer.Length)
        {
            throw new InvalidOperationException("O buffer é menor que a mensagem FIX.");
        }

        int offset = 0;
        offset = AppendLiteral(buffer, offset, "8="u8);
        offset = AppendAscii(buffer, offset, options.BeginString);
        offset = AppendSoh(buffer, offset);
        offset = AppendLiteral(buffer, offset, "9="u8);
        if (!Utf8Formatter.TryFormat(bodyLength, buffer[offset..], out int writtenDigits) || writtenDigits != lengthDigits)
        {
            throw new InvalidOperationException("BodyLength não foi formatado.");
        }

        offset += writtenDigits;
        offset = AppendSoh(buffer, offset);
        body[..bodyLength].CopyTo(buffer[offset..]);
        offset += bodyLength;

        int checksum = 0;
        for (int i = 0; i < offset; i++)
        {
            checksum += buffer[i];
        }

        checksum %= 256;
        offset = AppendLiteral(buffer, offset, "10="u8);
        offset = AppendDigit(buffer, offset, checksum / 100);
        offset = AppendDigit(buffer, offset, (checksum / 10) % 10);
        offset = AppendDigit(buffer, offset, checksum % 10);
        offset = AppendSoh(buffer, offset);
        return offset;
    }

    private static int WriteBody(
        Span<byte> body,
        SessionOptions options,
        long msgSeqNum,
        ReadOnlySpan<byte> msgType,
        ReadOnlySpan<byte> extra)
    {
        int offset = 0;
        offset = AppendLiteral(body, offset, "35="u8);
        offset = AppendBytes(body, offset, msgType);
        offset = AppendSoh(body, offset);
        offset = AppendLiteral(body, offset, "49="u8);
        offset = AppendAscii(body, offset, options.SenderCompId);
        offset = AppendSoh(body, offset);
        offset = AppendLiteral(body, offset, "56="u8);
        offset = AppendAscii(body, offset, options.TargetCompId);
        offset = AppendSoh(body, offset);
        offset = AppendLiteral(body, offset, "34="u8);
        if (!Utf8Formatter.TryFormat(msgSeqNum, body[offset..], out int sequenceWidth))
        {
            throw new InvalidOperationException("MsgSeqNum não coube na mensagem FIX.");
        }

        offset += sequenceWidth;
        offset = AppendSoh(body, offset);
        offset = AppendLiteral(body, offset, "52="u8);
        offset = AppendSendingTime(body, offset);
        offset = AppendSoh(body, offset);
        if (!extra.IsEmpty)
        {
            offset = AppendBytes(body, offset, extra);
        }

        return offset;
    }

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

    private static int AppendAscii(Span<byte> destination, int offset, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (offset > destination.Length - value.Length)
        {
            throw new InvalidOperationException("A mensagem FIX não coube no buffer de trabalho.");
        }

        for (int i = 0; i < value.Length; i++)
        {
            char ch = value[i];
            if (ch > 127 || ch == '=' || ch == Soh)
            {
                throw new ArgumentException("O valor deve ser ASCII e não pode conter SOH nem '='.", nameof(value));
            }

            destination[offset++] = (byte)ch;
        }

        return offset;
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
