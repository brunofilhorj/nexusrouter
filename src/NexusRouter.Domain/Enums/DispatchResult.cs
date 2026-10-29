namespace NexusRouter.Domain.Enums;

/// <summary>
/// Resultado de uma tentativa de enfileirar uma mensagem em um dispatcher.
/// </summary>
public enum DispatchResult
{
    /// <summary>
    /// O payload foi aceito pelo channel do dispatcher.
    /// </summary>
    Enqueued = 0,

    /// <summary>
    /// O channel do dispatcher está cheio. A mensagem não foi enfileirada.
    /// </summary>
    ChannelFull = 1,

    /// <summary>
    /// O dispatcher não está saudável e recusou a mensagem.
    /// </summary>
    DispatcherUnhealthy = 2,
}
