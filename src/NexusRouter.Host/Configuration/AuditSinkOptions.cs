using Microsoft.Extensions.Options;

namespace NexusRouter.Host.Configuration;

/// <summary>
/// Um sink da lista <c>Audit:Sinks</c>. O campo <c>Type</c> da configuração escolhe a classe concreta.
/// </summary>
public abstract class AuditSinkOptions
{
    /// <summary>
    /// Discriminador fixo da classe concreta. A configuração não sobrescreve este valor: ela escolhe a classe.
    /// </summary>
    public abstract AuditSinkType Type { get; }

    /// <summary>
    /// Confere os campos do sink concreto.
    /// </summary>
    /// <param name="name">Nome das options no container. Pode ser nulo neste estágio.</param>
    /// <returns>Sucesso ou a lista de campos que impedem a subida.</returns>
    public abstract ValidateOptionsResult Validate(string? name);
}
