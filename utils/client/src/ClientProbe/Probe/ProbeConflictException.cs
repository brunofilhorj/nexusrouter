namespace ClientProbe.Probe;

/// <summary>Raised when a connect arrives while the same transport is still connecting. Maps to HTTP 409.</summary>
public sealed class ProbeConflictException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">Operator-facing explanation.</param>
    public ProbeConflictException(string message)
        : base(message)
    {
    }
}
