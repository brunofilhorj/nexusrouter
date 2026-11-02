using Microsoft.Extensions.Options;

namespace NexusRouter.Host.Configuration;

/// <summary>
/// Endereço do PostgreSQL usado pelo sink de auditoria.
/// </summary>
/// <remarks>
/// Usuário e senha não ficam nestas propriedades. <see cref="UsernameEnvVar"/> e <see cref="PasswordEnvVar"/>
/// são só os nomes das variáveis de ambiente lidas na abertura do sink.
/// </remarks>
public sealed class PostgresAuditSinkOptions : AuditSinkOptions, IValidateOptions<PostgresAuditSinkOptions>
{
    /// <inheritdoc />
    public override AuditSinkType Type => AuditSinkType.Postgres;

    /// <summary>
    /// Host do PostgreSQL.
    /// </summary>
    public required string Host { get; init; }

    /// <summary>
    /// Porta TCP. 5432 é o padrão do PostgreSQL.
    /// </summary>
    public int Port { get; init; } = 5432;

    /// <summary>
    /// Nome do database.
    /// </summary>
    public required string Database { get; init; }

    /// <summary>
    /// Nome da variável de ambiente com o usuário.
    /// </summary>
    public required string UsernameEnvVar { get; init; }

    /// <summary>
    /// Nome da variável de ambiente com a senha.
    /// </summary>
    public required string PasswordEnvVar { get; init; }

    /// <summary>
    /// Modo TLS da conexão. Desenvolvimento local usa <see cref="PostgresSslMode.Disable"/>.
    /// </summary>
    public PostgresSslMode SslMode { get; init; } = PostgresSslMode.Disable;

    /// <summary>
    /// Tempo limite, em segundos, da conexão e de cada comando.
    /// </summary>
    public int CommandTimeoutSeconds { get; init; } = 15;

    /// <inheritdoc />
    public override ValidateOptionsResult Validate(string? name) => Validate(name, this);

    /// <summary>
    /// Confere host, porta, database e os nomes das variáveis.
    /// </summary>
    /// <param name="name">Nome das options no container. Pode ser nulo neste estágio.</param>
    /// <param name="options">Sink já lido da configuração.</param>
    /// <returns>Sucesso ou a lista de campos que impedem a subida.</returns>
    public ValidateOptionsResult Validate(string? name, PostgresAuditSinkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> failures = [];
        ConfigurationChecks.Add(failures, ConfigurationChecks.Host(options.Host, nameof(options.Host)));
        ConfigurationChecks.Add(failures, ConfigurationChecks.Port(options.Port, nameof(options.Port)));
        ConfigurationChecks.Add(failures, ConfigurationChecks.Required(options.Database, nameof(options.Database)));
        ConfigurationChecks.Add(failures, ConfigurationChecks.EnvVar(options.UsernameEnvVar, nameof(options.UsernameEnvVar), required: true));
        ConfigurationChecks.Add(failures, ConfigurationChecks.EnvVar(options.PasswordEnvVar, nameof(options.PasswordEnvVar), required: true));
        ConfigurationChecks.Add(failures, ConfigurationChecks.Defined(options.SslMode, nameof(options.SslMode)));
        ConfigurationChecks.Add(
            failures,
            ConfigurationChecks.Positive(options.CommandTimeoutSeconds, nameof(options.CommandTimeoutSeconds)));
        return ConfigurationChecks.From(failures);
    }
}
