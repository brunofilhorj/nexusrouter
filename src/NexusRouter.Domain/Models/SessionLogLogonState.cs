namespace NexusRouter.Domain.Models;

/// <summary>
/// Sequence state to use when an initiator opens a FIX connection.
/// </summary>
/// <param name="NextOut">Next outbound sequence number.</param>
/// <param name="ResetSeqNumFlag">Whether the Logon must carry <c>141=Y</c>.</param>
public readonly record struct SessionLogLogonState(long NextOut, bool ResetSeqNumFlag);
