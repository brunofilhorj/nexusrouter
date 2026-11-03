using Microsoft.Extensions.Options;

namespace NexusRouter.Host.Configuration;

/// <summary>
/// Arquivo rolling que recebe os eventos de auditoria.
/// </summary>
public sealed class SerilogAuditSinkOptions : AuditSinkOptions, IValidateOptions<SerilogAuditSinkOptions>
{
    /// <inheritdoc />
    public override AuditSinkType Type => AuditSinkType.Serilog;

    /// <summary>
    /// Caminho do arquivo. O Serilog acrescenta o sufixo do intervalo quando o rolling não é infinito.
    /// </summary>
    public required string FilePath { get; init; }

    /// <summary>
    /// Quando o arquivo vira.
    /// </summary>
    public AuditFileRollingInterval RollingInterval { get; init; } = AuditFileRollingInterval.Hour;

    /// <inheritdoc />
    public override ValidateOptionsResult Validate(string? name) => Validate(name, this);

    /// <summary>
    /// Confere caminho e intervalo.
    /// </summary>
    /// <param name="name">Nome das options no container. Pode ser nulo neste estágio.</param>
    /// <param name="options">Sink já lido da configuração.</param>
    /// <returns>Sucesso ou a lista de campos que impedem a subida.</returns>
    public ValidateOptionsResult Validate(string? name, SerilogAuditSinkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> failures = [];
        ConfigurationChecks.Add(failures, ConfigurationChecks.Required(options.FilePath, nameof(options.FilePath)));
        ConfigurationChecks.Add(failures, ConfigurationChecks.Defined(options.RollingInterval, nameof(options.RollingInterval)));
        return ConfigurationChecks.From(failures);
    }
}
