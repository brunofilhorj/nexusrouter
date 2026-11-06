namespace ClientProbe.Probe;

/// <summary>Raised when a transport name or profile is invalid. Maps to HTTP 400.</summary>
public sealed class ProbeValidationException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="code">Stable error code.</param>
    /// <param name="message">Operator-facing explanation.</param>
    public ProbeValidationException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>Stable error code returned in the response body.</summary>
    public string Code { get; }
}
