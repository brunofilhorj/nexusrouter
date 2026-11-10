using System.Collections.Immutable;
using System.Threading.Channels;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using NexusRouter.Audit;
using NexusRouter.Audit.Sinks;
using NexusRouter.Domain.Abstractions;
using NexusRouter.Domain.Models;
using NexusRouter.Domain.Pooling;
using NexusRouter.Domain.Secrets;
using NexusRouter.Egress;
using NexusRouter.Egress.Fix;
using NexusRouter.Egress.Kafka;
using NexusRouter.Egress.RabbitMQ;
using NexusRouter.Host.Configuration;
using NexusRouter.Ingress.Decoders;
using NexusRouter.Ingress.Sessions;
using NexusRouter.Router;
using NexusRouter.SessionLog;

using IngressSessionOptions = NexusRouter.Ingress.Sessions.SessionOptions;
using HostSessionOptions = NexusRouter.Host.Configuration.SessionOptions;
using EgressKafkaOptions = NexusRouter.Egress.Kafka.KafkaDispatcherOptions;
using EgressRabbitMQOptions = NexusRouter.Egress.RabbitMQ.RabbitMQDispatcherOptions;
using EgressFixOptions = NexusRouter.Egress.Fix.FixOutboundDispatcherOptions;
using AuditSerilogOptions = NexusRouter.Audit.SerilogAuditSinkOptions;
using AuditPostgresOptions = NexusRouter.Audit.PostgresAuditSinkOptions;
using AuditCoreOptions = NexusRouter.Audit.AuditOptions;
using DomainRouteContext = NexusRouter.Domain.Models.RouteContext;

namespace NexusRouter.Host.Extensions;

