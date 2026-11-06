using ClientProbe.Probe;
using ClientProbe.Telemetry;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace ClientProbe.Rabbit;

/// <summary>
/// Consumes the raw FIX frame that the NexusRouter publishes to RabbitMQ. On connect it declares the
/// profile exchange (<c>topic</c>, durable, idempotent) and an exclusive auto-delete queue bound to the
/// routing key. Each body goes through <see cref="ProbeAdapterBase.OnFrame"/>. Broker failures move the
/// adapter to <see cref="ProbeState.Faulted"/> and never reach the host.
/// </summary>
public sealed class RabbitConsumerAdapter : ProbeAdapterBase
{
    private IConnection? _connection;
    private IModel? _channel;

    /// <summary>Creates the adapter.</summary>
    /// <param name="publisher">Telemetry entry point.</param>
    /// <param name="options">Probe options.</param>
    /// <param name="time">Clock.</param>
    public RabbitConsumerAdapter(ITelemetryPublisher publisher, IOptions<ClientProbeOptions> options, TimeProvider time)
        : base(ProbeTransport.Rabbit, publisher, options, time)
    {
    }

    /// <inheritdoc />
    public override async Task ConnectAsync(ProbeProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile is not RabbitProfile rabbit)
        {
            throw new ArgumentException("Rabbit adapter requires a RabbitProfile.", nameof(profile));
        }

        await DisconnectAsync(ct).ConfigureAwait(false);
        Counters.Reset();
        SetState(ProbeState.Connecting);
        SetEndpoint(rabbit.Endpoint);

        try
        {
            var factory = new ConnectionFactory
            {
                HostName = rabbit.Host,
                Port = rabbit.Port,
                VirtualHost = rabbit.VirtualHost,
                UserName = Environment.GetEnvironmentVariable(rabbit.UsernameEnvVar) ?? "",
                // The modal password lives only in this factory/connection; it is never stored elsewhere.
                Password = rabbit.Password ?? Environment.GetEnvironmentVariable(rabbit.PasswordEnvVar) ?? "",
                AutomaticRecoveryEnabled = false,
                DispatchConsumersAsync = false,
            };

            var connection = await Task.Run(() => factory.CreateConnection("client-probe"), ct).ConfigureAwait(false);
            _connection = connection;
            connection.ConnectionShutdown += OnShutdown;

            var channel = connection.CreateModel();
            _channel = channel;
            channel.ExchangeDeclare(rabbit.Exchange, ExchangeType.Topic, durable: true, autoDelete: false);
            var queue = channel.QueueDeclare(queue: "", durable: false, exclusive: true, autoDelete: true).QueueName;
            channel.QueueBind(queue, rabbit.Exchange, rabbit.RoutingKey);

            var consumer = new EventingBasicConsumer(channel);
            consumer.Received += OnReceived;
            channel.BasicConsume(queue, autoAck: true, consumer);
            SetState(ProbeState.Live);
        }
        catch (OperationCanceledException)
        {
            CloseAll();
            SetState(ProbeState.Disconnected);
            SetEndpoint("");
            throw;
        }
        catch (Exception ex) when (ex is BrokerUnreachableException or AlreadyClosedException
            or OperationInterruptedException or IOException or ArgumentException or InvalidOperationException)
        {
            CloseAll();
            SetState(ProbeState.Faulted, ex.Message);
        }
    }

    /// <inheritdoc />
    public override Task DisconnectAsync(CancellationToken ct)
    {
        CloseAll();
        SetState(ProbeState.Disconnected);
        SetEndpoint("");
        return Task.CompletedTask;
    }

    private void OnReceived(object? sender, BasicDeliverEventArgs args)
    {
        if (State == ProbeState.Faulted)
        {
            SetState(ProbeState.Live);
        }

        OnFrame(args.Body.Span);
    }

    private void OnShutdown(object? sender, ShutdownEventArgs args)
    {
        // Initiator Application means our own Close; anything else is a broker/network fault.
        if (args.Initiator != ShutdownInitiator.Application && _connection is not null)
        {
            SetState(ProbeState.Faulted, args.ReplyText);
        }
    }

    private void CloseAll()
    {
        var channel = _channel;
        var connection = _connection;
        _channel = null;
        _connection = null;

        if (connection is not null)
        {
            connection.ConnectionShutdown -= OnShutdown;
        }

        try
        {
            channel?.Close();
        }
        catch (Exception ex) when (ex is AlreadyClosedException or OperationInterruptedException or IOException or ObjectDisposedException)
        {
            // Channel already gone with the connection; nothing left to release.
        }

        try
        {
            connection?.Close();
        }
        catch (Exception ex) when (ex is AlreadyClosedException or OperationInterruptedException or IOException or ObjectDisposedException)
        {
            // Connection already dropped by the broker.
        }

        channel?.Dispose();
        connection?.Dispose();
    }
}
