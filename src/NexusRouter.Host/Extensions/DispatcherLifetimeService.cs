using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using NexusRouter.Domain.Abstractions;

namespace NexusRouter.Host.Extensions;

/// <summary>
/// Serviço hospedado que gerencia o ciclo de vida de todos os <see cref="IMessageDispatcher"/>s registrados.
/// </summary>
/// <remarks>
/// <para>
/// Os dispatchers implementam <see cref="IMessageDispatcher"/> (com <c>StartAsync</c>/<c>StopAsync</c>
/// retornando <c>ValueTask</c>), não <see cref="IHostedService"/> diretamente. Este adaptador permite
/// que o Generic Host controle o startup e o shutdown deles na ordem correta.
/// </para>
/// <para>
/// Um único <see cref="DispatcherLifetimeService"/> inicia e para todos os dispatchers em lote.
/// A instância é registrada no tier de Egress, garantindo que pare antes do Audit writer.
/// </para>
/// </remarks>
internal sealed class DispatcherLifetimeService : IHostedService
{
    private readonly IReadOnlyList<IMessageDispatcher> _dispatchers;
    private readonly ILogger<DispatcherLifetimeService> _logger;

    /// <summary>
    /// Cria o serviço com os dispatchers a gerenciar.
    /// </summary>
    /// <param name="dispatchers">Todos os <see cref="IMessageDispatcher"/> registrados no container.</param>
    /// <param name="logger">Logger estruturado.</param>
    public DispatcherLifetimeService(
        IEnumerable<IMessageDispatcher> dispatchers,
        ILogger<DispatcherLifetimeService> logger)
    {
        ArgumentNullException.ThrowIfNull(dispatchers);
        ArgumentNullException.ThrowIfNull(logger);

        _dispatchers = dispatchers.ToList().AsReadOnly();
        _logger = logger;
    }

    /// <summary>
    /// Inicia todos os dispatchers. Uma falha em um dispatcher não impede os demais de iniciarem.
    /// </summary>
    /// <param name="cancellationToken">Token de cancelamento do host.</param>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (IMessageDispatcher dispatcher in _dispatchers)
        {
            try
            {
                await dispatcher.StartAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Dispatcher {DispatcherId} iniciado", dispatcher.DispatcherId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao iniciar dispatcher {DispatcherId}", dispatcher.DispatcherId);
            }
        }
    }

    /// <summary>
    /// Para todos os dispatchers. Uma falha em um dispatcher não impede os demais de pararem.
    /// </summary>
    /// <param name="cancellationToken">Token de cancelamento do host.</param>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (IMessageDispatcher dispatcher in _dispatchers)
        {
            try
            {
                await dispatcher.StopAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Dispatcher {DispatcherId} encerrado", dispatcher.DispatcherId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao encerrar dispatcher {DispatcherId}", dispatcher.DispatcherId);
            }
        }
    }
}
