namespace NexusRouter.Host.Configuration;

/// <summary>
/// Discriminador de <see cref="AuditSinkOptions"/> na lista <c>Audit:Sinks</c>.
/// </summary>
public enum AuditSinkType
{
    /// <summary>
    /// Arquivo rolling do Serilog. O valor na configuração é <c>Serilog</c>.
    /// </summary>
    Serilog = 0,

    /// <summary>
    /// Hypertable no PostgreSQL. O valor na configuração é <c>Postgres</c>.
    /// </summary>
    Postgres = 1,
}
