using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using NexusRouter.Audit;
using NexusRouter.Domain.Abstractions;
using NexusRouter.Domain.Models;
using NexusRouter.Domain.Pooling;
using NexusRouter.Egress.Fix;
using NexusRouter.Egress.Kafka;
using NexusRouter.Egress.RabbitMQ;
using NexusRouter.Host.Extensions;
using NexusRouter.Ingress.Decoders;
using NexusRouter.Ingress.Sessions;
using NexusRouter.Router;

namespace NexusRouter.Host.Tests;

/// <summary>
/// Testes das extensões de injeção de dependência do NexusRouter.
/// </summary>
public sealed class ServiceCollectionExtensionsTests
{
    private static IConfiguration CreateConfiguration()
    {
        var dict = new Dictionary<string, string?>
        {
            ["NexusRouter:Sessions:0:SessionId"] = "B3_EQUITIES",
            ["NexusRouter:Sessions:0:Host"] = "127.0.0.1",
            ["NexusRouter:Sessions:0:Port"] = "9800",
            ["NexusRouter:Sessions:0:SenderCompId"] = "NEXUS",
            ["NexusRouter:Sessions:0:TargetCompId"] = "B3",
            ["NexusRouter:Sessions:0:HeartbeatIntervalSeconds"] = "20",
            ["NexusRouter:Sessions:0:DeliveryMode"] = "PassThrough",
            ["NexusRouter:Sessions:0:FrameDecoder"] = "Fix44",
            ["NexusRouter:Sessions:0:Routes:0:DispatcherId"] = "KAFKA_DISPATCHER",
            ["NexusRouter:Sessions:0:Routes:1:DispatcherId"] = "RABBIT_DISPATCHER",

            ["NexusRouter:Dispatchers:0:Id"] = "KAFKA_DISPATCHER",
            ["NexusRouter:Dispatchers:0:Type"] = "Kafka",
            ["NexusRouter:Dispatchers:0:BootstrapServers"] = "localhost:9092",
            ["NexusRouter:Dispatchers:0:Topic"] = "b3-orders",
            ["NexusRouter:Dispatchers:0:ClientId"] = "nexus-kafka",
            ["NexusRouter:Dispatchers:0:Acks"] = "1",

            ["NexusRouter:Dispatchers:1:Id"] = "RABBIT_DISPATCHER",
            ["NexusRouter:Dispatchers:1:Type"] = "RabbitMQ",
            ["NexusRouter:Dispatchers:1:Host"] = "localhost",
            ["NexusRouter:Dispatchers:1:Port"] = "5672",
            ["NexusRouter:Dispatchers:1:UsernameEnvVar"] = "RABBIT_USER",
            ["NexusRouter:Dispatchers:1:PasswordEnvVar"] = "RABBIT_PASS",
            ["NexusRouter:Dispatchers:1:Exchange"] = "orders-exchange",

            ["NexusRouter:SessionLog:Directory"] = Path.Combine(Path.GetTempPath(), "nexusrouter-host-tests"),
            ["NexusRouter:SessionLog:SegmentSizeMB"] = "1",
            ["NexusRouter:SessionLog:IndexSlotsPerSegment"] = "1000",

            ["NexusRouter:Dispatchers:2:Id"] = "FIX_DISPATCHER",
            ["NexusRouter:Dispatchers:2:Type"] = "FixOutbound",
            ["NexusRouter:Dispatchers:2:ListenAddress"] = "127.0.0.1",
            ["NexusRouter:Dispatchers:2:ListenPort"] = "9801",
            ["NexusRouter:Dispatchers:2:SenderCompId"] = "NEXUS_ACCEPTOR",
            ["NexusRouter:Dispatchers:2:BeginString"] = "FIX.4.4",

            ["NexusRouter:Audit:ChannelCapacity"] = "32768",
            ["NexusRouter:Audit:OverflowMode"] = "DropOldest",
            ["NexusRouter:Audit:BatchSize"] = "500",
            ["NexusRouter:Audit:FlushIntervalMs"] = "50",
            ["NexusRouter:Audit:PersistMode"] = "Full",
            ["NexusRouter:Audit:SampleRate"] = "1.0",
            ["NexusRouter:Audit:Sinks:0:Type"] = "Serilog",
            ["NexusRouter:Audit:Sinks:0:FilePath"] = "/tmp/test-audit.log",
            ["NexusRouter:Audit:Sinks:0:RollingInterval"] = "Hour",
        };

        return new ConfigurationBuilder()
            .AddInMemoryCollection(dict)
            .Build();
    }

