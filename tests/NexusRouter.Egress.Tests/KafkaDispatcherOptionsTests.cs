using NexusRouter.Egress.Kafka;
using NexusRouter.Egress;

namespace NexusRouter.Egress.Tests;

public sealed class KafkaDispatcherOptionsTests
{
    [Fact]
    public void EnsureValid_PlaintextWithoutSasl_AcceptsLocalBroker()
    {
        KafkaDispatcherOptions options = Local();

        options.EnsureValid();
    }

    [Fact]
    public void EnsureValid_SaslSslWithoutMechanism_Throws()
    {
        KafkaDispatcherOptions options = Local();
        options = new KafkaDispatcherOptions
        {
            BootstrapServers = options.BootstrapServers,
            Topic = options.Topic,
            ClientId = options.ClientId,
            Acks = 1,
            SecurityProtocol = KafkaSecurityProtocol.SaslSsl,
            SaslMechanism = KafkaSaslMechanism.None,
        };

        Assert.Throws<ArgumentException>(() => options.EnsureValid());
    }

    [Fact]
    public void EnsureValid_ScramWithoutEnvVarNames_Throws()
    {
        var options = new KafkaDispatcherOptions
        {
            BootstrapServers = "b-1.example:9096",
            Topic = "fix.raw",
            ClientId = "nexusrouter",
            Acks = 1,
            SecurityProtocol = KafkaSecurityProtocol.SaslSsl,
            SaslMechanism = KafkaSaslMechanism.ScramSha512,
        };

        ArgumentException error = Assert.Throws<ArgumentException>(() => options.EnsureValid());
        Assert.Contains("SaslUsernameEnvVar", error.Message);
    }

    [Fact]
    public void EnsureValid_ByClOrdIdWithoutPartitionCount_Throws()
    {
        var options = new KafkaDispatcherOptions
        {
            BootstrapServers = "localhost:19092",
            Topic = "fix.raw",
            ClientId = "nexusrouter",
            Acks = 1,
            PartitionStrategy = KafkaPartitionStrategy.ByClOrdId,
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => options.EnsureValid());
    }

    [Fact]
    public void EnsureValid_AcksZeroWithOnBrokerAck_Throws()
    {
        var options = new KafkaDispatcherOptions
        {
            BootstrapServers = "localhost:19092",
            Topic = "fix.raw",
            ClientId = "nexusrouter",
            Acks = 0,
            CursorAckMode = CursorAckMode.OnBrokerAck,
        };

        ArgumentException error = Assert.Throws<ArgumentException>(() => options.EnsureValid());
        Assert.Contains("OnEnqueue", error.Message);
    }

    [Fact]
    public void EnsureValid_AcksZeroWithOnEnqueue_Accepts()
    {
        var options = new KafkaDispatcherOptions
        {
            BootstrapServers = "localhost:19092",
            Topic = "fix.raw",
            ClientId = "nexusrouter",
            Acks = 0,
            CursorAckMode = CursorAckMode.OnEnqueue,
        };

        options.EnsureValid();
    }

    private static KafkaDispatcherOptions Local() =>
        new()
        {
            BootstrapServers = "localhost:19092",
            Topic = "fix.raw",
            ClientId = "nexusrouter",
            Acks = 1,
        };
}
