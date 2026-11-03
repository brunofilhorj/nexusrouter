using System.Diagnostics;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using NexusRouter.Domain.Abstractions;
using NexusRouter.Domain.Models;

namespace NexusRouter.Audit;

/// <summary>
/// Copia <see cref="IMessageDispatcher.OnDispatchError"/> para o channel de auditoria.
/// </summary>
/// <remarks>
/// O evento sai do loop de consumo do dispatcher, não do roteamento. O observer só chama
/// <see cref="AuditChannelWriter.TryWrite"/>. Overflow de channel de dispatcher já é registrado
/// pelo fan-out, no mesmo channel.
/// </remarks>
public sealed class DispatcherAuditObserver : IHostedService
{
    private readonly AuditChannelWriter _writer;
    private readonly ILogger<DispatcherAuditObserver> _logger;
    private readonly IMessageDispatcher[] _dispatchers;
    private readonly Action<string, Exception> _onError;
    private int _started;

    /// <summary>
    /// Guarda os dispatchers que já existem na inicialização.
    /// </summary>
    /// <param name="writer">Escrita não bloqueante do channel de auditoria.</param>
    /// <param name="dispatchers">Dispatchers registrados. A lista é copiada.</param>
    /// <param name="logger">Avisa quando o channel recusa o registro de erro.</param>
    /// <exception cref="ArgumentNullException">Alguma dependência é nula.</exception>
    public DispatcherAuditObserver(
        AuditChannelWriter writer,
        IEnumerable<IMessageDispatcher> dispatchers,
        ILogger<DispatcherAuditObserver> logger)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(dispatchers);
        ArgumentNullException.ThrowIfNull(logger);
        _writer = writer;
        _logger = logger;
        _dispatchers = dispatchers.ToArray();
        for (int i = 0; i < _dispatchers.Length; i++)
        {
            ArgumentNullException.ThrowIfNull(_dispatchers[i]);
        }

        _onError = OnDispatchError;
    }

    /// <summary>
    /// Assina o erro de cada dispatcher.
    /// </summary>
    /// <param name="cancellationToken">Não usado: a assinatura é síncrona.</param>
    /// <returns>Tarefa já concluída.</returns>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return Task.CompletedTask;
        }

        foreach (IMessageDispatcher dispatcher in _dispatchers)
        {
            dispatcher.OnDispatchError += _onError;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Remove a assinatura feita em <see cref="StartAsync"/>.
    /// </summary>
    /// <param name="cancellationToken">Não usado: a remoção é síncrona.</param>
    /// <returns>Tarefa já concluída.</returns>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 0) != 1)
        {
            return Task.CompletedTask;
        }

        foreach (IMessageDispatcher dispatcher in _dispatchers)
        {
            dispatcher.OnDispatchError -= _onError;
        }

        return Task.CompletedTask;
    }

    private void OnDispatchError(string dispatcherId, Exception exception)
    {
        var record = new AuditRecord
        {
            WasDropped = true,
            DropReason = exception.Message,
            DispatcherIds = string.IsNullOrEmpty(dispatcherId) ? [] : [dispatcherId],
            IngressTimestampTicks = Stopwatch.GetTimestamp(),
        };

        if (_writer.TryWrite(record))
        {
            return;
        }

        _logger.LogWarning(
            "O channel de auditoria recusou o erro do dispatcher {DispatcherId}.",
            dispatcherId);
    }
}
