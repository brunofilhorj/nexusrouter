using Microsoft.Extensions.Options;

namespace NexusRouter.Host.Configuration;

/// <summary>
/// Um destino de fan-out de uma sessão de ingresso.
/// </summary>
public sealed class SessionRouteOptions : IValidateOptions<SessionRouteOptions>
{
    /// <summary>
    /// <see cref="DispatcherOptions.Id"/> que recebe a mensagem desta sessão.
    /// </summary>
    public required string DispatcherId { get; init; }

    /// <summary>
    /// Confere o identificador do dispatcher.
    /// </summary>
    /// <param name="name">Nome das options no container. Pode ser nulo neste estágio.</param>
    /// <param name="options">Rota já lida da configuração.</param>
    /// <returns>Sucesso ou a lista de campos que impedem a subida.</returns>
    public ValidateOptionsResult Validate(string? name, SessionRouteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> failures = [];
        ConfigurationChecks.Add(failures, ConfigurationChecks.RoutingId(options.DispatcherId, nameof(options.DispatcherId)));
        return ConfigurationChecks.From(failures);
    }
}
