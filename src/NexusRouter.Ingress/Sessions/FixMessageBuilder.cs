using System.Buffers.Text;
using System.Text;

namespace NexusRouter.Ingress.Sessions;

/// <summary>
/// Monta mensagens administrativas FIX direto no buffer de quem chama.
/// </summary>
/// <remarks>
/// Não aloca no heap. O checksum é a soma dos bytes anteriores à tag 10, módulo 256, em três dígitos ASCII.
/// Toda mensagem leva SendingTime (tag 52), exigido no header FIX. O Logon inclui EncryptMethod (98), HeartBtInt (108)
/// e, quando configurados, Text (58), RawData (95/96), ResetSeqNumFlag (141) e COD (35002/35003).
/// </remarks>
public static class FixMessageBuilder
{
    private const byte Soh = 0x01;
    private const int StackBodyCapacity = 768;

    /// <summary>
    /// Escreve um Logon (MsgType=A) em <paramref name="buffer"/>.
    /// </summary>
    /// <param name="buffer">Destino. O slice retornado aponta para o início deste buffer.</param>
    /// <param name="options">BeginString, comp IDs, HeartBtInt e campos opcionais do Logon.</param>
    /// <param name="msgSeqNum">MsgSeqNum (tag 34), a partir de 1.</param>
    /// <returns>Slice preenchido, incluindo o checksum.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> é nulo.</exception>
    /// <exception cref="ArgumentException">BeginString, comp ID ou Text (58) é inválido.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="msgSeqNum"/> ou <see cref="SessionOptions.HeartBtInt"/> não é positivo,
    /// ou COD está fora da faixa da B3.
    /// </exception>
    /// <exception cref="InvalidOperationException">A mensagem não cabe no buffer.</exception>
    /// <remarks>Zero allocation. O retorno é um slice de <paramref name="buffer"/>. Não envia senha.</remarks>
    public static Span<byte> BuildLogon(Span<byte> buffer, SessionOptions options, long msgSeqNum) =>
        BuildLogon(buffer, options, msgSeqNum, ReadOnlySpan<byte>.Empty);

