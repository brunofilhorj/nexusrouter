using NexusRouter.Egress.RabbitMQ;

namespace NexusRouter.Egress.Tests;

public sealed class RabbitMQDispatcherOptionsTests
{
    [Fact]
    public void EnsureValid_WithoutTemplate_AcceptsFixedRoutingKey()
    {
        RabbitMQDispatcherOptions options = Publisher();

        options.EnsureValid();
        Assert.Empty(options.HeaderFields);
        Assert.Null(options.RoutingKeyTemplate);
    }

    [Fact]
    public void EnsureValid_TemplateWithConfiguredTags_Accepts()
    {
        RabbitMQDispatcherOptions options = Publisher();
        options = new RabbitMQDispatcherOptions
        {
            Host = options.Host,
            UsernameEnvVar = options.UsernameEnvVar,
            PasswordEnvVar = options.PasswordEnvVar,
            Exchange = options.Exchange,
            HeaderFields = [1, 55],
            RoutingKeyTemplate = "fix.<1>.<55>",
        };

        options.EnsureValid();
    }

    [Fact]
    public void EnsureValid_TemplateTagMissingFromHeaderFields_Throws()
    {
        RabbitMQDispatcherOptions options = Publisher();
        options = new RabbitMQDispatcherOptions
        {
            Host = options.Host,
            UsernameEnvVar = options.UsernameEnvVar,
            PasswordEnvVar = options.PasswordEnvVar,
            Exchange = options.Exchange,
            HeaderFields = [1],
            RoutingKeyTemplate = "fix.<1>.<55>",
        };

        ArgumentException error = Assert.Throws<ArgumentException>(() => options.EnsureValid());
        Assert.Contains("55", error.Message);
    }

    [Fact]
    public void EnsureValid_TemplateWithoutPlaceholder_Throws()
    {
        RabbitMQDispatcherOptions options = Publisher();
        options = new RabbitMQDispatcherOptions
        {
            Host = options.Host,
            UsernameEnvVar = options.UsernameEnvVar,
            PasswordEnvVar = options.PasswordEnvVar,
            Exchange = options.Exchange,
            RoutingKeyTemplate = "fix.raw",
        };

        Assert.Throws<ArgumentException>(() => options.EnsureValid());
    }

    [Fact]
    public void EnsureValid_UnclosedPlaceholder_Throws()
    {
        RabbitMQDispatcherOptions options = Publisher();
        options = new RabbitMQDispatcherOptions
        {
            Host = options.Host,
            UsernameEnvVar = options.UsernameEnvVar,
            PasswordEnvVar = options.PasswordEnvVar,
            Exchange = options.Exchange,
            HeaderFields = [1],
            RoutingKeyTemplate = "fix.<1",
        };

        Assert.Throws<ArgumentException>(() => options.EnsureValid());
    }

    private static RabbitMQDispatcherOptions Publisher() =>
        new()
        {
            Host = "127.0.0.1",
            UsernameEnvVar = "RABBIT_USER",
            PasswordEnvVar = "RABBIT_PASS",
            Exchange = "dropcopy",
        };
}
