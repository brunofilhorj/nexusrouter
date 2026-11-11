using Microsoft.Extensions.Options;

using NexusRouter.Host.Configuration;

namespace NexusRouter.Host.Tests;

public sealed class BrokerDispatcherOptionsTests
{
    [Fact]
    public void Validate_KafkaWithoutHeaderFields_Succeeds()
    {
        KafkaDispatcherOptions options = Kafka();

        ValidateOptionsResult result = options.Validate(null);

        Assert.True(result.Succeeded);
        Assert.Empty(options.HeaderFields);
    }

    [Fact]
    public void Validate_KafkaDuplicateHeaderField_Fails()
    {
        KafkaDispatcherOptions options = Kafka();
        options = new KafkaDispatcherOptions
        {
            Id = options.Id,
            BootstrapServers = options.BootstrapServers,
            Topic = options.Topic,
            ClientId = options.ClientId,
            Acks = options.Acks,
            HeaderFields = [11, 11],
        };

        ValidateOptionsResult result = options.Validate(null);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("11"));
    }

    [Fact]
    public void Validate_RabbitWithoutTemplate_Succeeds()
    {
        RabbitMQDispatcherOptions options = Rabbit();

        ValidateOptionsResult result = options.Validate(null);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_RabbitTemplateWithAccountAndSymbol_Succeeds()
    {
        RabbitMQDispatcherOptions options = Rabbit();
        options = new RabbitMQDispatcherOptions
        {
            Id = options.Id,
            Host = options.Host,
            UsernameEnvVar = options.UsernameEnvVar,
            PasswordEnvVar = options.PasswordEnvVar,
            Exchange = options.Exchange,
            HeaderFields = [1, 55],
            RoutingKeyTemplate = "fix.<1>.<55>",
        };

        ValidateOptionsResult result = options.Validate(null);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_RabbitTemplateTagMissing_Fails()
    {
        RabbitMQDispatcherOptions options = Rabbit();
        options = new RabbitMQDispatcherOptions
        {
            Id = options.Id,
            Host = options.Host,
            UsernameEnvVar = options.UsernameEnvVar,
            PasswordEnvVar = options.PasswordEnvVar,
            Exchange = options.Exchange,
            RoutingKeyTemplate = "fix.<1>.<55>",
        };

        ValidateOptionsResult result = options.Validate(null);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("ausente em HeaderFields"));
    }

    private static KafkaDispatcherOptions Kafka() =>
        new()
        {
            Id = "kafka-main",
            BootstrapServers = "localhost:19092",
            Topic = "fix.raw",
            ClientId = "nexusrouter",
            Acks = 1,
        };

    private static RabbitMQDispatcherOptions Rabbit() =>
        new()
        {
            Id = "rabbitmq-dropcopy",
            Host = "127.0.0.1",
            UsernameEnvVar = "RABBIT_USER",
            PasswordEnvVar = "RABBIT_PASS",
            Exchange = "dropcopy",
        };
}
