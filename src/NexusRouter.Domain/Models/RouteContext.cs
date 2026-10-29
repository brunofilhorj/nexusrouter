using System.Collections.Immutable;

using NexusRouter.Domain.Abstractions;
using NexusRouter.Domain.Enums;

namespace NexusRouter.Domain.Models;

/// <summary>
/// Rota imutável de uma sessão: modo de entrega e dispatchers de destino.
/// </summary>
/// <remarks>
/// Vive em um dicionário congelado depois da inicialização. O array de dispatchers não é substituído em operação normal.
/// </remarks>
public readonly record struct RouteContext
{
    /// <summary>
    /// Cria o contexto de roteamento de uma sessão.
    /// </summary>
    /// <param name="sessionId">Sessão à qual a rota pertence.</param>
    /// <param name="deliveryMode">Modo de entrega aplicado às mensagens dessa sessão.</param>
    /// <param name="dispatchers">Dispatchers que recebem o fan-out. Um array default é normalizado para vazio.</param>
    public RouteContext(SessionId sessionId, DeliveryMode deliveryMode, ImmutableArray<IMessageDispatcher> dispatchers)
    {
        SessionId = sessionId;
        DeliveryMode = deliveryMode;
        Dispatchers = dispatchers.IsDefault ? ImmutableArray<IMessageDispatcher>.Empty : dispatchers;
    }

    /// <summary>
    /// Sessão dona desta rota.
    /// </summary>
    public SessionId SessionId { get; }

    /// <summary>
    /// Modo de entrega das mensagens desta sessão.
    /// </summary>
    public DeliveryMode DeliveryMode { get; }

    /// <summary>
    /// Dispatchers que recebem cada mensagem aceita pela rota.
    /// </summary>
    public ImmutableArray<IMessageDispatcher> Dispatchers { get; }
}
