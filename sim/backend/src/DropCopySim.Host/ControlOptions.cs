using System.ComponentModel.DataAnnotations;

namespace DropCopySim.Host;

/// <summary>
/// Control-tier HTTP options for the Minimal API and telemetry WebSocket.
/// Bound from configuration section <c>DropCopySim:Control</c>.
/// </summary>
public sealed record ControlOptions
{
    /// <summary>Configuration section name under <c>DropCopySim</c>.</summary>
    public const string SectionName = "Control";

    /// <summary>TCP port the control-plane HTTP/WebSocket endpoints listen on (local default: 8099).</summary>
    [Range(1, 65535)]
    public int HttpPort { get; init; } = 8099;

    /// <summary>Vite dev-server origin used when <see cref="AllowedOrigins"/> is left unset.</summary>
    public const string DefaultViteOrigin = "http://localhost:5173";

    /// <summary>
    /// Browser origins allowed by CORS for the SimPanel frontend. When empty, the host falls back to
    /// <see cref="DefaultViteOrigin"/>. Left empty by default so configuration binding fully replaces it.
    /// </summary>
    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];

    /// <summary>
    /// Name of the environment variable holding the control-API bearer token. When set and the variable
    /// has a value, the API requires <c>Authorization: Bearer &lt;token&gt;</c>; empty or absent means open
    /// lab mode. The token value itself is never stored in configuration.
    /// </summary>
    public string AuthTokenEnvVar { get; init; } = "SIM_CONTROL_TOKEN";
}
