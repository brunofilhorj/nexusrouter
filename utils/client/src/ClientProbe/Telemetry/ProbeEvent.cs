using ClientProbe.Probe;

namespace ClientProbe.Telemetry;

/// <summary>Short view of one FIX frame, as broadcast on <c>/v1/ws</c>.</summary>
/// <param name="Transport">Transport that carried the frame.</param>
/// <param name="ReceivedAt">Receive or send time, UTC.</param>
/// <param name="MsgType">Tag 35, or null when the frame has none (a parse failure).</param>
/// <param name="Fields">Tags 11, 55, 54, 38 and 39 when present. Absent tags are not listed.</param>
/// <param name="RawPreview">Truncated frame text with SOH rendered as <c>|</c>.</param>
/// <param name="Direction">Whether the probe sent the frame or received it.</param>
public sealed record ProbeEvent(
    ProbeTransport Transport,
    DateTimeOffset ReceivedAt,
    string? MsgType,
    IReadOnlyDictionary<string, string> Fields,
    string RawPreview,
    ProbeDirection Direction = ProbeDirection.In);