/// <summary>
/// Métodos de extensão de <see cref="IServiceCollection"/> que conectam os serviços do NexusRouter.
/// </summary>
/// <remarks>
/// Cada método cobre um tier do pipeline: Ingress, Router, Egress e Audit.
/// A ordem de chamada em <c>Program.cs</c> define a ordem de registro e,
/// consequentemente, a ordem inversa de shutdown dos <see cref="Microsoft.Extensions.Hosting.IHostedService"/>s.
/// </remarks>
public static class ServiceCollectionExtensions
{
    // ─────────────────────────────────────────────────────────────────────────
    // Ingress
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Registra o tier de ingresso: frame decoders, política de reconexão e uma sessão FIX por entrada configurada.
    /// </summary>
    /// <param name="services">Container de DI.</param>
    /// <param name="configuration">Configuração da aplicação.</param>
    /// <returns>O mesmo <paramref name="services"/> para encadeamento.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> ou <paramref name="configuration"/> é nulo.</exception>
    public static IServiceCollection AddNexusRouterIngress(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        AddSecretResolver(services, configuration);
        NexusRouterOptions options = NexusRouterOptions.FromConfiguration(configuration);

        // Frame decoders — singletons sem estado, compartilháveis entre sessões.
        services.AddSingleton<Fix44FrameDecoder>();
        services.AddSingleton<Fix50FrameDecoder>();
        services.AddSingleton<SbeFrameDecoder>();
        services.AddSingleton<FixSessionMetrics>();

        // Registra uma FixSession como IHostedService por sessão configurada.
        foreach (HostSessionOptions sessionOptions in options.Sessions)
        {
            // Captura local para a lambda de factory.
            HostSessionOptions captured = sessionOptions;

            services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(sp =>
            {
                ISessionFrameDecoder decoder = ResolveDecoder(sp, captured.FrameDecoder);

                ReconnectPolicyOptions policy = captured.ReconnectPolicy;
                var reconnect = new ExponentialBackoffReconnectPolicy(
                    TimeSpan.FromMilliseconds(policy.BaseDelayMs),
                    TimeSpan.FromMilliseconds(policy.MaxDelayMs),
                    policy.JitterFactor);

                var ingressOptions = new IngressSessionOptions
                {
                    SessionId = captured.SessionId,
                    Host = captured.Host,
                    Port = captured.Port,
                    SenderCompId = captured.SenderCompId,
                    TargetCompId = captured.TargetCompId,
                    BeginString = MapBeginString(captured.FrameDecoder),
                    HeartBtInt = captured.HeartbeatIntervalSeconds,
                    LogonText = captured.LogonText,
                    PasswordEnvVar = captured.PasswordEnvVar,
                    CancelOnDisconnectType = captured.CancelOnDisconnectType,
                    CodTimeoutWindow = captured.CodTimeoutWindow,
                    ResetSeqNumFlag = captured.ResetSeqNumFlag,
                    MissedHeartbeatThreshold = 2,
                    LogonTimeout = TimeSpan.FromSeconds(10),
                    TlsEnabled = captured.Tls.Enabled,
                    TlsCertificatePath = captured.Tls.CertificatePath,
                    TlsCertificatePasswordEnvVar = captured.Tls.CertificatePasswordEnvVar,
                    ShutdownTimeoutSeconds = captured.ShutdownTimeoutSeconds,
                    SendingTimeTolerance = TimeSpan.FromSeconds(captured.SendingTimeToleranceSeconds),
                };

                IMessageRouter router = sp.GetRequiredService<IMessageRouter>();
                AuditChannelSingleton auditSingleton = sp.GetRequiredService<AuditChannelSingleton>();
                ChannelWriter<AuditRecord> auditWriter = auditSingleton.Instance.Writer;
                ISecretResolver secretResolver = sp.GetRequiredService<ISecretResolver>();
                ILogger<FixSession> logger = sp.GetRequiredService<ILogger<FixSession>>();
                ISessionLog sessionLog = sp.GetRequiredService<ISessionLog>();

                FixSessionMetrics metrics = sp.GetRequiredService<FixSessionMetrics>();
                return new FixSession(ingressOptions, decoder, reconnect, router, auditWriter, secretResolver, logger, sessionLog, metrics);
            });
        }

        return services;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Router
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Registra o tier do router: tabela de rotas congelada, <see cref="RouterEngine"/>, <see cref="FanOutEngine"/> e o pool.
    /// </summary>
    /// <param name="services">Container de DI.</param>
    /// <param name="configuration">Configuração da aplicação.</param>
    /// <returns>O mesmo <paramref name="services"/> para encadeamento.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> ou <paramref name="configuration"/> é nulo.</exception>
    /// <remarks>
    /// Deve ser chamado <b>depois</b> de <see cref="AddNexusRouterEgress"/> para que os
    /// <see cref="IMessageDispatcher"/>s já estejam no container quando a tabela de rotas for montada.
    /// </remarks>
    public static IServiceCollection AddNexusRouterRouter(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        NexusRouterOptions options = NexusRouterOptions.FromConfiguration(configuration);

        services.TryAddSingleton<DispatchPayloadPool>();
        services.AddSingleton<NexusRouterMetrics>();

        // FanOutEngine recebe Channel<AuditRecord> diretamente via AuditChannelSingleton.
        services.AddSingleton<FanOutEngine>(sp =>
        {
            AuditChannelSingleton auditSingleton = sp.GetRequiredService<AuditChannelSingleton>();
            DispatchPayloadPool pool = sp.GetRequiredService<DispatchPayloadPool>();
            ILogger<FanOutEngine> logger = sp.GetRequiredService<ILogger<FanOutEngine>>();
            NexusRouterMetrics metrics = sp.GetRequiredService<NexusRouterMetrics>();
            return new FanOutEngine(auditSingleton.Instance, pool, logger, metrics);
        });

        // RouteResolver: monta a tabela de roteamento congelada resolvendo dispatchers pelo Id.
        services.AddSingleton<RouteResolver>(sp =>
        {
            IEnumerable<IMessageDispatcher> allDispatchers = sp.GetServices<IMessageDispatcher>();
            Dictionary<string, IMessageDispatcher> byId = new(StringComparer.Ordinal);
            foreach (IMessageDispatcher d in allDispatchers)
            {
                byId.TryAdd(d.DispatcherId, d);
            }

            ILogger<RouteResolver> logger = sp.GetRequiredService<ILogger<RouteResolver>>();

            List<DomainRouteContext> routes = [];
            foreach (HostSessionOptions sessionOpts in options.Sessions)
            {
                var sessionId = new SessionId(sessionOpts.SessionId);
                ImmutableArray<IMessageDispatcher>.Builder builders =
                    ImmutableArray.CreateBuilder<IMessageDispatcher>(sessionOpts.Routes.Count);

                foreach (SessionRouteOptions route in sessionOpts.Routes)
                {
                    if (byId.TryGetValue(route.DispatcherId, out IMessageDispatcher? dispatcher))
                    {
                        builders.Add(dispatcher);
                    }
                    else
                    {
                        ILogger fallbackLogger = sp.GetRequiredService<ILogger<RouteResolver>>();
                        fallbackLogger.LogWarning(
                            "Dispatcher '{DispatcherId}' referenciado pela sessão '{SessionId}' não foi registrado.",
                            route.DispatcherId,
                            sessionOpts.SessionId);
                    }
                }

                routes.Add(new DomainRouteContext(sessionId, sessionOpts.DeliveryMode, builders.ToImmutable()));
            }

            return new RouteResolver(routes, logger);
        });

        services.AddSingleton<RouterEngine>(sp =>
        {
            RouteResolver resolver = sp.GetRequiredService<RouteResolver>();
            FanOutEngine fanOut = sp.GetRequiredService<FanOutEngine>();
            DispatchPayloadPool pool = sp.GetRequiredService<DispatchPayloadPool>();
            ILogger<RouterEngine> logger = sp.GetRequiredService<ILogger<RouterEngine>>();
            NexusRouterMetrics metrics = sp.GetRequiredService<NexusRouterMetrics>();
            return new RouterEngine(resolver, fanOut, pool, logger, metrics);
        });
        services.AddSingleton<IMessageRouter>(sp => sp.GetRequiredService<RouterEngine>());

        return services;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Session log
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Registers the session log so it stops after the dispatchers drain and before audit drains.
    /// </summary>
    /// <param name="services">DI container.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddNexusRouterSessionLog(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        NexusRouterOptions options = NexusRouterOptions.FromConfiguration(configuration);
        services.AddSingleton(options.SessionLog);
        services.AddSingleton<SessionLogManager>();
        services.AddSingleton<ISessionLog>(sp => sp.GetRequiredService<SessionLogManager>());
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<SessionLogManager>());
        return services;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Egress
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Registra o tier de saída: instancia cada dispatcher configurado e expõe-o como <see cref="IMessageDispatcher"/>.
    /// </summary>
    /// <param name="services">Container de DI.</param>
    /// <param name="configuration">Configuração da aplicação.</param>
    /// <returns>O mesmo <paramref name="services"/> para encadeamento.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> ou <paramref name="configuration"/> é nulo.</exception>
    public static IServiceCollection AddNexusRouterEgress(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        AddSecretResolver(services, configuration);
        services.TryAddSingleton<DispatchPayloadPool>();
        NexusRouterOptions options = NexusRouterOptions.FromConfiguration(configuration);

        foreach (DispatcherOptions dispatcherOptions in options.Dispatchers)
        {
            switch (dispatcherOptions)
            {
                case Configuration.KafkaDispatcherOptions kafka:
                    RegisterKafka(services, kafka);
                    break;

                case Configuration.RabbitMQDispatcherOptions rabbit:
                    RegisterRabbitMQ(services, rabbit);
                    break;

                case Configuration.FixOutboundDispatcherOptions fix:
                    RegisterFixOutbound(services, fix);
                    break;

                case Configuration.WebSocketDispatcherOptions ws:
                    RegisterWebSocket(services, ws);
                    break;
            }
        }

        // Registra o serviço que gerencia o ciclo de vida (Start/Stop) de todos os dispatchers.
        // Está no tier de Egress para parar antes do Audit writer no shutdown.
        services.AddSingleton<IHostedService, DispatcherLifetimeService>();

        return services;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Audit
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Registra o tier de auditoria: channel singleton, writer, sinks configurados e o serviço hospedado de drenagem.
    /// </summary>
    /// <param name="services">Container de DI.</param>
    /// <param name="configuration">Configuração da aplicação.</param>
    /// <returns>O mesmo <paramref name="services"/> para encadeamento.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> ou <paramref name="configuration"/> é nulo.</exception>
    public static IServiceCollection AddNexusRouterAudit(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        AddSecretResolver(services, configuration);
        NexusRouterOptions options = NexusRouterOptions.FromConfiguration(configuration);
        Configuration.AuditOptions auditConfig = options.Audit;

        // Core audit options passadas aos sinks.
        AuditCoreOptions coreOptions = MapAuditOptions(auditConfig);

        services.AddSingleton(coreOptions);
        services.AddSingleton<AuditChannelSingleton>(sp =>
        {
            AuditCoreOptions o = sp.GetRequiredService<AuditCoreOptions>();
            return new AuditChannelSingleton(o);
        });

        services.AddSingleton<AuditChannelWriter>();

        // Registra os sinks configurados.
        List<IAuditSink> sinksForWriter = [];

        foreach (AuditSinkOptions sinkOptions in auditConfig.Sinks)
        {
            switch (sinkOptions)
            {
                case Configuration.SerilogAuditSinkOptions serilogConfig:
                    services.AddSingleton<IAuditSink>(sp =>
                    {
                        var sinkOpts = new AuditSerilogOptions
                        {
                            FilePath = serilogConfig.FilePath,
                            RollingInterval = MapRollingInterval(serilogConfig.RollingInterval),
                        };
                        return new SerilogAuditSink(sinkOpts);
                    });
                    break;

                case Configuration.PostgresAuditSinkOptions postgresConfig:
                    services.AddSingleton<IAuditSink>(sp =>
                    {
                        var sinkOpts = new AuditPostgresOptions
                        {
                            Host = postgresConfig.Host,
                            Port = postgresConfig.Port,
                            Database = postgresConfig.Database,
                            UsernameEnvVar = postgresConfig.UsernameEnvVar,
                            PasswordEnvVar = postgresConfig.PasswordEnvVar,
                            SslMode = MapNpgsqlSslMode(postgresConfig.SslMode),
                            CommandTimeoutSeconds = postgresConfig.CommandTimeoutSeconds,
                        };
                        AuditCoreOptions core = sp.GetRequiredService<AuditCoreOptions>();
                        ILogger<PostgresAuditSink> logger = sp.GetRequiredService<ILogger<PostgresAuditSink>>();
                        ISecretResolver secretResolver = sp.GetRequiredService<ISecretResolver>();
                        return new PostgresAuditSink(sinkOpts, core, logger, secretResolver);
                    });
                    break;
            }
        }

        // AuditWriterService recebe a lista de sinks como IReadOnlyList<IAuditSink>.
        services.AddSingleton<AuditWriterService>(sp =>
        {
            AuditChannelSingleton singleton = sp.GetRequiredService<AuditChannelSingleton>();
            IReadOnlyList<IAuditSink> sinks = sp.GetServices<IAuditSink>().ToList().AsReadOnly();
            AuditCoreOptions core = sp.GetRequiredService<AuditCoreOptions>();
            ILogger<AuditWriterService> logger = sp.GetRequiredService<ILogger<AuditWriterService>>();
            return new AuditWriterService(singleton.Instance, sinks, logger, core);
        });

        services.AddSingleton<IHostedService>(
            sp => sp.GetRequiredService<AuditWriterService>());

        // DispatcherAuditObserver assina os erros de dispatch de todos os dispatchers.
        services.AddSingleton<DispatcherAuditObserver>(sp =>
        {
            AuditChannelWriter writer = sp.GetRequiredService<AuditChannelWriter>();
            IEnumerable<IMessageDispatcher> dispatchers = sp.GetServices<IMessageDispatcher>();
            ILogger<DispatcherAuditObserver> logger = sp.GetRequiredService<ILogger<DispatcherAuditObserver>>();
            return new DispatcherAuditObserver(writer, dispatchers, logger);
        });

        services.AddSingleton<IHostedService>(
            sp => sp.GetRequiredService<DispatcherAuditObserver>());

        return services;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers privados — Secrets
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Registra o <see cref="ISecretResolver"/> sobre a <see cref="IConfiguration"/> da aplicação.
    /// </summary>
    /// <remarks>
    /// Idempotente via <c>TryAddSingleton</c>: pode ser chamado por cada tier sem registrar o resolvedor mais de uma vez.
    /// O resolvedor lê segredos pelas chaves de lookup (ex.: <c>RABBITMQ_DEFAULT_PASS</c>), que o provider de
    /// variáveis de ambiente do .NET expõe por nome plano — preservando compatibilidade com env var do processo.
    /// </remarks>
    private static void AddSecretResolver(IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton<ISecretResolver>(_ => new ConfigurationSecretResolver(configuration));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers privados — Ingress
    // ─────────────────────────────────────────────────────────────────────────

    private static ISessionFrameDecoder ResolveDecoder(IServiceProvider sp, FrameDecoderKind kind) =>
        kind switch
        {
            FrameDecoderKind.Fix44 => sp.GetRequiredService<Fix44FrameDecoder>(),
            FrameDecoderKind.Fix50 => sp.GetRequiredService<Fix50FrameDecoder>(),
            FrameDecoderKind.Sbe => sp.GetRequiredService<SbeFrameDecoder>(),
            _ => throw new InvalidOperationException($"FrameDecoderKind desconhecido: {kind}."),
        };

    private static string MapBeginString(FrameDecoderKind kind) =>
        kind switch
        {
            FrameDecoderKind.Fix44 => "FIX.4.4",
            FrameDecoderKind.Fix50 => "FIX.5.0",
            FrameDecoderKind.Sbe => "FIX.4.4", // SBE stub — BeginString irrelevante.
            _ => "FIX.4.4",
        };

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers privados — Egress
    // ─────────────────────────────────────────────────────────────────────────

    private static void RegisterKafka(
        IServiceCollection services,
        Configuration.KafkaDispatcherOptions config)
    {
        services.AddSingleton<KafkaMessageDispatcher>(sp =>
        {
            var opts = new EgressKafkaOptions
            {
                BootstrapServers = config.BootstrapServers,
                Topic = config.Topic,
                ClientId = config.ClientId,
                Acks = config.Acks,
                CursorAckMode = config.CursorAckMode,
                SecurityProtocol = config.SecurityProtocol,
                SaslMechanism = config.SaslMechanism,
                SaslUsernameEnvVar = config.SaslUsernameEnvVar,
                SaslPasswordEnvVar = config.SaslPasswordEnvVar,
                SslCaLocation = config.SslCaLocation,
                EnableSslCertificateVerification = config.EnableSslCertificateVerification,
                ChannelCapacity = config.ChannelCapacity,
                PartitionStrategy = config.PartitionStrategy,
                PartitionCount = config.PartitionCount,
            };
            ILogger<KafkaMessageDispatcher> logger = sp.GetRequiredService<ILogger<KafkaMessageDispatcher>>();
            DispatchPayloadPool payloadPool = sp.GetRequiredService<DispatchPayloadPool>();
            ISecretResolver secretResolver = sp.GetRequiredService<ISecretResolver>();
            BrokerCursorTracker? tracker = CreateBrokerTracker(sp, config.Id, opts.CursorAckMode);
            var dispatcher = new KafkaMessageDispatcher(
                config.Id,
                opts,
                logger,
                payloadPool,
                secretResolver,
                catchUp: CreateCatchUp(sp, config.Id, payloadPool, tracker),
                tracker: tracker);
            return TrackLag(sp, dispatcher, () => dispatcher.Lag);
        });

        // Expõe como IMessageDispatcher para que o RouteResolver e o DispatcherAuditObserver resolvam.
        services.AddSingleton<IMessageDispatcher>(sp => sp.GetRequiredService<KafkaMessageDispatcher>());
    }

    private static void RegisterRabbitMQ(
        IServiceCollection services,
        Configuration.RabbitMQDispatcherOptions config)
    {
        services.AddSingleton<RabbitMQMessageDispatcher>(sp =>
        {
            var opts = new EgressRabbitMQOptions
            {
                Host = config.Host,
                Port = config.Port,
                VirtualHost = config.VirtualHost,
                UsernameEnvVar = config.UsernameEnvVar,
                PasswordEnvVar = config.PasswordEnvVar,
                Exchange = config.Exchange,
                RoutingKey = config.RoutingKey,
                ChannelCapacity = config.ChannelCapacity,
                PrefetchCount = config.PrefetchCount,
                CursorAckMode = config.CursorAckMode,
            };
            ILogger<RabbitMQMessageDispatcher> logger = sp.GetRequiredService<ILogger<RabbitMQMessageDispatcher>>();
            DispatchPayloadPool payloadPool = sp.GetRequiredService<DispatchPayloadPool>();
            ISecretResolver secretResolver = sp.GetRequiredService<ISecretResolver>();
            BrokerCursorTracker? tracker = CreateBrokerTracker(sp, config.Id, opts.CursorAckMode);
            var dispatcher = new RabbitMQMessageDispatcher(
                config.Id,
                opts,
                logger,
                payloadPool,
                secretResolver,
                CreateCatchUp(sp, config.Id, payloadPool, tracker),
                tracker);
            return TrackLag(sp, dispatcher, () => dispatcher.Lag);
        });

        services.AddSingleton<IMessageDispatcher>(sp => sp.GetRequiredService<RabbitMQMessageDispatcher>());
    }

    private static void RegisterFixOutbound(
        IServiceCollection services,
        Configuration.FixOutboundDispatcherOptions config)
    {
        services.AddSingleton<FixOutboundDispatcher>(sp =>
        {
            var opts = new EgressFixOptions
            {
                ListenAddress = config.ListenAddress,
                ListenPort = config.ListenPort,
                SenderCompId = config.SenderCompId,
                BeginString = config.BeginString,
                AllowedSenderCompIds = config.AllowedSenderCompIds,
                HeartBtInt = config.HeartBtInt,
                LogonTimeout = TimeSpan.FromSeconds(config.LogonTimeoutSeconds),
                ChannelCapacity = config.ChannelCapacity,
                TlsEnabled = config.Tls.Enabled,
                TlsCertificatePath = config.Tls.CertificatePath,
                TlsCertificatePasswordEnvVar = config.Tls.CertificatePasswordEnvVar,
                TlsRequireClientCertificate = config.RequireClientCertificate,
                AcceptRetryBaseDelay = TimeSpan.FromMilliseconds(config.AcceptRetryBaseDelayMs),
                AcceptRetryMaxDelay = TimeSpan.FromMilliseconds(config.AcceptRetryMaxDelayMs),
                AcceptRetryJitterFactor = config.AcceptRetryJitterFactor,
                Recovery = config.Recovery,
                ClientOverrides = config.ClientOverrides,
            };
            ILogger<FixOutboundDispatcher> logger = sp.GetRequiredService<ILogger<FixOutboundDispatcher>>();
            DispatchPayloadPool payloadPool = sp.GetRequiredService<DispatchPayloadPool>();
            ISecretResolver secretResolver = sp.GetRequiredService<ISecretResolver>();
            ISessionLog sessionLog = sp.GetRequiredService<ISessionLog>();
            var dispatcher = new FixOutboundDispatcher(
                config.Id,
                opts,
                logger,
                payloadPool,
                secretResolver,
                sessionLog,
                CreateCatchUp(sp, config.Id, payloadPool));
            return TrackLag(sp, dispatcher, () => dispatcher.Lag);
        });

        services.AddSingleton<IMessageDispatcher>(sp => sp.GetRequiredService<FixOutboundDispatcher>());
    }

    private static BrokerCursorTracker? CreateBrokerTracker(
        IServiceProvider services,
        string dispatcherId,
        CursorAckMode mode)
    {
        ISessionLog? log = services.GetService<ISessionLog>();
        if (log is null || services.GetService<SessionLogOptions>() is null)
        {
            return null;
        }

        return new BrokerCursorTracker(log.OpenBroker(dispatcherId), mode);
    }

    private static StreamCatchUp? CreateCatchUp(
        IServiceProvider services,
        string dispatcherId,
        DispatchPayloadPool pool,
        IConsumerState? state = null)
    {
        ISessionLog? log = services.GetService<ISessionLog>();
        SessionLogOptions? options = services.GetService<SessionLogOptions>();
        if (log is null || options is null)
        {
            return null;
        }

        return new StreamCatchUp(log.Reader, state ?? log.OpenBroker(dispatcherId), pool, options.CatchUpIdleMs);
    }

    private static TDispatcher TrackLag<TDispatcher>(IServiceProvider services, TDispatcher dispatcher, Func<long> lag)
        where TDispatcher : IMessageDispatcher
    {
        NexusRouterMetrics? metrics = services.GetService<NexusRouterMetrics>();
        metrics?.RegisterLag(dispatcher.DispatcherId, lag);
        return dispatcher;
    }

    private static void RegisterWebSocket(
        IServiceCollection services,
        Configuration.WebSocketDispatcherOptions config)
    {
        // WebSocketMessageDispatcher ainda não está implementado (T-28).
        // O registro é um stub que lança exceção descritiva ao tentar resolver,
        // para não bloquear builds sem a implementação.
        services.AddSingleton<IMessageDispatcher>(_ =>
            throw new NotImplementedException(
                $"WebSocketMessageDispatcher para o dispatcher '{config.Id}' ainda não está implementado (T-28)."));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers privados — Audit
    // ─────────────────────────────────────────────────────────────────────────

    private static AuditCoreOptions MapAuditOptions(Configuration.AuditOptions config) =>
        new()
        {
            ChannelCapacity = config.ChannelCapacity,
            OverflowMode = config.OverflowMode,
            BatchSize = config.BatchSize,
            FlushIntervalMs = config.FlushIntervalMs,
            PersistMode = config.PersistMode,
            SampleRate = config.SampleRate,
        };

    private static Serilog.RollingInterval MapRollingInterval(AuditFileRollingInterval interval) =>
        interval switch
        {
            AuditFileRollingInterval.Infinite => Serilog.RollingInterval.Infinite,
            AuditFileRollingInterval.Year => Serilog.RollingInterval.Year,
            AuditFileRollingInterval.Month => Serilog.RollingInterval.Month,
            AuditFileRollingInterval.Day => Serilog.RollingInterval.Day,
            AuditFileRollingInterval.Hour => Serilog.RollingInterval.Hour,
            AuditFileRollingInterval.Minute => Serilog.RollingInterval.Minute,
            _ => Serilog.RollingInterval.Hour,
        };

    private static Npgsql.SslMode MapNpgsqlSslMode(PostgresSslMode mode) =>
        mode switch
        {
            PostgresSslMode.Disable => Npgsql.SslMode.Disable,
            PostgresSslMode.Allow => Npgsql.SslMode.Allow,
            PostgresSslMode.Prefer => Npgsql.SslMode.Prefer,
            PostgresSslMode.Require => Npgsql.SslMode.Require,
            PostgresSslMode.VerifyCA => Npgsql.SslMode.VerifyCA,
            PostgresSslMode.VerifyFull => Npgsql.SslMode.VerifyFull,
            _ => Npgsql.SslMode.Disable,
        };
}
