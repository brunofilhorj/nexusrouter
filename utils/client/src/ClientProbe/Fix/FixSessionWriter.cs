using System.Globalization;
using System.Text;

namespace ClientProbe.Fix;

/// <summary>
/// Builds the FIX 4.4 session messages the probe sends (Logon, Heartbeat, Logout), with
/// <c>BodyLength</c> and <c>CheckSum</c> computed per the FIX 4.4 rule. The sequence number is a parameter; nothing is stored.
/// </summary>
public sealed class FixSessionWriter
{
    private const char Soh = '\u0001';
    private readonly string _senderCompId;
    private readonly string _targetCompId;
    private readonly TimeProvider _time;

    /// <summary>Creates the writer.</summary>
    /// <param name="senderCompId">SenderCompID (tag 49).</param>
    /// <param name="targetCompId">TargetCompID (tag 56).</param>
    /// <param name="time">Clock used for SendingTime (tag 52).</param>
    public FixSessionWriter(string senderCompId, string targetCompId, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrEmpty(senderCompId);
        ArgumentException.ThrowIfNullOrEmpty(targetCompId);
        ArgumentNullException.ThrowIfNull(time);
        _senderCompId = senderCompId;
        _targetCompId = targetCompId;
        _time = time;
    }

    /// <summary>Writes a Logon (35=A) with EncryptMethod=0 and the given HeartBtInt.</summary>
    /// <param name="msgSeqNum">MsgSeqNum (tag 34).</param>
    /// <param name="heartBtInt">Heartbeat interval, in seconds.</param>
    /// <returns>The complete frame.</returns>
    public byte[] Logon(int msgSeqNum, int heartBtInt) => Logon(msgSeqNum, heartBtInt, resetSeqNum: false, nextExpectedMsgSeqNum: null);

    /// <summary>
    /// Writes a Logon (35=A) with EncryptMethod=0, the given HeartBtInt and the recovery tags.
    /// </summary>
    /// <param name="msgSeqNum">MsgSeqNum (tag 34). Must be 1 when <paramref name="resetSeqNum"/> is set.</param>
    /// <param name="heartBtInt">Heartbeat interval, in seconds.</param>
    /// <param name="resetSeqNum">Adds ResetSeqNumFlag 141=Y.</param>
    /// <param name="nextExpectedMsgSeqNum">Adds NextExpectedMsgSeqNum 789 when not null.</param>
    /// <param name="replayMode">Adds ReplayMode 19001 when not null (<c>1</c> = FromLogon, <c>2</c> = StartOfDay).</param>
    /// <returns>The complete frame.</returns>
    public byte[] Logon(int msgSeqNum, int heartBtInt, bool resetSeqNum, int? nextExpectedMsgSeqNum, int? replayMode = null)
    {
        var extra = $"98=0{Soh}108={heartBtInt.ToString(CultureInfo.InvariantCulture)}{Soh}";
        if (resetSeqNum)
        {
            extra += $"141=Y{Soh}";
        }

        if (nextExpectedMsgSeqNum is int next)
        {
            extra += $"789={next.ToString(CultureInfo.InvariantCulture)}{Soh}";
        }

        if (replayMode is int mode)
        {
            extra += $"19001={mode.ToString(CultureInfo.InvariantCulture)}{Soh}";
        }

        return Build("A", msgSeqNum, extra);
    }

    /// <summary>Writes a ResendRequest (35=2).</summary>
    /// <param name="msgSeqNum">MsgSeqNum (tag 34).</param>
    /// <param name="beginSeqNo">BeginSeqNo (tag 7).</param>
    /// <param name="endSeqNo">EndSeqNo (tag 16). 0 means "up to the latest".</param>
    /// <returns>The complete frame.</returns>
    public byte[] ResendRequest(int msgSeqNum, int beginSeqNo, int endSeqNo) =>
        Build("2", msgSeqNum, $"7={beginSeqNo.ToString(CultureInfo.InvariantCulture)}{Soh}16={endSeqNo.ToString(CultureInfo.InvariantCulture)}{Soh}");

    /// <summary>
    /// Writes a SequenceReset-GapFill (35=4, 123=Y) with PossDupFlag=Y. The probe sends only session
    /// messages, so it answers any ResendRequest with a single gap fill.
    /// </summary>
    /// <param name="msgSeqNum">MsgSeqNum (tag 34): the BeginSeqNo of the request being answered.</param>
    /// <param name="newSeqNo">NewSeqNo (tag 36): the probe's next outgoing MsgSeqNum.</param>
    /// <returns>The complete frame.</returns>
    public byte[] GapFill(int msgSeqNum, int newSeqNo)
    {
        var now = SendingTime();
        return Build("4", msgSeqNum, $"43=Y{Soh}122={now}{Soh}123=Y{Soh}36={newSeqNo.ToString(CultureInfo.InvariantCulture)}{Soh}");
    }

    /// <summary>Writes a Heartbeat (35=0).</summary>
    /// <param name="msgSeqNum">MsgSeqNum (tag 34).</param>
    /// <param name="testReqId">Optional TestReqID (tag 112) when answering a TestRequest.</param>
    /// <returns>The complete frame.</returns>
    public byte[] Heartbeat(int msgSeqNum, string? testReqId = null) =>
        Build("0", msgSeqNum, string.IsNullOrEmpty(testReqId) ? "" : $"112={testReqId}{Soh}");

    /// <summary>Writes a Logout (35=5).</summary>
    /// <param name="msgSeqNum">MsgSeqNum (tag 34).</param>
    /// <param name="text">Optional Text (tag 58).</param>
    /// <returns>The complete frame.</returns>
    public byte[] Logout(int msgSeqNum, string? text = null) =>
        Build("5", msgSeqNum, string.IsNullOrEmpty(text) ? "" : $"58={text}{Soh}");

    /// <summary>Computes the FIX checksum: sum of bytes mod 256.</summary>
    /// <param name="data">Bytes up to, not including, <c>10=</c>.</param>
    /// <returns>The checksum value.</returns>
    public static int ComputeCheckSum(ReadOnlySpan<byte> data)
    {
        var sum = 0;
        foreach (var b in data)
        {
            sum += b;
        }
        return sum % 256;
    }

    private string SendingTime() =>
        _time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd-HH:mm:ss.fff", CultureInfo.InvariantCulture);

    private byte[] Build(string msgType, int msgSeqNum, string extra)
    {
        var sendingTime = SendingTime();
        var body = $"35={msgType}{Soh}49={_senderCompId}{Soh}56={_targetCompId}{Soh}" +
                   $"34={msgSeqNum.ToString(CultureInfo.InvariantCulture)}{Soh}52={sendingTime}{Soh}{extra}";
        var bodyBytes = Encoding.ASCII.GetBytes(body);
        var head = Encoding.ASCII.GetBytes($"8=FIX.4.4{Soh}9={bodyBytes.Length.ToString(CultureInfo.InvariantCulture)}{Soh}");
        var withoutTrailer = new byte[head.Length + bodyBytes.Length];
        head.CopyTo(withoutTrailer, 0);
        bodyBytes.CopyTo(withoutTrailer, head.Length);
        var trailer = Encoding.ASCII.GetBytes($"10={ComputeCheckSum(withoutTrailer).ToString("D3", CultureInfo.InvariantCulture)}{Soh}");
        var frame = new byte[withoutTrailer.Length + trailer.Length];
        withoutTrailer.CopyTo(frame, 0);
        trailer.CopyTo(frame, withoutTrailer.Length);
        return frame;
    }
}
