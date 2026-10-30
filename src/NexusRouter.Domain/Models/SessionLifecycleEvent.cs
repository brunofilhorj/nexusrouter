using NexusRouter.Domain.Enums;

namespace NexusRouter.Domain.Models;

/// <summary>
/// Transição de ciclo de vida de uma sessão, publicada fora do hot path.
/// </summary>
/// <remarks>
/// Os nomes de <see cref="Detail"/> usados pela sessão são Connected, Logon, Logout, Disconnected e Reconnecting.
/// </remarks>
public sealed record SessionLifecycleEvent
{
    /// <summary>
    /// Sessão que mudou de estado.
    /// </summary>
    public required SessionId SessionId { get; init; }

    /// <summary>
    /// Estado depois da transição.
    /// </summary>
    public required SessionState NewState { get; init; }

    /// <summary>
    /// Instante UTC da transição.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Texto curto do evento. Nulo quando a transição não tem rótulo adicional.
    /// </summary>
    public string? Detail { get; init; }
}
