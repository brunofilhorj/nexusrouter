namespace NexusRouter.Egress.Fix;

/// <summary>
/// Defines which messages a newly opened client epoch can receive.
/// </summary>
public enum ReplayMode
{
    /// <summary>
    /// Deliver only messages arriving after the Logon.
    /// </summary>
    FromLogon,

    /// <summary>
    /// Deliver all messages from the trading day.
    /// </summary>
    StartOfDay,
}
