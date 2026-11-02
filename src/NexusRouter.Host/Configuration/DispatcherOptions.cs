using Microsoft.Extensions.Options;

namespace NexusRouter.Host.Configuration;

/// <summary>
/// Um dispatcher da lista <c>Dispatchers</c>. O campo <c>Type</c> da configuração escolhe a classe concreta.
/// </summary>
public abstract class DispatcherOptions
{
    /// <summary>
    /// Nome estável citado por <see cref="SessionRouteOptions.DispatcherId"/> e pelo cadastro de clientes.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Discriminador fixo da classe concreta. A configuração não sobrescreve este valor: ela escolhe a classe.
    /// </summary>
    public abstract DispatcherType Type { get; }

    /// <summary>
    /// Confere os campos do dispatcher concreto.
    /// </summary>
    /// <param name="name">Nome das options no container. Pode ser nulo neste estágio.</param>
    /// <returns>Sucesso ou a lista de campos que impedem a subida.</returns>
    public abstract ValidateOptionsResult Validate(string? name);

    /// <summary>
    /// Confere o <see cref="Id"/> comum a Kafka, RabbitMQ, FIX e WebSocket.
    /// </summary>
    /// <param name="options">Dispatcher já lido da configuração.</param>
    /// <param name="failures">Lista em construção.</param>
    internal static void AddIdentity(DispatcherOptions options, List<string> failures)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(failures);
        ConfigurationChecks.Add(failures, ConfigurationChecks.RoutingId(options.Id, nameof(Id)));
    }
}
