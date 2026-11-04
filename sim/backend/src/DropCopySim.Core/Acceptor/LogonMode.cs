namespace DropCopySim.Core.Acceptor;

/// <summary>
/// Configured acceptor response when a client Logon is received.
/// Used for fault-injection scenarios without changing session code paths.
/// </summary>
public enum LogonMode
{
    /// <summary>Accept the Logon and proceed to Active.</summary>
    Success = 0,

    /// <summary>Reject Logon as invalid credentials (Logout / session reject path).</summary>
    InvalidCredentials = 1,

    /// <summary>Reject Logon as not authorized.</summary>
    NotAuthorized = 2,
}
