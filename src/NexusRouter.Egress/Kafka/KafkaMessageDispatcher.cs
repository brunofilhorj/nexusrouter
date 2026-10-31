using Confluent.Kafka;

using Microsoft.Extensions.Logging;

using NexusRouter.Domain.Models;

namespace NexusRouter.Egress.Kafka;

/// <summary>
/// Publica o frame FIX em um tópico Kafka.
/// </summary>
/// <remarks>
/// <see cref="ConnectAsync"/> monta um <c>ProducerConfig</c> a partir das options.
/// Não há ramo de provedor no envio. <see cref="KafkaSaslMechanism.AwsMskIam"/> é o único caso
/// que carrega o callback de IAM. Com <c>Acks=0</c> o envio usa <c>Produce</c>. Nos demais, <c>ProduceAsync</c>.
/// </remarks>
public sealed class KafkaMessageDispatcher : DispatcherBase<KafkaDispatcherOptions>
{
    private readonly IKafkaPartitionStrategy _partitionStrategy;
    private IProducer<Null, byte[]>? _producer;
    private int _released;

    /// <summary>
    /// Cria o dispatcher. A conexão com o broker acontece em <see cref="DispatcherBase{TOptions}.StartAsync"/>.
    /// </summary>
    /// <param name="dispatcherId">Nome único na rota.</param>
    /// <param name="options">Brokers, tópico e segurança. A validação ocorre aqui.</param>
    /// <param name="logger">Log de conexão e de falha de entrega.</param>
    /// <param name="partitionStrategy">
    /// Estratégia já construída. Quando nula, a opção <see cref="KafkaDispatcherOptions.PartitionStrategy"/> define a estratégia.
    /// </param>
    /// <exception cref="ArgumentException">As options são inválidas.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> é nulo.</exception>
    public KafkaMessageDispatcher(
        string dispatcherId,
        KafkaDispatcherOptions options,
        ILogger<KafkaMessageDispatcher> logger,
        IKafkaPartitionStrategy? partitionStrategy = null)
        : base(dispatcherId, Validate(options), options.ChannelCapacity, logger)
    {
        _partitionStrategy = partitionStrategy ?? CreateStrategy(options);
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
        IProducer<Null, byte[]> producer = _producer
            ?? throw new InvalidOperationException($"O dispatcher {DispatcherId} não tem producer Kafka.");
        if (payload.RawBuffer is null)
        {
            throw new InvalidOperationException("O payload Kafka não tem buffer.");
        }

        var message = new Message<Null, byte[]>
        {
            Value = payload.RawBuffer.Memory.ToArray(),
        };
        int? partition = _partitionStrategy.GetPartition(payload);

        if (Options.Acks == 0)
        {
            ProduceFireAndForget(producer, message, partition);
            return;
        }

        if (partition is int assigned)
        {
            await producer.ProduceAsync(
                new TopicPartition(Options.Topic, new Partition(assigned)),
                message,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await producer.ProduceAsync(Options.Topic, message, cancellationToken).ConfigureAwait(false);
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
                config.SaslUsername = EnvironmentValue.ReadRequired(Options.SaslUsernameEnvVar!);
                config.SaslPassword = EnvironmentValue.ReadRequired(Options.SaslPasswordEnvVar!);
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