    /// <summary>
    /// Escreve um Logon (MsgType=A) em <paramref name="buffer"/>, com a senha em RawData (tag 96).
    /// </summary>
    /// <param name="buffer">Destino. O slice retornado aponta para o início deste buffer.</param>
    /// <param name="options">BeginString, comp IDs, HeartBtInt e campos opcionais do Logon.</param>
    /// <param name="msgSeqNum">MsgSeqNum (tag 34), a partir de 1.</param>
    /// <param name="password">
    /// Senha ASCII da sessão. Vazia omite RawDataLength (95) e RawData (96). O valor não entra em mensagem de exceção.
    /// </param>
    /// <returns>Slice preenchido, incluindo o checksum.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> é nulo.</exception>
    /// <exception cref="ArgumentException">BeginString, comp ID, Text (58) ou a senha é inválido.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="msgSeqNum"/> ou <see cref="SessionOptions.HeartBtInt"/> não é positivo,
    /// a senha passa de 256 bytes, ou COD está fora da faixa da B3.
    /// </exception>
    /// <exception cref="InvalidOperationException">A mensagem não cabe no buffer.</exception>
    /// <remarks>
    /// Zero allocation. O retorno é um slice de <paramref name="buffer"/>.
    /// A senha não é registrada em log: este método não escreve em <c>ILogger</c>.
    /// </remarks>
    public static Span<byte> BuildLogon(
        Span<byte> buffer,
        SessionOptions options,
        long msgSeqNum,
        ReadOnlySpan<byte> password)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.HeartBtInt);
        if (options.CancelOnDisconnectType is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.CancelOnDisconnectType,
                "CancelOnDisconnectType aceita 0, 1, 2 ou 3.");
        }

        if (options.CodTimeoutWindow is < 0 or > 60_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.CodTimeoutWindow,
                "CODTimeoutWindow aceita de 0 a 60000 milissegundos.");
        }

        if (password.Length > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(password), password.Length, "A senha do Logon aceita no máximo 256 bytes.");
        }

        ValidatePassword(password);
        if (options.LogonText is { Length: > 0 })
        {
            ValidateLogonText(options.LogonText);
        }

        Span<byte> extra = stackalloc byte[512];
        int length = AppendLiteral(extra, 0, "98=0\x01"u8);
        length = AppendLiteral(extra, length, "108="u8);
        length = AppendInt(extra, length, options.HeartBtInt);
        length = AppendSoh(extra, length);
        if (options.ResetSeqNumFlag)
        {
            length = AppendLiteral(extra, length, "141=Y\x01"u8);
        }

        if (options.NextExpectedMsgSeqNum is long nextExpected)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nextExpected);
            length = AppendLiteral(extra, length, "789="u8);
            if (!Utf8Formatter.TryFormat(nextExpected, extra[length..], out int nextWidth))
            {
                throw new InvalidOperationException("NextExpectedMsgSeqNum não coube no Logon.");
            }

            length += nextWidth;
            length = AppendSoh(extra, length);
        }

        if (!string.IsNullOrEmpty(options.LogonText))
        {
            length = AppendLiteral(extra, length, "58="u8);
            length = AppendAscii(extra, length, options.LogonText);
            length = AppendSoh(extra, length);
        }

        if (!password.IsEmpty)
        {
            length = AppendLiteral(extra, length, "95="u8);
            length = AppendInt(extra, length, password.Length);
            length = AppendSoh(extra, length);
            length = AppendLiteral(extra, length, "96="u8);
            length = AppendBytes(extra, length, password);
            length = AppendSoh(extra, length);
        }

        if (options.CancelOnDisconnectType is int cancelOnDisconnectType)
        {
            length = AppendLiteral(extra, length, "35002="u8);
            length = AppendInt(extra, length, cancelOnDisconnectType);
            length = AppendSoh(extra, length);
        }

        if (options.CodTimeoutWindow is int codTimeoutWindow)
        {
            length = AppendLiteral(extra, length, "35003="u8);
            length = AppendInt(extra, length, codTimeoutWindow);
            length = AppendSoh(extra, length);
        }

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
    public static Span<byte> BuildLogout(Span<byte> buffer, SessionOptions options, long msgSeqNum) =>
        BuildLogout(buffer, options, msgSeqNum, ReadOnlySpan<byte>.Empty);

    /// <summary>
    /// Escreve um Logout (MsgType=5) com Text (tag 58).
    /// </summary>
    /// <param name="buffer">Destino. O slice retornado aponta para o início deste buffer.</param>
    /// <param name="options">BeginString e comp IDs.</param>
    /// <param name="msgSeqNum">MsgSeqNum (tag 34), a partir de 1.</param>
    /// <param name="text">Text (tag 58). Vazio omite o campo.</param>
    /// <returns>Slice preenchido, incluindo o checksum.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> é nulo.</exception>
    /// <exception cref="ArgumentException">BeginString, comp ID ou <paramref name="text"/> é inválido.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="msgSeqNum"/> não é positivo ou o texto passa de 128 bytes.</exception>
    /// <exception cref="InvalidOperationException">A mensagem não cabe no buffer.</exception>
    /// <remarks>Zero allocation. O retorno é um slice de <paramref name="buffer"/>.</remarks>
    public static Span<byte> BuildLogout(Span<byte> buffer, SessionOptions options, long msgSeqNum, ReadOnlySpan<byte> text)
    {
        if (text.IsEmpty)
        {
            int empty = WriteMessage(buffer, options, msgSeqNum, "5"u8, ReadOnlySpan<byte>.Empty);
            return buffer[..empty];
        }

        if (text.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(text), text.Length, "Text do Logout aceita no máximo 128 bytes.");
        }

        if (text.IndexOf(Soh) >= 0 || text.IndexOf((byte)'=') >= 0)
        {
            throw new ArgumentException("Text do Logout não pode conter SOH nem '='.", nameof(text));
        }

        Span<byte> extra = stackalloc byte[4 + 128 + 1];
        int length = AppendLiteral(extra, 0, "58="u8);
        length = AppendBytes(extra, length, text);
        length = AppendSoh(extra, length);
        int written = WriteMessage(buffer, options, msgSeqNum, "5"u8, extra[..length]);
        return buffer[..written];
    }

    /// <summary>
    /// Escreve um SequenceReset (MsgType=4) em <paramref name="buffer"/>.
    /// </summary>
    /// <param name="buffer">Destino. O slice retornado aponta para o início deste buffer.</param>
    /// <param name="options">BeginString e comp IDs.</param>
    /// <param name="msgSeqNum">MsgSeqNum (tag 34) desta mensagem. No GapFill é o início do buraco, não o próximo número da sessão.</param>
    /// <param name="newSeqNo">NewSeqNo (tag 36). O próximo MsgSeqNum que a sessão vai enviar.</param>
    /// <param name="gapFill">
    /// Quando <see langword="true"/>, GapFillFlag (tag 123) sai como Y, junto com PossDupFlag (43=Y) e OrigSendingTime (122).
    /// </param>
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

        Span<byte> extra = stackalloc byte[96];
        int length = 0;
        if (gapFill)
        {
            // O gap-fill substitui mensagens já enviadas: FIX exige PossDupFlag=Y e OrigSendingTime.
            length = AppendLiteral(extra, length, "43=Y\x01"u8);
            length = AppendLiteral(extra, length, "122="u8);
            length = AppendSendingTime(extra, length);
            length = AppendSoh(extra, length);
        }

        length = AppendLiteral(extra, length, gapFill ? "123=Y\x01"u8 : "123=N\x01"u8);
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

    private static int AppendInt(Span<byte> destination, int offset, int value)
    {
        if (!Utf8Formatter.TryFormat(value, destination[offset..], out int width))
        {
            throw new InvalidOperationException("O campo numérico não coube no Logon.");
        }

        return offset + width;
    }

    private static void ValidatePassword(ReadOnlySpan<byte> password)
    {
        for (int i = 0; i < password.Length; i++)
        {
            if (password[i] is > 127 or Soh)
            {
                throw new ArgumentException("A senha do Logon deve ser ASCII e não pode conter SOH.", nameof(password));
            }
        }
    }

    private static void ValidateLogonText(string text)
    {
        if (!Ascii.IsValid(text) || text.Contains('=') || text.Contains((char)Soh))
        {
            throw new ArgumentException("Text (58) deve ser ASCII e não pode conter SOH nem '='.", nameof(text));
        }
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
