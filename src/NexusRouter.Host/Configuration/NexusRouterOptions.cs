using System.Globalization;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

using NexusRouter.Audit;
using NexusRouter.SessionLog;

namespace NexusRouter.Host.Configuration;

/// <summary>
/// Raiz da configuração do NexusRouter (<c>NexusRouter</c>).
/// </summary>
public sealed class NexusRouterOptions : IValidateOptions<NexusRouterOptions>
{
    /// <summary>
    /// Sessões FIX de ingresso.
    /// </summary>
    public List<SessionOptions> Sessions { get; init; } = [];

    /// <summary>
    /// Dispatchers de saída (Kafka, RabbitMQ, FIX Acceptor, WebSocket).
    /// </summary>
    public List<DispatcherOptions> Dispatchers { get; init; } = [];

    /// <summary>
    /// Cadastro opcional de clientes autorizados para replay e retenção.
    /// </summary>
    public List<ClientOptions> Clients { get; init; } = [];

    /// <summary>
    /// File session log. Replaces the replay channel.
    /// </summary>
    public SessionLogOptions SessionLog { get; init; } = new();

    /// <summary>
    /// Configuração do cold path de auditoria e sinks.
    /// </summary>
    public AuditOptions Audit { get; init; } = new();

    /// <summary>
    /// Confere a integridade da árvore inteira: sessões, dispatchers, rotas e clientes.
    /// </summary>
    /// <param name="name">Nome das options no container. Pode ser nulo neste estágio.</param>
    /// <param name="options">Configuração raiz já lida.</param>
    /// <returns>Sucesso ou a lista de falhas encontradas.</returns>
    public ValidateOptionsResult Validate(string? name, NexusRouterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> failures = [];

        Dictionary<string, DispatcherOptions> dispatchersById = ValidateDispatchers(options.Dispatchers, failures);
        ValidateSessions(options.Sessions, dispatchersById, failures);
        ValidateClients(options.Clients, dispatchersById, failures);

        if (options.SessionLog is null)
        {
            failures.Add("SessionLog é obrigatório.");
        }
        else
        {
            ConfigurationChecks.Append(failures, nameof(options.SessionLog), options.SessionLog.Validate(null, options.SessionLog));
        }

        if (options.Audit is null)
        {
            failures.Add("Audit é obrigatório.");
        }
        else
        {
            ConfigurationChecks.Append(failures, nameof(options.Audit), options.Audit.Validate(null, options.Audit));
        }

        return ConfigurationChecks.From(failures);
    }

    private static Dictionary<string, DispatcherOptions> ValidateDispatchers(
        List<DispatcherOptions>? dispatchers,
        List<string> failures)
    {
        Dictionary<string, DispatcherOptions> map = new(StringComparer.Ordinal);
        if (dispatchers is null || dispatchers.Count == 0)
        {
            failures.Add("Dispatchers: informe ao menos um dispatcher.");
            return map;
        }

        for (int i = 0; i < dispatchers.Count; i++)
        {
            DispatcherOptions? dispatcher = dispatchers[i];
            if (dispatcher is null)
            {
                failures.Add($"Dispatchers[{i}] é obrigatório.");
                continue;
            }

            ConfigurationChecks.Append(failures, $"Dispatchers[{i}]", dispatcher.Validate(null));
            if (!string.IsNullOrWhiteSpace(dispatcher.Id) && !map.TryAdd(dispatcher.Id, dispatcher))
            {
                failures.Add($"Dispatchers[{i}].Id '{dispatcher.Id}' está duplicado.");
            }
        }

        return map;
    }

    private static void ValidateSessions(
        List<SessionOptions>? sessions,
        Dictionary<string, DispatcherOptions> dispatchersById,
        List<string> failures)
    {
        if (sessions is null || sessions.Count == 0)
        {
            failures.Add("Sessions: informe ao menos uma sessão.");
            return;
        }

        HashSet<string> seenSessionIds = new(StringComparer.Ordinal);
        for (int i = 0; i < sessions.Count; i++)
        {
            SessionOptions? session = sessions[i];
            if (session is null)
            {
                failures.Add($"Sessions[{i}] é obrigatório.");
                continue;
            }

            ConfigurationChecks.Append(failures, $"Sessions[{i}]", session.Validate(null, session));
            if (!string.IsNullOrWhiteSpace(session.SessionId) && !seenSessionIds.Add(session.SessionId))
            {
                failures.Add($"Sessions[{i}].SessionId '{session.SessionId}' está duplicado.");
            }

            if (session.Routes is not null)
            {
                for (int r = 0; r < session.Routes.Count; r++)
                {
                    SessionRouteOptions? route = session.Routes[r];
                    if (route?.DispatcherId is not null && !dispatchersById.ContainsKey(route.DispatcherId))
                    {
                        failures.Add($"Sessions[{i}].Routes[{r}].DispatcherId '{route.DispatcherId}' não existe em Dispatchers.");
                    }
                }
            }
        }
    }

