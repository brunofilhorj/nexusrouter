using System.Text;

using Microsoft.Extensions.Logging;

using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

using NexusRouter.Domain.Models;
using NexusRouter.Domain.Pooling;
using NexusRouter.Domain.Secrets;
using NexusRouter.Egress;

namespace NexusRouter.Egress.RabbitMQ;

/// <summary>
/// Publica o frame FIX em uma exchange RabbitMQ, com um model exclusivo deste dispatcher.
/// </summary>
/// <remarks>
/// <see cref="CursorAckMode.OnBrokerAck"/> liga publisher confirms e só então avança o cursor.
/// <see cref="CursorAckMode.OnEnqueue"/> publica sem esperar o broker.
/// Cada frame com <see cref="LogRef"/> leva o header <see cref="BrokerIdentity.HeaderName"/>.
/// Se o model já fechou, o envio abre a conexão de novo uma vez e tenta publicar outra vez.
/// </remarks>
public sealed class RabbitMQMessageDispatcher : DispatcherBase<RabbitMQDispatcherOptions>
{
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(5);

    private readonly object _connectionGate = new();
    private readonly ISecretResolver _secretResolver;
    private readonly BrokerCursorTracker? _tracker;
    private readonly RabbitPublish? _publish;
    private IConnection? _connection;
    private IModel? _model;
    private int _released;

    /// <summary>
    /// Publica o corpo já com o header. O teste injeta este delegate no lugar do model.
    /// </summary>
    /// <param name="body">Frame FIX.</param>
    /// <param name="identity">Header <see cref="BrokerIdentity.HeaderName"/>, ou nulo.</param>
    /// <param name="cancellationToken">Cancela a espera do teste.</param>
    /// <returns>Tarefa concluída quando a publicação foi aceita.</returns>
    internal delegate ValueTask RabbitPublish(
        ReadOnlyMemory<byte> body,
        string? identity,
        CancellationToken cancellationToken);

    /// <summary>
    /// Cria o dispatcher. A conexão abre em <see cref="DispatcherBase{TOptions}.StartAsync"/>.
    /// </summary>
    /// <param name="dispatcherId">Nome único na rota. Também é o nome da conexão no broker.</param>
    /// <param name="options">Host, exchange e chaves de lookup das credenciais.</param>
    /// <param name="logger">Log de conexão e de reconexão.</param>
    /// <param name="payloadPool">Pool de payloads do dispatcher.</param>
    /// <param name="secretResolver">Resolve as credenciais pelas chaves em <paramref name="options"/>, sob demanda.</param>
    /// <param name="catchUp">Cursor do log de sessão. Nulo entrega só o que chegar pelo channel.</param>
    /// <param name="tracker">Marca-d'água do cursor. Nulo não persiste confirmação.</param>
    /// <exception cref="ArgumentException">As options são inválidas.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> ou <paramref name="secretResolver"/> é nulo.</exception>
    public RabbitMQMessageDispatcher(
        string dispatcherId,
        RabbitMQDispatcherOptions options,
        ILogger<RabbitMQMessageDispatcher> logger,
        DispatchPayloadPool payloadPool,
        ISecretResolver secretResolver,
        StreamCatchUp? catchUp = null,
        BrokerCursorTracker? tracker = null)
        : base(dispatcherId, Validate(options), options.ChannelCapacity, payloadPool, logger, catchUp)
    {
        ArgumentNullException.ThrowIfNull(secretResolver);
        _secretResolver = secretResolver;
        _tracker = tracker;
    }

    /// <summary>
    /// Cria o dispatcher com um publicador injetado, sem abrir conexão com o broker.
    /// </summary>
    /// <param name="dispatcherId">Nome único na rota.</param>
    /// <param name="options">Options já coerentes com o modo do cursor.</param>
    /// <param name="logger">Log de falha de entrega.</param>
    /// <param name="payloadPool">Pool de payloads do dispatcher.</param>
    /// <param name="secretResolver">Não é chamado enquanto o publicador injetado estiver no lugar.</param>
    /// <param name="publish">Publicação usada no lugar do model.</param>
    /// <param name="tracker">Marca-d'água do cursor.</param>
    /// <param name="catchUp">Cursor do log de sessão.</param>
    internal RabbitMQMessageDispatcher(
        string dispatcherId,
        RabbitMQDispatcherOptions options,
        ILogger<RabbitMQMessageDispatcher> logger,
        DispatchPayloadPool payloadPool,
        ISecretResolver secretResolver,
        RabbitPublish publish,
        BrokerCursorTracker? tracker,
        StreamCatchUp? catchUp)
        : this(dispatcherId, options, logger, payloadPool, secretResolver, catchUp, tracker)
    {
        ArgumentNullException.ThrowIfNull(publish);
        _publish = publish;
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
        if (_publish is not null)
        {
            return ValueTask.CompletedTask;
        }

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
    protected override async ValueTask SendAsync(DispatchPayload payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (payload.RawBuffer is null)
        {
            throw new InvalidOperationException("O payload RabbitMQ não tem buffer.");
        }

        ReadOnlyMemory<byte> body = payload.RawBuffer.Memory;
        string? identity = BrokerIdentity.Format(payload.LogRef);
        if (Options.CursorAckMode == CursorAckMode.OnBrokerAck)
        {
            _tracker?.Track(payload.LogRef);
        }

        if (_publish is not null)
        {
            await _publish(body, identity, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            try
            {
                Publish(body, identity);
            }
            catch (Exception ex) when (ex is AlreadyClosedException or BrokerUnreachableException or InvalidOperationException)
            {
                Logger.LogWarning(ex, "Dispatcher {DispatcherId} perdeu o canal RabbitMQ e vai reconectar.", DispatcherId);
                Open();
                Publish(body, identity);
            }
        }

        if (Options.CursorAckMode == CursorAckMode.OnBrokerAck)
        {
            _tracker?.Confirm(payload.LogRef);
        }
    }

    private void Publish(ReadOnlyMemory<byte> body, string? identity)
    {
        lock (_connectionGate)
        {
            IModel model = _model ?? throw new InvalidOperationException("O model RabbitMQ não está aberto.");
            IBasicProperties? properties = null;
            if (identity is not null)
            {
                properties = model.CreateBasicProperties();
                properties.Headers = new Dictionary<string, object>
                {
                    [BrokerIdentity.HeaderName] = Encoding.UTF8.GetBytes(identity),
                };
            }

            model.BasicPublish(Options.Exchange, Options.RoutingKey, mandatory: false, basicProperties: properties, body: body);
            if (Options.CursorAckMode != CursorAckMode.OnBrokerAck)
            {
                return;
            }

            if (!model.WaitForConfirms(ConfirmTimeout))
            {
                throw new TimeoutException($"O dispatcher {DispatcherId} não recebeu a confirmação do RabbitMQ.");
            }
        }
    }

    private void Open()
    {
        lock (_connectionGate)
        {
            CloseQuietly();

            string userName = _secretResolver.ResolveRequired(Options.UsernameEnvVar);
            string password = _secretResolver.ResolveRequired(Options.PasswordEnvVar);
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
            if (Options.CursorAckMode == CursorAckMode.OnBrokerAck)
            {
                _model.ConfirmSelect();
            }

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
