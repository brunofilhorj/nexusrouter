namespace ClientProbe.Probe;

/// <summary>Connection profile for one transport. Each transport has its own sealed subtype.</summary>
public abstract record ProbeProfile
{
    /// <summary>Transport this profile belongs to.</summary>
    public abstract ProbeTransport Transport { get; }

    /// <summary>Effective endpoint shown in the state snapshot. Never contains credentials.</summary>
    public abstract string Endpoint { get; }
}
