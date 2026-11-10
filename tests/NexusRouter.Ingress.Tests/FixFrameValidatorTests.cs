using System.Text;

using NexusRouter.Ingress.Sessions;

namespace NexusRouter.Ingress.Tests;

public sealed class FixFrameValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly SessionOptions Options = new()
    {
        SessionId = "S",
        Host = "127.0.0.1",
        Port = 1,
        SenderCompId = "CLIENT",
        TargetCompId = "EXCHANGE",
        BeginString = "FIX.4.4",
        SendingTimeTolerance = TimeSpan.FromSeconds(120),
    };

    [Fact]
    public void Validate_WhenFrameIsWellFormed_ReturnsValid()
    {
        byte[] frame = Frame("35=0", "49=EXCHANGE", "56=CLIENT", "34=7", "52=20260101-12:00:30.123");

        Assert.True(FixFrameValidator.Validate(frame, Options, Now).IsValid);
    }

    [Fact]
    public void Validate_WhenSendingTimeHasMicroseconds_ReturnsValid()
    {
        byte[] frame = Frame("35=0", "49=EXCHANGE", "56=CLIENT", "34=7", "52=20260101-12:00:00.123456");

        Assert.True(FixFrameValidator.Validate(frame, Options, Now).IsValid);
    }

    [Fact]
    public void Validate_WhenBeginStringDiffers_RejectsTag8()
    {
        byte[] frame = Frame("35=0", "49=EXCHANGE", "56=CLIENT", "34=7", "52=20260101-12:00:00", beginString: "FIX.4.2");

        FixFrameValidation result = FixFrameValidator.Validate(frame, Options, Now);

        Assert.Equal(5, result.SessionRejectReason);
        Assert.Equal(8, result.RefTagId);
        Assert.Equal(7, result.RefSeqNum);
    }

    [Fact]
    public void Validate_WhenBodyLengthIsWrong_RejectsTag9()
    {
        byte[] frame = Frame("35=0", "49=EXCHANGE", "56=CLIENT", "34=7", "52=20260101-12:00:00", bodyLengthDelta: 1);

        FixFrameValidation result = FixFrameValidator.Validate(frame, Options, Now);

        Assert.Equal(5, result.SessionRejectReason);
        Assert.Equal(9, result.RefTagId);
    }

    [Fact]
    public void Validate_WhenChecksumIsWrong_RejectsTag10()
    {
        byte[] frame = Frame("35=0", "49=EXCHANGE", "56=CLIENT", "34=7", "52=20260101-12:00:00", checksumDelta: 1);

        FixFrameValidation result = FixFrameValidator.Validate(frame, Options, Now);

        Assert.Equal(5, result.SessionRejectReason);
        Assert.Equal(10, result.RefTagId);
    }

    [Theory]
    [InlineData("49=OTHER", "56=CLIENT", 49)]
    [InlineData("49=EXCHANGE", "56=OTHER", 56)]
    public void Validate_WhenCompIdDiffers_RejectsWithCompIdProblem(string sender, string target, int expectedTag)
    {
        byte[] frame = Frame("35=0", sender, target, "34=7", "52=20260101-12:00:00");

        FixFrameValidation result = FixFrameValidator.Validate(frame, Options, Now);

        Assert.Equal(9, result.SessionRejectReason);
        Assert.Equal(expectedTag, result.RefTagId);
    }

    [Fact]
    public void Validate_WhenSendingTimeIsMissing_RejectsAsRequiredTagMissing()
    {
        byte[] frame = Frame("35=0", "49=EXCHANGE", "56=CLIENT", "34=7");

        FixFrameValidation result = FixFrameValidator.Validate(frame, Options, Now);

        Assert.Equal(1, result.SessionRejectReason);
        Assert.Equal(52, result.RefTagId);
    }

    [Fact]
    public void Validate_WhenSendingTimeIsMalformed_RejectsAsIncorrectFormat()
    {
        byte[] frame = Frame("35=0", "49=EXCHANGE", "56=CLIENT", "34=7", "52=2026-01-01");

        FixFrameValidation result = FixFrameValidator.Validate(frame, Options, Now);

        Assert.Equal(6, result.SessionRejectReason);
        Assert.Equal(52, result.RefTagId);
    }

    [Theory]
    [InlineData("20260101-12:05:00")]
    [InlineData("20260101-11:55:00")]
    public void Validate_WhenSendingTimeIsOutsideTolerance_RejectsAsAccuracyProblem(string sendingTime)
    {
        byte[] frame = Frame("35=0", "49=EXCHANGE", "56=CLIENT", "34=7", $"52={sendingTime}");

        FixFrameValidation result = FixFrameValidator.Validate(frame, Options, Now);

        Assert.Equal(10, result.SessionRejectReason);
        Assert.Equal(52, result.RefTagId);
    }

    [Fact]
    public void Validate_WhenToleranceIsZero_SkipsSendingTimeWindow()
    {
        SessionOptions disabled = new()
        {
            SessionId = "S",
            Host = "127.0.0.1",
            Port = 1,
            SenderCompId = "CLIENT",
            TargetCompId = "EXCHANGE",
            BeginString = "FIX.4.4",
            SendingTimeTolerance = TimeSpan.Zero,
        };
        byte[] frame = Frame("35=0", "49=EXCHANGE", "56=CLIENT", "34=7", "52=20200101-00:00:00");

        Assert.True(FixFrameValidator.Validate(frame, disabled, Now).IsValid);
    }

    [Fact]
    public void BuildReject_WritesRefFieldsAndValidFraming()
    {
        byte[] buffer = new byte[512];

        Span<byte> reject = FixMessageBuilder.BuildReject(buffer, Options, 3, 7, 52, 10, "SendingTime (52) fora da tolerancia");

        string text = Encoding.ASCII.GetString(reject);
        Assert.Contains("\u000135=3\u0001", text);
        Assert.Contains("\u000145=7\u0001", text);
        Assert.Contains("\u0001371=52\u0001", text);
        Assert.Contains("\u0001373=10\u0001", text);
        Assert.Contains("\u000158=SendingTime (52) fora da tolerancia\u0001", text);
        FixMessageBuilderTests.AssertValidBodyLengthAndChecksum(reject.ToArray());
    }

    private static byte[] Frame(
        string messageType,
        string sender,
        string target,
        string sequence,
        string? sendingTime = null,
        string beginString = "FIX.4.4",
        int bodyLengthDelta = 0,
        int checksumDelta = 0)
    {
        var body = new StringBuilder();
        foreach (string field in new[] { messageType, sender, target, sequence, sendingTime ?? string.Empty })
        {
            if (field.Length > 0)
            {
                body.Append(field).Append('\u0001');
            }
        }

        string bodyText = body.ToString();
        string prefix = $"8={beginString}\u00019={bodyText.Length + bodyLengthDelta}\u0001{bodyText}";
        int checksum = 0;
        foreach (char ch in prefix)
        {
            checksum += ch;
        }

        checksum = (checksum + checksumDelta) & 0xFF;
        return Encoding.ASCII.GetBytes($"{prefix}10={checksum:D3}\u0001");
    }
}
