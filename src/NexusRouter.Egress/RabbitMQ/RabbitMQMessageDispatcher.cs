using Microsoft.Extensions.Logging;

using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

using NexusRouter.Domain.Models;

namespace NexusRouter.Egress.RabbitMQ;

/// <summary>
/// Publica o frame FIX em uma exchange RabbitMQ, com um model exclusivo deste dispatcher.
/// </summary>
/// <remarks>
/// Confirmação de publisher fica desligada. <c>mandatory</c> é falso.
/// Se o model já fechou, o envio abre a conexão de novo uma vez e tenta publicar outra vez.
/// </remarks>
public sealed class RabbitMQMessageDispatcher : DispatcherBase<RabbitMQDispatcherOptions>
{
    private readonly object _connectionGate = new();
    private IConnection? _connection;
    private IModel? _model;
    private int _released;

    /// <summary>
    /// Cria o dispatcher. A conexão abre em <see cref="DispatcherBase{TOptions}.StartAsync"/>.
    /// </summary>
    /// <param name="dispatcherId">Nome único na rota. Também é o nome da conexão no broker.</param>
    /// <param name="options">Host, exchange e nomes das variáveis de credencial.</param>
    /// <param name="logger">Log de conexão e de reconexão.</param>
    /// <exception cref="ArgumentException">As options são inválidas.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> é nulo.</exception>
    public RabbitMQMessageDispatcher(
        string dispatcherId,
        RabbitMQDispatcherOptions options,
        ILogger<RabbitMQMessageDispatcher> logger)
        : base(dispatcherId, Validate(options), options.ChannelCapacity, logger)
    {
    }

    /// <summary>
    /// Fecha o model e a conexão depois de drenar o channel.
    /// </summary>
    /// <param name="cancellationToken">Abandona a espera do loop. A conexão ainda é fechada.</param>
    /// <returns>Tarefa concluída quando a conexão foi descartada.</returns>
    public override async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        ReleaseConnection();
    }

    /// <inheritdoc />
    protected override ValueTask ConnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Open();
        Logger.LogInformation(
            "Dispatcher {DispatcherId} conectou no RabbitMQ {Host}:{Port}, exchange {Exchange}.",
            DispatcherId,
            Options.Host,
            Options.Port,
            Options.Exchange);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask SendAsync(DispatchPayload payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (payload.RawBuffer is null)
        {
            throw new InvalidOperationException("O payload RabbitMQ não tem buffer.");
        }

        ReadOnlyMemory<byte> body = payload.RawBuffer.Memory;
        try
        {
            Publish(body);
        }
        catch (Exception ex) when (ex is AlreadyClosedException or BrokerUnreachableException or InvalidOperationException)
        {
            Logger.LogWarning(ex, "Dispatcher {DispatcherId} perdeu o canal RabbitMQ e vai reconectar.", DispatcherId);
            Open();
            Publish(body);
        }

        return ValueTask.CompletedTask;
    }

    private void Publish(ReadOnlyMemory<byte> body)
    {
        lock (_connectionGate)
        {
            IModel model = _model ?? throw new InvalidOperationException("O model RabbitMQ não está aberto.");
            model.BasicPublish(Options.Exchange, Options.RoutingKey, mandatory: false, basicProperties: null, body: body);
        }
    }

    private void Open()
    {
        lock (_connectionGate)
        {
            CloseQuietly();

            string userName = EnvironmentValue.ReadRequired(Options.UsernameEnvVar);
            string password = EnvironmentValue.ReadRequired(Options.PasswordEnvVar);
            var factory = new ConnectionFactory
            {
                HostName = Options.Host,
                Port = Options.Port,
                VirtualHost = Options.VirtualHost,
                UserName = userName,
                Password = password,
                AutomaticRecoveryEnabled = false,
            };

            _connection = factory.CreateConnection(DispatcherId);
            _model = _connection.CreateModel();
            if (Options.PrefetchCount is ushort prefetch)
            {
                _model.BasicQos(prefetchSize: 0, prefetchCount: prefetch, global: false);
            }
        }
    }

    private void ReleaseConnection()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return;
        }

        lock (_connectionGate)
        {
            CloseQuietly();
        }
    }

    private void CloseQuietly()
    {
        try
        {
            _model?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or AlreadyClosedException)
        {
            Logger.LogDebug(ex, "Dispatcher {DispatcherId} fechou um model RabbitMQ já encerrado.", DispatcherId);
        }
        finally
        {
            _model = null;
        }

        try
        {
            _connection?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or AlreadyClosedException)
        {
            Logger.LogDebug(ex, "Dispatcher {DispatcherId} fechou uma conexão RabbitMQ já encerrada.", DispatcherId);
        }
        finally
        {
            _connection = null;
        }
    }

    private static RabbitMQDispatcherOptions Validate(RabbitMQDispatcherOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.EnsureValid();
        return options;
    }
}
