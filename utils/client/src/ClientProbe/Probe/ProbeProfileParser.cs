using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ClientProbe.Probe;

/// <summary>Turns the JSON body of <c>POST /v1/connect</c> into a validated <see cref="ProbeProfile"/>.</summary>
public sealed class ProbeProfileParser
{
    private readonly ClientProbeOptions _defaults;

    /// <summary>Creates the parser. Missing profile fields fall back to the configured defaults.</summary>
    /// <param name="options">Probe options.</param>
    public ProbeProfileParser(IOptions<ClientProbeOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _defaults = options.Value;
    }

    /// <summary>Parses a transport name: <c>kafka</c>, <c>rabbit</c> or <c>fix</c>, case-insensitive.</summary>
    /// <param name="value">Transport name from the request.</param>
    /// <returns>The transport.</returns>
    /// <exception cref="ProbeValidationException">The name is missing or unknown.</exception>
    public static ProbeTransport ParseTransport(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && !int.TryParse(value, out _)
            && Enum.TryParse<ProbeTransport>(value.Trim(), ignoreCase: true, out var transport))
        {
            return transport;
        }

        throw new ProbeValidationException("invalid_transport", "Transport must be one of: kafka, rabbit, fix.");
    }

    /// <summary>Builds the profile for a transport.</summary>
    /// <param name="transport">Transport the profile is for.</param>
    /// <param name="profile">The <c>profile</c> object from the request; null or absent means all defaults.</param>
    /// <returns>A validated profile.</returns>
    /// <exception cref="ProbeValidationException">A field has the wrong type or an invalid value.</exception>
    public ProbeProfile Parse(ProbeTransport transport, JsonElement? profile)
    {
        var source = profile is { ValueKind: JsonValueKind.Object } element ? element : (JsonElement?)null;
        if (profile is { ValueKind: not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined) })
        {
            throw Invalid("profile must be a JSON object.");
        }

        return transport switch
        {
            ProbeTransport.Kafka => ParseKafka(source),
            ProbeTransport.Rabbit => ParseRabbit(source),
            ProbeTransport.Fix => ParseFix(source),
            _ => throw new ProbeValidationException("invalid_transport", "Transport must be one of: kafka, rabbit, fix."),
        };
    }

    private KafkaProfile ParseKafka(JsonElement? source)
    {
        var d = _defaults.Kafka;
        var reset = Text(source, "autoOffsetReset", d.AutoOffsetReset);
        if (!reset.Equals("Latest", StringComparison.OrdinalIgnoreCase)
            && !reset.Equals("Earliest", StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid("autoOffsetReset must be Latest or Earliest.");
        }

        return new KafkaProfile(
            Text(source, "bootstrapServers", d.BootstrapServers),
            Text(source, "topic", d.Topic),
            Text(source, "groupId", d.GroupId),
            reset.Equals("Earliest", StringComparison.OrdinalIgnoreCase) ? "Earliest" : "Latest");
    }

    private RabbitProfile ParseRabbit(JsonElement? source)
    {
        var d = _defaults.Rabbit;
        var password = Optional(source, "password");
        var username = Optional(source, "username")?.Trim();
        return new RabbitProfile(
            Text(source, "host", d.Host),
            Port(source, "port", d.Port),
            Text(source, "virtualHost", d.VirtualHost),
            Text(source, "exchange", d.Exchange),
            Text(source, "routingKey", d.RoutingKey),
            Text(source, "usernameEnvVar", d.UsernameEnvVar),
            Text(source, "passwordEnvVar", d.PasswordEnvVar),
            string.IsNullOrEmpty(password) ? null : password,
            string.IsNullOrEmpty(username) ? null : username);
    }

    private FixProfile ParseFix(JsonElement? source)
    {
        var d = _defaults.Fix;
        var heartBtInt = Number(source, "heartBtInt", d.HeartBtInt);
        if (heartBtInt is < 1 or > 3600)
        {
            throw Invalid("heartBtInt must be between 1 and 3600 seconds.");
        }

        return new FixProfile(
            Text(source, "host", d.Host),
            Port(source, "port", d.Port),
            Text(source, "senderCompId", d.SenderCompId),
            Text(source, "targetCompId", d.TargetCompId),
            heartBtInt,
            Flag(source, "resetSeqNum", d.ResetSeqNum),
            Flag(source, "sendNextExpected", d.SendNextExpected));
    }

    private static bool Flag(JsonElement? source, string name, bool fallback)
    {
        if (source is not { } obj || !obj.TryGetProperty(name, out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => fallback,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            JsonValueKind.String when string.IsNullOrWhiteSpace(value.GetString()) => fallback,
            _ => throw Invalid($"{name} must be true or false."),
        };
    }

    private static string? Optional(JsonElement? source, string name)
    {
        if (source is not { } obj || !obj.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => throw Invalid($"{name} must be a string."),
        };
    }

    private static string Text(JsonElement? source, string name, string fallback)
    {
        var value = Optional(source, name);
        var effective = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return string.IsNullOrWhiteSpace(effective) ? throw Invalid($"{name} is required.") : effective;
    }

    private static int Number(JsonElement? source, string name, int fallback)
    {
        var value = Optional(source, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw Invalid($"{name} must be a whole number.");
    }

    private static int Port(JsonElement? source, string name, int fallback)
    {
        var port = Number(source, name, fallback);
        return port is < 1 or > 65535 ? throw Invalid($"{name} must be between 1 and 65535.") : port;
    }

    private static ProbeValidationException Invalid(string message) => new("invalid_profile", message);
}
