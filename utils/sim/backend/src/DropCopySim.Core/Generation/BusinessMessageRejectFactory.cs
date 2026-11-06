using DropCopySim.Core.Fix;

namespace DropCopySim.Core.Generation;

/// <summary>
/// Builds B3 EntryPoint FIX 4.4 BusinessMessageReject frames (MsgType j).
/// </summary>
public sealed class BusinessMessageRejectFactory : IFixMessageFactory
{
    private const byte Soh = 0x01;
    private const string DefaultRefMsgType = "D";
    private const int DefaultBusinessRejectReason = 0;
    private const string DefaultText = "Business message rejected";

    private readonly string _senderCompId;
    private readonly string _targetCompId;
    private readonly bool _testMessageIndicator;

    /// <summary>
    /// Creates a BusinessMessageReject factory for the configured FIX session.
    /// </summary>
    /// <param name="senderCompId">SenderCompID for the simulated B3 side.</param>
    /// <param name="targetCompId">TargetCompID for the connected client.</param>
    /// <param name="testMessageIndicator">
    /// Whether to include <c>TestMessageIndicator (464)=Y</c> in generated rejects.
    /// </param>
    /// <exception cref="ArgumentException">A CompID is empty or contains invalid FIX field data.</exception>
    public BusinessMessageRejectFactory(
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

    /// <summary>FIX MsgType for BusinessMessageReject.</summary>
    public const string MsgType = "j";

    /// <inheritdoc />
    public string Name => "BusinessMessageReject";

    /// <inheritdoc />
    /// <remarks>
    /// Writes <c>RefMsgType (372)</c>, <c>BusinessRejectReason (380)</c>, and <c>Text (58)</c>.
    /// <c>SanityValidation (10031)</c> is written only when
    /// <see cref="OrderContext.SanityValidation"/> is <c>Y</c> or <c>N</c>
    /// (<c>.kiro/steering/b3-guidelines.md</c> §10).
    /// Missing context values default to MsgType <c>D</c>, reason <c>0</c>, and a stable text.
    /// </remarks>
    /// <exception cref="ArgumentException">A field value is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="OrderContext.BusinessRejectReason"/> is negative.</exception>
    /// <exception cref="InvalidOperationException">The destination is too small.</exception>
    public int Build(Span<byte> destination, in OrderContext ctx, long seqNum, long businessSeqNum)
    {
        string refMsgType = ctx.RefMsgType ?? DefaultRefMsgType;
        ValidateAsciiFieldValue(refMsgType, nameof(ctx.RefMsgType));

        int reason = ctx.BusinessRejectReason ?? DefaultBusinessRejectReason;
        if (reason < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ctx), "BusinessRejectReason cannot be negative.");
        }

        string text = ctx.Text ?? DefaultText;
        ValidateAsciiFieldValue(text, nameof(ctx.Text));

        char? sanityValidation = ctx.SanityValidation;
        if (sanityValidation is char flag && flag is not ('Y' or 'N'))
        {
            throw new ArgumentException("SanityValidation must be Y or N.", nameof(ctx));
        }

        var writer = new B3FixMessageWriter(destination);
        writer.WriteB3Header(MsgType, _senderCompId, _targetCompId, seqNum, DateTime.UtcNow, businessSeqNum);
        if (_testMessageIndicator)
        {
            writer.WriteField(FixTags.TestMessageIndicator, "Y"u8);
        }

        writer.WriteField(FixTags.RefMsgType, refMsgType);
        writer.WriteField(FixTags.BusinessRejectReason, reason);
        writer.WriteField(FixTags.Text, text);
        if (sanityValidation is char sanityFlag)
        {
            WriteCharField(ref writer, FixTags.SanityValidation, sanityFlag);
        }

        return writer.Finish();
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
