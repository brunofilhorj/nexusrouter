using System.Collections.Frozen;

namespace NexusRouter.Egress;

/// <summary>
/// Cadastro imutável dos clientes WebSocket. Resolve um <c>ClientId</c> do Logon para o cliente e o nome da variável do token.
/// </summary>
/// <remarks>
/// Clientes FIX não entram aqui: a chave deles é o <c>SenderCompID</c> do Logon, criada no primeiro Logon aceito.
/// A consulta é só de leitura e não aloca no caminho de sucesso; serve ao Logon, nunca ao hot path.
/// </remarks>
public sealed class ClientRegistry
{
    private readonly FrozenDictionary<(string DispatcherId, Guid ClientId), RegisteredClient> _clients;

    /// <summary>
    /// Monta o cadastro.
    /// </summary>
    /// <param name="clients">Clientes WebSocket cadastrados.</param>
    /// <exception cref="ArgumentNullException"><paramref name="clients"/> é nulo.</exception>
    /// <exception cref="ArgumentException">Há um cliente com UUID vazio, sem dispatcher, sem variável de token, ou repetido no mesmo dispatcher.</exception>
    public ClientRegistry(IEnumerable<RegisteredClient> clients)
    {
        ArgumentNullException.ThrowIfNull(clients);
        var map = new Dictionary<(string, Guid), RegisteredClient>();
        foreach (RegisteredClient client in clients)
        {
            ArgumentNullException.ThrowIfNull(client);
            if (client.ClientId == Guid.Empty)
            {
                throw new ArgumentException("O ClientId do cadastro não pode ser vazio.", nameof(clients));
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(client.DispatcherId);
            ArgumentException.ThrowIfNullOrWhiteSpace(client.TokenEnvVar);
            if (!map.TryAdd((client.DispatcherId, client.ClientId), client))
            {
                throw new ArgumentException($"O ClientId {client.ClientId} está repetido no dispatcher {client.DispatcherId}.", nameof(clients));
            }
        }

        _clients = map.ToFrozenDictionary();
    }

    /// <summary>
    /// Quantidade de clientes cadastrados.
    /// </summary>
    public int Count => _clients.Count;

    /// <summary>
    /// Procura o cliente de um dispatcher pelo UUID.
    /// </summary>
    /// <param name="dispatcherId">Dispatcher que recebeu o Logon.</param>
    /// <param name="clientId">UUID informado no Logon.</param>
    /// <param name="client">O cadastro, quando existe.</param>
    /// <returns><see langword="false"/> quando o UUID não está cadastrado para este dispatcher.</returns>
    public bool TryGet(string dispatcherId, Guid clientId, out RegisteredClient? client)
    {
        if (dispatcherId is null)
        {
            client = null;
            return false;
        }

        return _clients.TryGetValue((dispatcherId, clientId), out client);
    }
}
