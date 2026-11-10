using System.Text;

using NexusRouter.Ingress.Sessions;

namespace NexusRouter.Ingress.Tests;

public sealed class FixMessageBuilderTests
{
    private static readonly SessionOptions Options = new()
    {
        SessionId = "BUILDER-TEST",
        Host = "127.0.0.1",
        Port = 1,
        SenderCompId = "CLIENT",
        TargetCompId = "EXCHANGE",
        BeginString = "FIX.4.4",
    };

    [Fact]
    public void BuildLogon_WithCredentialsCodAndReset_WritesB3FieldsAndOmitsUsername()
    {
        byte[] buffer = new byte[512];
        var options = new SessionOptions
        {
            SessionId = Options.SessionId,
            Host = Options.Host,
            Port = Options.Port,
            SenderCompId = Options.SenderCompId,
            TargetCompId = Options.TargetCompId,
            BeginString = Options.BeginString,
            HeartBtInt = 30,
            LogonText = "NexusRouter",
            CancelOnDisconnectType = 3,
            CodTimeoutWindow = 30000,
            ResetSeqNumFlag = true,
        };

        int length = FixMessageBuilder.BuildLogon(buffer, options, msgSeqNum: 1, "s3cret"u8).Length;

        string message = Encoding.ASCII.GetString(buffer, 0, length);
        Assert.Contains("\u000198=0\u0001", message);
        Assert.Contains("\u0001108=30\u0001", message);
        Assert.Contains("\u0001141=Y\u0001", message);
        Assert.Contains("\u000158=NexusRouter\u0001", message);
        Assert.Contains("\u000195=6\u0001", message);
        Assert.Contains("\u000196=s3cret\u0001", message);
        Assert.Contains("\u000135002=3\u0001", message);
        Assert.Contains("\u000135003=30000\u0001", message);
        Assert.DoesNotContain("\u0001553=", message);
        Assert.DoesNotContain("\u0001554=", message);
        AssertValidBodyLengthAndChecksum(buffer.AsSpan(0, length));
    }

    [Fact]
    public void BuildLogon_WithoutOptionalFields_OmitsCredentialsCodAndReset()
    {
        byte[] buffer = new byte[512];

        int length = FixMessageBuilder.BuildLogon(buffer, Options, msgSeqNum: 1).Length;

        string message = Encoding.ASCII.GetString(buffer, 0, length);
        Assert.Contains("\u000198=0\u0001", message);
        Assert.Contains("\u0001108=20\u0001", message);
        Assert.DoesNotContain("\u0001141=", message);
        Assert.DoesNotContain("\u000158=", message);
        Assert.DoesNotContain("\u000195=", message);
        Assert.DoesNotContain("\u000196=", message);
        Assert.DoesNotContain("\u000135002=", message);
        Assert.DoesNotContain("\u000135003=", message);
        AssertValidBodyLengthAndChecksum(buffer.AsSpan(0, length));
    }

    [Fact]
    public void BuildSequenceReset_GapFill_WritesPossDupOrigSendingTimeAndNewSeqNo()
    {
        byte[] buffer = new byte[512];

        int length = FixMessageBuilder.BuildSequenceReset(buffer, Options, msgSeqNum: 3, newSeqNo: 9, gapFill: true).Length;

        string message = Encoding.ASCII.GetString(buffer, 0, length);
        Assert.Contains("\u000135=4\u0001", message);
        Assert.Contains("\u000134=3\u0001", message);
        Assert.Contains("\u000143=Y\u0001", message);
        Assert.Contains("\u0001122=", message);
        Assert.Contains("\u0001123=Y\u0001", message);
        Assert.Contains("\u000136=9\u0001", message);
        AssertValidBodyLengthAndChecksum(buffer.AsSpan(0, length));
    }

    [Fact]
    public void BuildSequenceReset_ResetWithoutGapFill_OmitsPossDupFlag()
    {
        byte[] buffer = new byte[512];

        int length = FixMessageBuilder.BuildSequenceReset(buffer, Options, msgSeqNum: 1, newSeqNo: 5, gapFill: false).Length;

        string message = Encoding.ASCII.GetString(buffer, 0, length);
        Assert.Contains("\u0001123=N\u0001", message);
        Assert.DoesNotContain("\u000143=", message);
        Assert.DoesNotContain("\u0001122=", message);
        AssertValidBodyLengthAndChecksum(buffer.AsSpan(0, length));
    }

    internal static void AssertValidBodyLengthAndChecksum(ReadOnlySpan<byte> frame)
    {
        string text = Encoding.ASCII.GetString(frame);
        int bodyStart = text.IndexOf("\u00019=", StringComparison.Ordinal) + 3;
        int bodyLengthEnd = text.IndexOf('\u0001', bodyStart);
        int declaredBodyLength = int.Parse(text.AsSpan(bodyStart, bodyLengthEnd - bodyStart));
        int checksumStart = text.LastIndexOf("\u000110=", StringComparison.Ordinal) + 1;

        Assert.Equal(checksumStart - (bodyLengthEnd + 1), declaredBodyLength);

        int sum = 0;
        for (int i = 0; i < checksumStart; i++)
        {
            sum += frame[i];
        }

        Assert.Equal($"{sum % 256:D3}", text.Substring(checksumStart + 3, 3));
    }
}
