using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClientProbe.Telemetry;

/// <summary>JSON options for the telemetry socket: camelCase names, lowercase transport, no nulls.</summary>
public static class ProbeJson
{
    /// <summary>Shared, read-only serializer options.</summary>
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
