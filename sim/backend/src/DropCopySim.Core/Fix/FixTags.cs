namespace DropCopySim.Core.Fix;

/// <summary>
/// FIX 4.4 and B3 EntryPoint tag numbers used by the DropCopy simulator.
/// </summary>
/// <remarks>
/// Standard tags follow FIX 4.4. B3-specific tags are defined in
/// <c>docs/FIX44Entrypoint.xml</c> and <c>.kiro/steering/b3-guidelines.md</c>.
/// </remarks>
public static class FixTags
{
    /// <summary>BeginString (tag 8).</summary>
    public const int BeginString = 8;

    /// <summary>BodyLength (tag 9).</summary>
    public const int BodyLength = 9;

    /// <summary>CheckSum (tag 10).</summary>
    public const int CheckSum = 10;

    /// <summary>Text (tag 58).</summary>
    public const int Text = 58;

    /// <summary>Account (tag 1).</summary>
    public const int Account = 1;

    /// <summary>Average price (tag 6).</summary>
    public const int AvgPx = 6;

    /// <summary>Client order identifier (tag 11).</summary>
    public const int ClOrdId = 11;

    /// <summary>Cumulative quantity (tag 14).</summary>
    public const int CumQty = 14;

    /// <summary>Execution identifier (tag 17).</summary>
    public const int ExecId = 17;

    /// <summary>SecurityIDSource (tag 22).</summary>
    public const int SecurityIdSource = 22;

    /// <summary>Last price (tag 31).</summary>
    public const int LastPx = 31;

    /// <summary>Last quantity (tag 32).</summary>
    public const int LastQty = 32;

    /// <summary>Order identifier (tag 37).</summary>
    public const int OrderId = 37;

    /// <summary>Side (tag 54).</summary>
    public const int Side = 54;

    /// <summary>TransactTime (tag 60).</summary>
    public const int TransactTime = 60;

    /// <summary>SecurityID (tag 48).</summary>
    public const int SecurityId = 48;

    /// <summary>Symbol (tag 55).</summary>
    public const int Symbol = 55;

    /// <summary>SecurityExchange (tag 207).</summary>
    public const int SecurityExchange = 207;

    /// <summary>Leaves quantity (tag 151).</summary>
    public const int LeavesQty = 151;

    /// <summary>Execution type (tag 150).</summary>
    public const int ExecType = 150;

    /// <summary>Order status (tag 39).</summary>
    public const int OrdStatus = 39;

    /// <summary>MsgSeqNum (tag 34).</summary>
    public const int MsgSeqNum = 34;

    /// <summary>NewSeqNo (tag 36).</summary>
    public const int NewSeqNo = 36;

    /// <summary>MsgType (tag 35).</summary>
    public const int MsgType = 35;

    /// <summary>BeginSeqNo (tag 7).</summary>
    public const int BeginSeqNo = 7;

    /// <summary>EndSeqNo (tag 16).</summary>
    public const int EndSeqNo = 16;

    /// <summary>GapFillFlag (tag 123).</summary>
    public const int GapFillFlag = 123;

    /// <summary>SenderCompID (tag 49).</summary>
    public const int SenderCompId = 49;

    /// <summary>SendingTime (tag 52).</summary>
    public const int SendingTime = 52;

    /// <summary>TargetCompID (tag 56).</summary>
    public const int TargetCompId = 56;

    /// <summary>PartyIDSource (tag 447).</summary>
    public const int PartyIdSource = 447;

    /// <summary>PartyID (tag 448).</summary>
    public const int PartyId = 448;

    /// <summary>PartyRole (tag 452).</summary>
    public const int PartyRole = 452;

    /// <summary>NoPartyIDs (tag 453).</summary>
    public const int NoPartyIds = 453;

    /// <summary>
    /// <c>PartyRole</c> (tag 452) value for Client ID.
    /// See <c>.kiro/steering/b3-guidelines.md</c> §9.
    /// </summary>
    public const int PartyRoleClientId = 3;

    /// <summary>
    /// <c>PartyRole</c> (tag 452) value for Clearing Firm. Required on B3 equities.
    /// See <c>.kiro/steering/b3-guidelines.md</c> §9.
    /// </summary>
    public const int PartyRoleClearingFirm = 4;

    /// <summary>EncryptMethod (tag 98).</summary>
    public const int EncryptMethod = 98;

    /// <summary>HeartBtInt (tag 108).</summary>
    public const int HeartBtInt = 108;

    /// <summary>TestReqID (tag 112).</summary>
    public const int TestReqId = 112;

    /// <summary>ResetSeqNumFlag (tag 141).</summary>
    public const int ResetSeqNumFlag = 141;

    /// <summary>TestMessageIndicator (tag 464) — B3 Logon / session indicator.</summary>
    public const int TestMessageIndicator = 464;

    /// <summary>Username (tag 553).</summary>
    public const int Username = 553;

    /// <summary>Password (tag 554).</summary>
    public const int Password = 554;

    /// <summary>ApplicationLogonRspCode (tag 6930) — B3.</summary>
    public const int ApplicationLogonRspCode = 6930;

    /// <summary>MMProtectionReset (tag 9773) — B3.</summary>
    public const int MmProtectionReset = 9773;

    /// <summary>BusinessSeqNum (tag 10016) — B3 business-level sequence.</summary>
    public const int BusinessSeqNum = 10016;

    /// <summary>BTSFinalTxOrdStatus (tag 10029) — B3.</summary>
    public const int BtsFinalTxOrdStatus = 10029;

    /// <summary>AdministrativeMessage (tag 35020) — B3.</summary>
    public const int AdministrativeMessage = 35020;

    /// <summary>ProducerID (tag 35036) — B3.</summary>
    public const int ProducerId = 35036;
}