    [Fact]
    public void AddNexusRouterAudit_RegistersRequiredServices()
    {
        IConfiguration config = CreateConfiguration();
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddNexusRouterAudit(config);

        using ServiceProvider sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetService<AuditChannelSingleton>());
        Assert.NotNull(sp.GetService<AuditChannelWriter>());
        Assert.NotNull(sp.GetService<AuditWriterService>());

        IEnumerable<IHostedService> hostedServices = sp.GetServices<IHostedService>();
        Assert.Contains(hostedServices, s => s is AuditWriterService);
        Assert.Contains(hostedServices, s => s is DispatcherAuditObserver);
    }

    [Fact]
    public void AddNexusRouterEgress_RegistersConcreteAndInterfaceDispatchers()
    {
        IConfiguration config = CreateConfiguration();
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddNexusRouterSessionLog(config);
        services.AddNexusRouterEgress(config);

        using ServiceProvider sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetService<KafkaMessageDispatcher>());
        Assert.NotNull(sp.GetService<RabbitMQMessageDispatcher>());
        Assert.NotNull(sp.GetService<FixOutboundDispatcher>());

        IEnumerable<IMessageDispatcher> dispatchers = sp.GetServices<IMessageDispatcher>();
        List<string> ids = dispatchers.Select(d => d.DispatcherId).ToList();

        Assert.Contains("KAFKA_DISPATCHER", ids);
        Assert.Contains("RABBIT_DISPATCHER", ids);
        Assert.Contains("FIX_DISPATCHER", ids);
    }

    [Fact]
    public void AddNexusRouterRouter_RegistersRouterComponentsAndBuildsRouteTable()
    {
        IConfiguration config = CreateConfiguration();
        var services = new ServiceCollection();
        services.AddLogging();

        // Router precisa de Audit (AuditChannelSingleton) e Egress (IMessageDispatcher)
        services.AddNexusRouterAudit(config);
        services.AddNexusRouterSessionLog(config);
        services.AddNexusRouterEgress(config);
        services.AddNexusRouterRouter(config);

        using ServiceProvider sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetService<DispatchPayloadPool>());
        Assert.NotNull(sp.GetService<FanOutEngine>());
        Assert.NotNull(sp.GetService<RouteResolver>());
        Assert.NotNull(sp.GetService<RouterEngine>());
        Assert.NotNull(sp.GetService<IMessageRouter>());

        RouteResolver resolver = sp.GetRequiredService<RouteResolver>();
        Assert.True(resolver.TryResolve(new SessionId("B3_EQUITIES"), out RouteContext route));
        Assert.Equal(2, route.Dispatchers.Length);
    }

    [Fact]
    public void AddNexusRouterIngress_RegistersDecodersAndSessionHostedServices()
    {
        IConfiguration config = CreateConfiguration();
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddNexusRouterAudit(config);
        services.AddNexusRouterSessionLog(config);
        services.AddNexusRouterEgress(config);
        services.AddNexusRouterRouter(config);
        services.AddNexusRouterIngress(config);

        using ServiceProvider sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetService<Fix44FrameDecoder>());
        Assert.NotNull(sp.GetService<Fix50FrameDecoder>());
        Assert.NotNull(sp.GetService<SbeFrameDecoder>());

        IEnumerable<IHostedService> hostedServices = sp.GetServices<IHostedService>();
        Assert.Contains(hostedServices, s => s is FixSession);
    }

    [Fact]
    public void FullPipelineWiring_ResolvesWithoutExceptions()
    {
        IConfiguration config = CreateConfiguration();
        var services = new ServiceCollection();
        services.AddLogging();

        // Ordem canônica do design: Audit -> Egress -> Router -> Ingress
        services.AddNexusRouterAudit(config);
        services.AddNexusRouterSessionLog(config);
        services.AddNexusRouterEgress(config);
        services.AddNexusRouterRouter(config);
        services.AddNexusRouterIngress(config);

        using ServiceProvider sp = services.BuildServiceProvider();

        // Verifica que todos os IHostedService instanciam sem lançar exceção
        IEnumerable<IHostedService> hostedServices = sp.GetServices<IHostedService>().ToList();
        Assert.True(hostedServices.Count() >= 3); // FixSession, AuditWriterService, DispatcherAuditObserver
    }
}

