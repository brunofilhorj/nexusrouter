using NexusRouter.Domain.Models;

namespace NexusRouter.Egress;

/// <summary>
/// Cliente WebSocket cadastrado em <c>Clients</c>.
/// </summary>
/// <param name="ClientId">UUID do cliente. É a chave dele no log de sessão.</param>
/// <param name="DispatcherId">Dispatcher WebSocket ao qual o cliente pertence.</param>
/// <param name="TokenEnvVar">Nome da variável de ambiente com o token do Logon. O valor nunca fica no cadastro.</param>
public sealed record RegisteredClient(Guid ClientId, string DispatcherId, string TokenEnvVar)
{
    /// <summary>
    /// Chave do cliente no log de sessão (<c>clients/{DispatcherId}/{Key}</c>).
    /// </summary>
    public ClientKey Key => ClientKey.ForWebSocket(ClientId);
}
