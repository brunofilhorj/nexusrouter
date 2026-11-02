using Microsoft.Extensions.Options;

using NexusRouter.Domain.Enums;

namespace NexusRouter.Host.Configuration;

/// <summary>
/// Uma sessão FIX de ingresso: para onde conecta, como interpreta o frame e para quem entrega.
/// </summary>
public sealed class SessionOptions : IValidateOptions<SessionOptions>
{
    /// <summary>
    /// Identificador lógico da sessão. Também é a chave do contexto de rota.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Host ou endereço do gateway. Não é um IP obrigatório: o DNS do gateway é aceito.
    /// </summary>
    public required string Host { get; init; }

    /// <summary>
    /// Porta TCP do gateway.
    /// </summary>
    public int Port { get; init; }

    /// <summary>
    /// SenderCompID (tag 49) da sessão.
    /// </summary>
    public required string SenderCompId { get; init; }

    /// <summary>
    /// TargetCompID (tag 56) da sessão.
    /// </summary>
    public required string TargetCompId { get; init; }

    /// <summary>
    /// HeartBtInt (tag 108), em segundos.
    /// </summary>
    public int HeartbeatIntervalSeconds { get; init; } = 30;

    /// <summary>
    /// <see cref="DeliveryMode.PassThrough"/> encaminha o frame; <see cref="DeliveryMode.Parse"/> tokeniza antes do fan-out.
    /// </summary>
    public DeliveryMode DeliveryMode { get; init; } = DeliveryMode.PassThrough;

    /// <summary>
    /// Decoder de framing desta sessão.
    /// </summary>
    public FrameDecoderKind FrameDecoder { get; init; } = FrameDecoderKind.Fix44;

    /// <summary>
    /// TLS do socket de ingresso. Desligado por omissão.
    /// </summary>
    public TlsOptions Tls { get; init; } = new();

    /// <summary>
    /// Backoff usado quando a sessão cai.
    /// </summary>
    public ReconnectPolicyOptions ReconnectPolicy { get; init; } = new();

    /// <summary>
    /// Dispatchers que recebem cada mensagem desta sessão.
    /// </summary>
    public List<SessionRouteOptions> Routes { get; init; } = [];

    /// <summary>
    /// Confere endereço, identidade FIX, TLS, backoff e rotas.
    /// </summary>
    /// <param name="name">Nome das options no container. Pode ser nulo neste estágio.</param>
    /// <param name="options">Sessão já lida da configuração.</param>
    /// <returns>Sucesso ou a lista de campos que impedem a subida.</returns>
    public ValidateOptionsResult Validate(string? name, SessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> failures = [];
        ConfigurationChecks.Add(failures, ConfigurationChecks.RoutingId(options.SessionId, nameof(options.SessionId)));
        ConfigurationChecks.Add(failures, ConfigurationChecks.Host(options.Host, nameof(options.Host)));
        ConfigurationChecks.Add(failures, ConfigurationChecks.Port(options.Port, nameof(options.Port)));
        ConfigurationChecks.Add(failures, ConfigurationChecks.FixToken(options.SenderCompId, nameof(options.SenderCompId), required: true));
        ConfigurationChecks.Add(failures, ConfigurationChecks.FixToken(options.TargetCompId, nameof(options.TargetCompId), required: true));
        ConfigurationChecks.Add(
            failures,
            ConfigurationChecks.Positive(options.HeartbeatIntervalSeconds, nameof(options.HeartbeatIntervalSeconds)));
        ConfigurationChecks.Add(failures, ConfigurationChecks.Defined(options.DeliveryMode, nameof(options.DeliveryMode)));
        ConfigurationChecks.Add(failures, ConfigurationChecks.Defined(options.FrameDecoder, nameof(options.FrameDecoder)));
        ConfigurationChecks.AppendTls(failures, options.Tls, requireCertificateWhenEnabled: false);

        if (options.ReconnectPolicy is null)
        {
            failures.Add("ReconnectPolicy é obrigatório.");
        }
        else
        {
            ConfigurationChecks.Append(failures, nameof(options.ReconnectPolicy), options.ReconnectPolicy.Validate(null, options.ReconnectPolicy));
        }

        AddRoutes(options.Routes, failures);
        return ConfigurationChecks.From(failures);
    }

    private static void AddRoutes(List<SessionRouteOptions>? routes, List<string> failures)
    {
        if (routes is null || routes.Count == 0)
        {
            failures.Add("Routes: informe ao menos um dispatcher.");
            return;
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        for (int i = 0; i < routes.Count; i++)
        {
            SessionRouteOptions? route = routes[i];
            if (route is null)
            {
                failures.Add($"Routes[{i}] é obrigatório.");
                continue;
            }

            ConfigurationChecks.Append(failures, $"Routes[{i}]", route.Validate(null, route));
            if (route.DispatcherId is not null && !seen.Add(route.DispatcherId))
            {
                failures.Add($"Routes[{i}].DispatcherId está repetido na sessão.");
            }
        }
    }
}
