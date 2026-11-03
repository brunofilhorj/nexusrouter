using Microsoft.Extensions.Options;

namespace NexusRouter.Host.Configuration;

/// <summary>
/// Backoff da reconexão de uma sessão de ingresso. Os tempos estão em milissegundos.
/// </summary>
public sealed class ReconnectPolicyOptions : IValidateOptions<ReconnectPolicyOptions>
{
    /// <summary>
    /// Atraso da primeira espera, antes do teto e do jitter.
    /// </summary>
    public int BaseDelayMs { get; init; } = 500;

    /// <summary>
    /// Teto do atraso exponencial, aplicado antes do jitter.
    /// </summary>
    public int MaxDelayMs { get; init; } = 30_000;

    /// <summary>
    /// Fração do atraso já limitado usada como meia-amplitude do jitter. Zero desliga o jitter.
    /// </summary>
    public double JitterFactor { get; init; } = 0.2;

    /// <summary>
    /// Confere atrasos e jitter antes de a política ser construída.
    /// </summary>
    /// <param name="name">Nome das options no container. Pode ser nulo neste estágio.</param>
    /// <param name="options">Política já lida da configuração.</param>
    /// <returns>Sucesso ou a lista de campos que impedem a subida.</returns>
    public ValidateOptionsResult Validate(string? name, ReconnectPolicyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> failures = [];
        ConfigurationChecks.Add(failures, ConfigurationChecks.NonNegative(options.BaseDelayMs, nameof(options.BaseDelayMs)));
        ConfigurationChecks.Add(failures, ConfigurationChecks.NonNegative(options.MaxDelayMs, nameof(options.MaxDelayMs)));
        ConfigurationChecks.Add(failures, ConfigurationChecks.Jitter(options.JitterFactor, nameof(options.JitterFactor)));
        return ConfigurationChecks.From(failures);
    }
}
