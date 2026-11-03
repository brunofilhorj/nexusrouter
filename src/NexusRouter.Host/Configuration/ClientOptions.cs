using Microsoft.Extensions.Options;

namespace NexusRouter.Host.Configuration;

/// <summary>
/// Cadastro opcional de um cliente que pode pedir retenção. Sem esta lista não há retransmissão.
/// </summary>
/// <remarks>
/// O token de WebSocket não entra aqui. <see cref="TokenEnvVar"/> é só o nome da variável de ambiente.
/// </remarks>
public sealed class ClientOptions : IValidateOptions<ClientOptions>
{
    /// <summary>
    /// UUID do cliente. É a chave da escolha diária de retenção.
    /// </summary>
    public required string ClientId { get; init; }

    /// <summary>
    /// Dispatcher ao qual este cliente pertence.
    /// </summary>
    public required string DispatcherId { get; init; }

    /// <summary>
    /// SenderCompID (tag 49) do Logon FIX. Obrigatório quando o dispatcher é <see cref="DispatcherType.FixOutbound"/>.
    /// </summary>
    public string? FixSenderCompId { get; init; }

    /// <summary>
    /// Nome da variável com o token do Logon WebSocket. Obrigatório quando o dispatcher é <see cref="DispatcherType.WebSocket"/>.
    /// </summary>
    public string? TokenEnvVar { get; init; }

    /// <summary>
    /// Confere o UUID, o dispatcher e o formato dos campos opcionais.
    /// </summary>
    /// <param name="name">Nome das options no container. Pode ser nulo neste estágio.</param>
    /// <param name="options">Cliente já lido da configuração.</param>
    /// <returns>Sucesso ou a lista de campos que impedem a subida.</returns>
    /// <remarks>
    /// A obrigação de <see cref="FixSenderCompId"/> ou <see cref="TokenEnvVar"/> depende do tipo do dispatcher
    /// e é conferida na raiz, que enxerga a lista inteira.
    /// </remarks>
    public ValidateOptionsResult Validate(string? name, ClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> failures = [];
        ConfigurationChecks.Add(failures, ConfigurationChecks.GuidValue(options.ClientId, nameof(options.ClientId)));
        ConfigurationChecks.Add(failures, ConfigurationChecks.RoutingId(options.DispatcherId, nameof(options.DispatcherId)));
        ConfigurationChecks.Add(
            failures,
            ConfigurationChecks.FixToken(options.FixSenderCompId, nameof(options.FixSenderCompId), required: false));
        ConfigurationChecks.Add(
            failures,
            ConfigurationChecks.EnvVar(options.TokenEnvVar, nameof(options.TokenEnvVar), required: false));
        return ConfigurationChecks.From(failures);
    }
}
