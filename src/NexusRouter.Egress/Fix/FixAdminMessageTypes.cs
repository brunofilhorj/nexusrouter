namespace NexusRouter.Egress.Fix;

/// <summary>
/// Tipos de mensagem de sessão (<c>msgcat="admin"</c> em <c>docs/FIX44Entrypoint.xml</c>).
/// </summary>
/// <remarks>
/// O dicionário da B3 marca como administrativas: Heartbeat (0), TestRequest (1), ResendRequest (2), Reject (3),
/// SequenceReset (4), Logout (5), Logon (A) e NetworkStatusResponse (BD). Elas não são reenviadas, são substituídas por gap-fill.
/// </remarks>
internal static class FixAdminMessageTypes
{
    /// <summary>
    /// Informa se o MsgType (tag 35) é administrativo.
    /// </summary>
    /// <param name="msgType">Valor da tag 35.</param>
    /// <returns><see langword="true"/> para os oito tipos administrativos do dicionário.</returns>
    /// <remarks>Zero allocation.</remarks>
    internal static bool IsAdmin(ReadOnlySpan<byte> msgType) =>
        msgType.Length switch
        {
            1 => msgType[0] is (byte)'0' or (byte)'1' or (byte)'2' or (byte)'3' or (byte)'4' or (byte)'5' or (byte)'A',
            2 => msgType[0] == (byte)'B' && msgType[1] == (byte)'D',
            _ => false,
        };
}
