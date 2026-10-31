using System.Collections.Frozen;
using System.Collections.Immutable;

using Microsoft.Extensions.Logging;

using NexusRouter.Domain.Abstractions;
using NexusRouter.Domain.Models;

namespace NexusRouter.Router;

/// <summary>
/// Tabela de roteamento imutável, consultada sem lock.
/// </summary>
public sealed class RouteResolver
{
    private readonly FrozenDictionary<SessionId, RouteContext> _routes;

    /// <summary>
    /// Congela a lista de rotas recebida do host.
    /// </summary>
    /// <param name="routes">Rotas já montadas. A lista não é relida depois da construção.</param>
    /// <param name="logger">Recebe o aviso de sessão sem dispatcher.</param>
    /// <exception cref="ArgumentNullException"><paramref name="routes"/> ou <paramref name="logger"/> é nulo.</exception>
    /// <exception cref="ArgumentException">Há sessão repetida ou um dispatcher nulo.</exception>
    public RouteResolver(IReadOnlyList<RouteContext> routes, ILogger<RouteResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(logger);

        var table = new Dictionary<SessionId, RouteContext>(routes.Count);
        foreach (RouteContext route in routes)
        {
            WarnWhenDispatchersMissing(route, logger);
            EnsureDispatchersPresent(route);

            if (!table.TryAdd(route.SessionId, route))
            {
                throw new ArgumentException(
                    $"A sessão '{route.SessionId.Value}' aparece mais de uma vez na tabela de rotas.",
                    nameof(routes));
            }
        }

        _routes = table.ToFrozenDictionary();
    }

    /// <summary>
    /// Quantidade de sessões na tabela.
    /// </summary>
    public int Count => _routes.Count;

    /// <summary>
    /// Procura a rota da sessão.
    /// </summary>
    /// <param name="sessionId">Sessão do frame inbound.</param>
    /// <param name="context">Rota encontrada. Default quando o método retorna <see langword="false"/>.</param>
    /// <returns><see langword="true"/> quando a sessão está na tabela.</returns>
    /// <remarks>Zero allocation. Seguro para chamada no hot path. A leitura do dicionário congelado não usa lock.</remarks>
    public bool TryResolve(SessionId sessionId, out RouteContext context)
    {
        return _routes.TryGetValue(sessionId, out context);
    }

    private static void WarnWhenDispatchersMissing(RouteContext route, ILogger logger)
    {
        if (route.Dispatchers.IsDefault || route.Dispatchers.Length == 0)
        {
            logger.LogWarning(
                "Sessão {SessionId} não tem dispatcher registrado. As mensagens dessa sessão não serão encaminhadas.",
                route.SessionId.Value);
        }
    }

    private static void EnsureDispatchersPresent(RouteContext route)
    {
        if (route.Dispatchers.IsDefault)
        {
            return;
        }

        ImmutableArray<IMessageDispatcher> dispatchers = route.Dispatchers;
        for (int i = 0; i < dispatchers.Length; i++)
        {
            if (dispatchers[i] is null)
            {
                throw new ArgumentException(
                    $"A rota da sessão '{route.SessionId.Value}' contém um dispatcher nulo.",
                    nameof(route));
            }
        }
    }
}
