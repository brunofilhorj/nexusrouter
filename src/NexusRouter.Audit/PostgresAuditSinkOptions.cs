using Npgsql;

namespace NexusRouter.Audit;

/// <summary>
/// Endereço do PostgreSQL usado pelo sink de auditoria.
/// </summary>
/// <remarks>
/// Usuário e senha não ficam nestas propriedades. <see cref="UsernameEnvVar"/> e <see cref="PasswordEnvVar"/>
/// são só os nomes das variáveis de ambiente lidas na abertura do sink.
/// </remarks>
public sealed class PostgresAuditSinkOptions
{
    /// <summary>
    /// Host do PostgreSQL. Sem valor padrão: a configuração precisa informar.
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
    /// Modo TLS da conexão. Desenvolvimento local usa <see cref="SslMode.Disable"/>.
    /// </summary>
    public SslMode SslMode { get; init; } = SslMode.Disable;

    /// <summary>
    /// Tempo limite, em segundos, da conexão e de cada comando.
    /// </summary>
    public int CommandTimeoutSeconds { get; init; } = 15;

    /// <summary>
    /// Rejeita host, porta ou nomes de variável vazios.
    /// </summary>
    /// <exception cref="ArgumentException">Host, database ou nome de variável está vazio.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Porta ou timeout está fora da faixa útil.</exception>
    public void EnsureValid()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Host);
        ArgumentException.ThrowIfNullOrWhiteSpace(Database);
        ArgumentException.ThrowIfNullOrWhiteSpace(UsernameEnvVar);
        ArgumentException.ThrowIfNullOrWhiteSpace(PasswordEnvVar);

        if (Port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(Port), Port, "A porta precisa estar entre 1 e 65535.");
        }

        if (CommandTimeoutSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(CommandTimeoutSeconds), CommandTimeoutSeconds, "O timeout precisa ser maior que zero.");
        }
    }
}
