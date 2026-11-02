using System.Threading.Channels;

using NexusRouter.Domain.Models;

namespace NexusRouter.Audit;

/// <summary>
/// Dono do <see cref="Channel{T}"/> de auditoria. O processo registra uma instância no DI.
/// </summary>
/// <remarks>
/// Vários produtores (sessões e dispatchers) e um consumidor (o writer). O channel é limitado.
/// O hot path só usa <see cref="AuditChannelWriter.TryWrite"/> e não espera o sink.
/// </remarks>
public sealed class AuditChannelSingleton
{
    /// <summary>
    /// Cria o channel com a capacidade e o overflow de <paramref name="options"/>.
    /// </summary>
    /// <param name="options">Limites do cold path.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> é nulo.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A capacidade ou o modo de overflow não serve para um channel limitado.</exception>
    public AuditChannelSingleton(AuditOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.EnsureValid();

        Instance = Channel.CreateBounded<AuditRecord>(new BoundedChannelOptions(options.ChannelCapacity)
        {
            FullMode = ToFullMode(options.OverflowMode),
            SingleWriter = false,
            SingleReader = true,
            AllowSynchronousContinuations = false,
        });
    }

    /// <summary>
    /// Channel compartilhado. Quem produz usa <see cref="AuditChannelWriter"/>; o writer lê daqui.
    /// </summary>
    public Channel<AuditRecord> Instance { get; }

    /// <summary>
    /// Traduz o modo da configuração para o channel. <see cref="BoundedChannelFullMode.Wait"/> não entra:
    /// ele prenderia a thread que chamou <c>TryWrite</c>.
    /// </summary>
    private static BoundedChannelFullMode ToFullMode(AuditOverflowMode mode) => mode switch
    {
        AuditOverflowMode.DropOldest => BoundedChannelFullMode.DropOldest,
        AuditOverflowMode.DropWrite => BoundedChannelFullMode.DropWrite,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Modo de overflow desconhecido."),
    };
}
