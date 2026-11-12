using System.Buffers;
using System.Text;

using Confluent.Kafka;

using Microsoft.Extensions.Logging;

using NexusRouter.Domain.Models;
using NexusRouter.Egress;
using NexusRouter.Domain.Pooling;
using NexusRouter.Domain.Secrets;

namespace NexusRouter.Egress.Kafka;

/// <summary>
/// Publica o frame FIX em um tópico Kafka.
/// </summary>
/// <remarks>
/// <see cref="ConnectAsync"/> monta um <c>ProducerConfig</c> a partir das options.
/// Não há ramo de provedor no envio. <see cref="KafkaSaslMechanism.AwsMskIam"/> é o único caso
/// que carrega o callback de IAM. Com <c>Acks=0</c> o envio usa <c>Produce</c>. Nos demais, <c>ProduceAsync</c>.
/// Cada frame com <see cref="LogRef"/> leva o header <see cref="BrokerIdentity.HeaderName"/>.
/// <see cref="CursorAckMode.OnBrokerAck"/> só avança o cursor depois do ack do broker.
/// </remarks>
public sealed class KafkaMessageDispatcher : DispatcherBase<KafkaDispatcherOptions>
{
    private readonly IKafkaPartitionStrategy _partitionStrategy;
    private readonly ISecretResolver _secretResolver;
    private readonly BrokerCursorTracker? _tracker;
    private IProducer<Null, byte[]>? _producer;
    private KafkaPublish? _publish;
    private int _released;

    /// <summary>
    /// Publica um record já montado. O teste injeta este delegate no lugar do producer.
    /// </summary>
    /// <param name="topic">Tópico de destino.</param>
    /// <param name="partition">Partição explícita, ou nulo para o particionador do cliente.</param>
    /// <param name="value">Cópia do frame.</param>
    /// <param name="identity">Header <see cref="BrokerIdentity.HeaderName"/>, ou nulo.</param>
    /// <param name="cancellationToken">Cancela a espera do ack.</param>
    /// <returns>Tarefa concluída quando a biblioteca aceitou o envio, ou quando o broker confirmou.</returns>
    internal delegate ValueTask KafkaPublish(
        string topic,
        int? partition,
        byte[] value,
        string? identity,
        Headers? headers,
        CancellationToken cancellationToken);

    /// <summary>
    /// Cria o dispatcher. A conexão com o broker acontece em <see cref="DispatcherBase{TOptions}.StartAsync"/>.
    /// </summary>
    /// <param name="dispatcherId">Nome único na rota.</param>
    /// <param name="options">Brokers, tópico e segurança. A validação ocorre aqui.</param>
    /// <param name="logger">Log de conexão e de falha de entrega.</param>
    /// <param name="secretResolver">Resolve usuário e senha SASL pelas chaves em <paramref name="options"/>, sob demanda.</param>
    /// <param name="partitionStrategy">
    /// Estratégia já construída. Quando nula, a opção <see cref="KafkaDispatcherOptions.PartitionStrategy"/> define a estratégia.
    /// </param>
    /// <param name="catchUp">Cursor do log de sessão. Nulo entrega só o que chegar pelo channel.</param>
    /// <param name="tracker">Marca-d'água do cursor. Nulo não persiste confirmação.</param>
    /// <exception cref="ArgumentException">As options são inválidas.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> ou <paramref name="secretResolver"/> é nulo.</exception>
    public KafkaMessageDispatcher(
        string dispatcherId,
        KafkaDispatcherOptions options,
        ILogger<KafkaMessageDispatcher> logger,
        DispatchPayloadPool payloadPool,
        ISecretResolver secretResolver,
        IKafkaPartitionStrategy? partitionStrategy = null,
        StreamCatchUp? catchUp = null,
        BrokerCursorTracker? tracker = null)
        : base(dispatcherId, Validate(options), options.ChannelCapacity, payloadPool, logger, catchUp)
    {
        ArgumentNullException.ThrowIfNull(secretResolver);
        _secretResolver = secretResolver;
        _partitionStrategy = partitionStrategy ?? CreateStrategy(options);
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
    /// <param name="publish">Publicação usada no lugar do producer.</param>
    /// <param name="tracker">Marca-d'água do cursor.</param>
    /// <param name="catchUp">Cursor do log de sessão.</param>
    /// <param name="partitionStrategy">Estratégia de partição usada pelo envio de teste.</param>
    internal KafkaMessageDispatcher(
        string dispatcherId,
        KafkaDispatcherOptions options,
        ILogger<KafkaMessageDispatcher> logger,
        DispatchPayloadPool payloadPool,
        ISecretResolver secretResolver,
        KafkaPublish publish,
        BrokerCursorTracker? tracker,
        StreamCatchUp? catchUp,
        IKafkaPartitionStrategy? partitionStrategy = null)
        : this(dispatcherId, options, logger, payloadPool, secretResolver, partitionStrategy, catchUp, tracker)
    {
        ArgumentNullException.ThrowIfNull(publish);
        _publish = publish;
    }

    /// <summary>
    /// Fecha o producer depois de drenar o channel.
    /// </summary>
    /// <param name="cancellationToken">Abandona a espera do loop. O producer ainda é fechado.</param>
    /// <returns>Tarefa concluída quando o producer foi descartado.</returns>
    public override async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        ReleaseProducer();
    }

