using DropCopySim.Core.Fix;

namespace DropCopySim.Core.Generation;

/// <summary>
/// Builds B3 EntryPoint FIX 4.4 OrderCancelReject frames (MsgType 9).
/// </summary>
public sealed class OrderCancelRejectFactory : IFixMessageFactory
{
    private const byte Soh = 0x01;
    private const char DefaultCxlRejResponseTo = '1';
    private const int DefaultCxlRejReason = 0;

    private readonly string _senderCompId;
    private readonly string _targetCompId;
    private readonly bool _testMessageIndicator;

    /// <summary>
    /// Creates an OrderCancelReject factory for the configured FIX session.
    /// </summary>
    /// <param name="senderCompId">SenderCompID for the simulated B3 side.</param>
    /// <param name="targetCompId">TargetCompID for the connected client.</param>
    /// <param name="testMessageIndicator">
    /// Whether to include <c>TestMessageIndicator (464)=Y</c> in generated rejects.
    /// </param>
    /// <exception cref="ArgumentException">A CompID is empty or contains invalid FIX field data.</exception>
    public OrderCancelRejectFactory(
        string senderCompId,
        string targetCompId,
        bool testMessageIndicator = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(senderCompId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetCompId);
        ValidateAsciiFieldValue(senderCompId, nameof(senderCompId));
        ValidateAsciiFieldValue(targetCompId, nameof(targetCompId));

        _senderCompId = senderCompId;
        _targetCompId = targetCompId;
        _testMessageIndicator = testMessageIndicator;
    }

    /// <summary>FIX MsgType for OrderCancelReject.</summary>
    public const string MsgType = "9";

    /// <inheritdoc />
    public string Name => "OrderCancelReject";

    /// <inheritdoc />
    /// <remarks>
    /// Writes <c>OrderID (37)</c>, <c>ClOrdID (11)</c>, <c>OrigClOrdID (41)</c>,
    /// <c>OrdStatus (39)</c>, <c>CxlRejResponseTo (434)</c>, and <c>CxlRejReason (102)</c>.
    /// Missing optional context values use <c>ClOrdID</c>, <c>1</c> (cancel request), and <c>0</c>
    /// (too late to cancel).
    /// </remarks>
    /// <exception cref="ArgumentException">The order context is incomplete or a field value is invalid.</exception>
    /// <exception cref="InvalidOperationException">The destination is too small.</exception>
    public int Build(Span<byte> destination, in OrderContext ctx, long seqNum, long businessSeqNum)
    {
        ValidateContext(ctx);
        string origClOrdId = ctx.OrigClOrdId ?? ctx.ClOrdId;
        ValidateClOrdId(origClOrdId, nameof(ctx.OrigClOrdId));
        char responseTo = ctx.CxlRejResponseTo == '\0' ? DefaultCxlRejResponseTo : ctx.CxlRejResponseTo;
        if (responseTo is not ('1' or '2'))
        {
            throw new ArgumentException("CxlRejResponseTo must be 1 or 2.", nameof(ctx));
        }

        int reason = ctx.CxlRejReason ?? DefaultCxlRejReason;
        if (reason < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ctx), "CxlRejReason cannot be negative.");
        }

        var writer = new B3FixMessageWriter(destination);
        writer.WriteB3Header(MsgType, _senderCompId, _targetCompId, seqNum, DateTime.UtcNow, businessSeqNum);
        if (_testMessageIndicator)
        {
            writer.WriteField(FixTags.TestMessageIndicator, "Y"u8);
        }

        writer.WriteField(FixTags.OrderId, ctx.OrderId);
        writer.WriteField(FixTags.ClOrdId, ctx.ClOrdId);
        writer.WriteField(FixTags.OrigClOrdId, origClOrdId);
        WriteCharField(ref writer, FixTags.OrdStatus, ctx.OrdStatus);
        WriteCharField(ref writer, FixTags.CxlRejResponseTo, responseTo);
        writer.WriteField(FixTags.CxlRejReason, reason);
        return writer.Finish();
    }

    private static void ValidateContext(in OrderContext ctx)
    {
        ValidateAsciiFieldValue(ctx.OrderId, nameof(ctx.OrderId));
        ValidateClOrdId(ctx.ClOrdId, nameof(ctx.ClOrdId));
        if (ctx.OrdStatus == '\0')
        {
            throw new ArgumentException("OrdStatus is required.", nameof(ctx));
        }
    }

    private static void ValidateClOrdId(string value, string paramName)
    {
        ValidateAsciiFieldValue(value, paramName);
        if (value.Length > 20)
        {
            throw new ArgumentException("ClOrdID cannot exceed 20 characters.", paramName);
        }

        for (int i = 0; i < value.Length; i++)
        {
            char character = value[i];
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))
            {
                throw new ArgumentException(
                    "ClOrdID may contain only alphanumeric characters, hyphens, and underscores.",
                    paramName);
            }
        }
    }

    private static void ValidateAsciiFieldValue(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] > 0x7F || value[i] == (char)Soh)
            {
                throw new ArgumentException("FIX field values must be ASCII and must not contain SOH.", paramName);
            }
        }
    }

    private static void WriteCharField(ref B3FixMessageWriter writer, int tag, char value)
    {
        if (value > 0x7F || value == '\0' || value == (char)Soh)
        {
            throw new ArgumentException($"FIX tag {tag} must be a non-empty ASCII character.", nameof(value));
        }

        Span<byte> formatted = stackalloc byte[1];
        formatted[0] = (byte)value;
        writer.WriteField(tag, formatted);
    }
}