    private static void ValidateClients(
        List<ClientOptions>? clients,
        Dictionary<string, DispatcherOptions> dispatchersById,
        List<string> failures)
    {
        if (clients is null)
        {
            return;
        }

        HashSet<string> seenClientIds = new(StringComparer.Ordinal);
        for (int i = 0; i < clients.Count; i++)
        {
            ClientOptions? client = clients[i];
            if (client is null)
            {
                failures.Add($"Clients[{i}] é obrigatório.");
                continue;
            }

            ConfigurationChecks.Append(failures, $"Clients[{i}]", client.Validate(null, client));
            if (!string.IsNullOrWhiteSpace(client.ClientId) && !seenClientIds.Add(client.ClientId))
            {
                failures.Add($"Clients[{i}].ClientId '{client.ClientId}' está duplicado.");
            }

            if (client.DispatcherId is not null)
            {
                if (!dispatchersById.TryGetValue(client.DispatcherId, out DispatcherOptions? dispatcher))
                {
                    failures.Add($"Clients[{i}].DispatcherId '{client.DispatcherId}' não existe em Dispatchers.");
                }
                else
                {
                    if (dispatcher.Type == DispatcherType.FixOutbound && string.IsNullOrWhiteSpace(client.FixSenderCompId))
                    {
                        failures.Add($"Clients[{i}]: FixSenderCompId é obrigatório para clientes do dispatcher FIX '{client.DispatcherId}'.");
                    }

                    if (dispatcher.Type == DispatcherType.WebSocket && string.IsNullOrWhiteSpace(client.TokenEnvVar))
                    {
                        failures.Add($"Clients[{i}]: TokenEnvVar é obrigatório para clientes do dispatcher WebSocket '{client.DispatcherId}'.");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Converte uma seção de <see cref="IConfiguration"/> em <see cref="NexusRouterOptions"/>,
    /// instanciando as subclasses polimórficas de <see cref="DispatcherOptions"/> e <see cref="AuditSinkOptions"/>.
    /// </summary>
    /// <param name="configuration">Seção raiz <c>NexusRouter</c> ou configuração da aplicação.</param>
    /// <returns>Instância tipada de <see cref="NexusRouterOptions"/>.</returns>
    public static NexusRouterOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        IConfigurationSection root = configuration.GetSection("NexusRouter").Exists()
            ? configuration.GetSection("NexusRouter")
            : (configuration as IConfigurationSection ?? configuration.GetSection("NexusRouter"));

        List<SessionOptions> sessions = root.GetSection("Sessions").Get<List<SessionOptions>>() ?? [];
        List<ClientOptions> clients = root.GetSection("Clients").Get<List<ClientOptions>>() ?? [];
        SessionLogOptions sessionLog = root.GetSection("SessionLog").Get<SessionLogOptions>() ?? new SessionLogOptions();

        List<DispatcherOptions> dispatchers = BindDispatchers(root.GetSection("Dispatchers"));
        AuditOptions audit = BindAudit(root.GetSection("Audit"));

        return new NexusRouterOptions
        {
            Sessions = sessions,
            Dispatchers = dispatchers,
            Clients = clients,
            SessionLog = sessionLog,
            Audit = audit,
        };
    }

    private static List<DispatcherOptions> BindDispatchers(IConfigurationSection section)
    {
        List<DispatcherOptions> list = [];
        foreach (IConfigurationSection child in section.GetChildren())
        {
            string? typeRaw = child["Type"];
            if (!Enum.TryParse(typeRaw, ignoreCase: true, out DispatcherType type))
            {
                continue;
            }

            DispatcherOptions? item = type switch
            {
                DispatcherType.Kafka => child.Get<KafkaDispatcherOptions>(),
                DispatcherType.RabbitMQ => child.Get<RabbitMQDispatcherOptions>(),
                DispatcherType.FixOutbound => child.Get<FixOutboundDispatcherOptions>(),
                DispatcherType.WebSocket => child.Get<WebSocketDispatcherOptions>(),
                _ => null,
            };

            if (item is not null)
            {
                list.Add(item);
            }
        }

        return list;
    }

    private static AuditOptions BindAudit(IConfigurationSection section)
    {
        if (!section.Exists())
        {
            return new AuditOptions();
        }

        int channelCapacity = int.TryParse(section["ChannelCapacity"], out int cap) ? cap : 65536;
        AuditOverflowMode overflowMode = Enum.TryParse(section["OverflowMode"], ignoreCase: true, out AuditOverflowMode ofm) ? ofm : AuditOverflowMode.DropOldest;
        int batchSize = int.TryParse(section["BatchSize"], out int bs) ? bs : 1000;
        int flushIntervalMs = int.TryParse(section["FlushIntervalMs"], out int fi) ? fi : 100;
        AuditPersistMode persistMode = Enum.TryParse(section["PersistMode"], ignoreCase: true, out AuditPersistMode pm) ? pm : AuditPersistMode.Full;
        double sampleRate = double.TryParse(section["SampleRate"], CultureInfo.InvariantCulture, out double sr) ? sr : 1.0;

        List<AuditSinkOptions> sinks = [];
        foreach (IConfigurationSection child in section.GetSection("Sinks").GetChildren())
        {
            string? typeRaw = child["Type"];
            if (!Enum.TryParse(typeRaw, ignoreCase: true, out AuditSinkType type))
            {
                continue;
            }

            AuditSinkOptions? sink = type switch
            {
                AuditSinkType.Serilog => child.Get<SerilogAuditSinkOptions>(),
                AuditSinkType.Postgres => child.Get<PostgresAuditSinkOptions>(),
                _ => null,
            };

            if (sink is not null)
            {
                sinks.Add(sink);
            }
        }

        return new AuditOptions
        {
            ChannelCapacity = channelCapacity,
            OverflowMode = overflowMode,
            BatchSize = batchSize,
            FlushIntervalMs = flushIntervalMs,
            PersistMode = persistMode,
            SampleRate = sampleRate,
            Sinks = sinks,
        };
    }
}