    /// <inheritdoc />
    protected override ValueTask ConnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_publish is not null)
        {
            return ValueTask.CompletedTask;
        }

        if (Options.SaslMechanism == KafkaSaslMechanism.OAuthBearer)
        {
            throw new InvalidOperationException(
                "OAuthBearer genérico não tem callback neste dispatcher. Para Amazon MSK IAM use AwsMskIam.");
        }

        ProducerConfig config = BuildConfig();
        var builder = new ProducerBuilder<Null, byte[]>(config);
        if (Options.SaslMechanism == KafkaSaslMechanism.AwsMskIam)
        {
            AwsMskIamTokenRefresh.Attach(builder);
        }

        _producer = builder.Build();
        _publish = PublishWithProducer;
        Logger.LogInformation(
            "Dispatcher {DispatcherId} conectou o producer Kafka em {BootstrapServers}, tópico {Topic}.",
            DispatcherId,
            Options.BootstrapServers,
            Options.Topic);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override async ValueTask SendAsync(DispatchPayload payload, CancellationToken cancellationToken)
    {
        KafkaPublish publish = _publish
            ?? throw new InvalidOperationException($"O dispatcher {DispatcherId} não tem producer Kafka.");
        if (payload.RawBuffer is null)
        {
            throw new InvalidOperationException("O payload Kafka não tem buffer.");
        }

        string? identity = BrokerIdentity.Format(payload.LogRef);
        ReadOnlyMemory<byte>?[] fieldValues = BrokerHeaderFields.ReadValues(payload, Options.HeaderFields);
        Headers? headers = CreateHeaders(identity, Options.HeaderFields, fieldValues);
        if (Options.CursorAckMode == CursorAckMode.OnBrokerAck)
        {
            _tracker?.Track(payload.LogRef);
        }

        byte[] value;
        if (Options.PayloadEncoding == BrokerPayloadEncoding.Json)
        {
            var buffer = new ArrayBufferWriter<byte>(512);
            FixJsonProjector.Write(payload.RawBuffer.Memory.Span, DateTime.UtcNow.Ticks, buffer);
            value = buffer.WrittenSpan.ToArray();
        }
        else
        {
            value = payload.RawBuffer.Memory.ToArray();
        }

        await publish(
            Options.Topic,
            _partitionStrategy.GetPartition(payload),
            value,
            identity,
            headers,
            cancellationToken).ConfigureAwait(false);

        if (Options.CursorAckMode == CursorAckMode.OnBrokerAck)
        {
            _tracker?.Confirm(payload.LogRef);
        }
    }

    private async ValueTask PublishWithProducer(
        string topic,
        int? partition,
        byte[] value,
        string? identity,
        Headers? headers,
        CancellationToken cancellationToken)
    {
        IProducer<Null, byte[]> producer = _producer
            ?? throw new InvalidOperationException($"O dispatcher {DispatcherId} não tem producer Kafka.");
        var message = new Message<Null, byte[]>
        {
            Value = value,
            Headers = headers,
        };

        if (Options.Acks == 0)
        {
            ProduceFireAndForget(producer, message, partition);
            return;
        }

        if (partition is int assigned)
        {
            await producer.ProduceAsync(
                new TopicPartition(topic, new Partition(assigned)),
                message,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await producer.ProduceAsync(topic, message, cancellationToken).ConfigureAwait(false);
    }

    private static Headers? CreateHeaders(
        string? identity,
        IReadOnlyList<int> headerFields,
        ReadOnlyMemory<byte>?[] fieldValues)
    {
        bool hasField = false;
        for (int i = 0; i < fieldValues.Length; i++)
        {
            hasField |= fieldValues[i] is not null;
        }

        if (identity is null && !hasField)
        {
            return null;
        }

        var headers = new Headers();
        if (identity is not null)
        {
            headers.Add(BrokerIdentity.HeaderName, Encoding.UTF8.GetBytes(identity));
        }

        for (int i = 0; i < fieldValues.Length; i++)
        {
            if (fieldValues[i] is ReadOnlyMemory<byte> value)
            {
                headers.Add(BrokerHeaderFields.HeaderName(headerFields[i]), value.ToArray());
            }
        }

        return headers;
    }

    private void ProduceFireAndForget(IProducer<Null, byte[]> producer, Message<Null, byte[]> message, int? partition)
    {
        if (partition is int assigned)
        {
            producer.Produce(new TopicPartition(Options.Topic, new Partition(assigned)), message, OnDelivery);
            return;
        }

        producer.Produce(Options.Topic, message, OnDelivery);
    }

    private void OnDelivery(DeliveryReport<Null, byte[]> report)
    {
        if (report.Error.IsError)
        {
            ReportDispatchError(new KafkaException(report.Error));
        }
    }

    private ProducerConfig BuildConfig()
    {
        var config = new ProducerConfig
        {
            BootstrapServers = Options.BootstrapServers,
            ClientId = Options.ClientId,
            Acks = Options.Acks switch
            {
                0 => Acks.None,
                1 => Acks.Leader,
                _ => Acks.All,
            },
            SecurityProtocol = Options.SecurityProtocol switch
            {
                KafkaSecurityProtocol.Plaintext => SecurityProtocol.Plaintext,
                KafkaSecurityProtocol.Ssl => SecurityProtocol.Ssl,
                KafkaSecurityProtocol.SaslPlaintext => SecurityProtocol.SaslPlaintext,
                KafkaSecurityProtocol.SaslSsl => SecurityProtocol.SaslSsl,
                _ => throw new ArgumentOutOfRangeException(nameof(Options.SecurityProtocol)),
            },
        };

        if (Options.SecurityProtocol is KafkaSecurityProtocol.Ssl or KafkaSecurityProtocol.SaslSsl)
        {
            config.EnableSslCertificateVerification = Options.EnableSslCertificateVerification;
            if (!string.IsNullOrWhiteSpace(Options.SslCaLocation))
            {
                config.SslCaLocation = Options.SslCaLocation;
            }
        }

        if (Options.SecurityProtocol is KafkaSecurityProtocol.SaslPlaintext or KafkaSecurityProtocol.SaslSsl)
        {
            config.SaslMechanism = Options.SaslMechanism switch
            {
                KafkaSaslMechanism.Plain => SaslMechanism.Plain,
                KafkaSaslMechanism.ScramSha256 => SaslMechanism.ScramSha256,
                KafkaSaslMechanism.ScramSha512 => SaslMechanism.ScramSha512,
                KafkaSaslMechanism.OAuthBearer => SaslMechanism.OAuthBearer,
                KafkaSaslMechanism.AwsMskIam => SaslMechanism.OAuthBearer,
                _ => throw new ArgumentOutOfRangeException(nameof(Options.SaslMechanism)),
            };

            if (KafkaDispatcherOptions.UsesPassword(Options.SaslMechanism))
            {
                config.SaslUsername = _secretResolver.ResolveRequired(Options.SaslUsernameEnvVar!);
                config.SaslPassword = _secretResolver.ResolveRequired(Options.SaslPasswordEnvVar!);
            }
        }

        return config;
    }

    private void ReleaseProducer()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return;
        }

        IProducer<Null, byte[]>? producer = Interlocked.Exchange(ref _producer, null);
        if (producer is null)
        {
            return;
        }

        producer.Flush(TimeSpan.FromSeconds(5));
        producer.Dispose();
    }

    private static KafkaDispatcherOptions Validate(KafkaDispatcherOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.EnsureValid();
        return options;
    }

    private static IKafkaPartitionStrategy CreateStrategy(KafkaDispatcherOptions options) =>
        options.PartitionStrategy switch
        {
            KafkaPartitionStrategy.None => NonePartitionStrategy.Instance,
            KafkaPartitionStrategy.ByClOrdId => new ByClOrdIdPartitionStrategy(options.PartitionCount),
            _ => throw new ArgumentOutOfRangeException(nameof(options.PartitionStrategy)),
        };

    private sealed class NonePartitionStrategy : IKafkaPartitionStrategy
    {
        public static readonly NonePartitionStrategy Instance = new();

        public int? GetPartition(DispatchPayload payload) => null;
    }
}
