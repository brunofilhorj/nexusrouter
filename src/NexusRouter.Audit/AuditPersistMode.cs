namespace NexusRouter.Audit;

/// <summary>
/// Diz se o payload FIX bruto entra na hypertable.
/// </summary>
public enum AuditPersistMode
{
    /// <summary>
    /// Grava <see cref="NexusRouter.Domain.Models.AuditRecord.RawFix"/> em toda mensagem que o traz.
    /// </summary>
    Full = 0,

    /// <summary>
    /// Grava só metadados. A coluna <c>raw_fix</c> fica nula.
    /// </summary>
    MetadataOnly = 1,

    /// <summary>
    /// Grava o bruto em uma fração das mensagens, definida por <see cref="AuditOptions.SampleRate"/>.
    /// </summary>
    Sampled = 2,
}
