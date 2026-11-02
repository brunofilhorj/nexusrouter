using System.Threading.Channels;

using NexusRouter.Domain.Models;

namespace NexusRouter.Audit;

/// <summary>
/// Porta de escrita do channel de auditoria. Não expõe o channel nem o leitor.
/// </summary>
public sealed class AuditChannelWriter
{
    private readonly ChannelWriter<AuditRecord> _writer;

    /// <summary>
    /// Liga o wrapper ao channel do singleton.
    /// </summary>
    /// <param name="channel">Channel único do processo.</param>
    /// <exception cref="ArgumentNullException"><paramref name="channel"/> é nulo.</exception>
    public AuditChannelWriter(AuditChannelSingleton channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        _writer = channel.Instance.Writer;
    }

    /// <summary>
    /// Tenta publicar um registro sem esperar o consumidor.
    /// </summary>
    /// <param name="record">Registro já preenchido pelo chamador.</param>
    /// <returns>
    /// <see langword="false"/> quando o channel já foi encerrado.
    /// Com a fila cheia, <see cref="AuditOverflowMode.DropOldest"/> e <see cref="AuditOverflowMode.DropWrite"/>
    /// também devolvem <see langword="true"/>: o primeiro descarta o mais antigo, o segundo descarta o registro novo.
    /// </returns>
    /// <remarks>Não bloqueia e não tem await. Várias threads podem chamar ao mesmo tempo.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="record"/> é nulo.</exception>
    public bool TryWrite(AuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return _writer.TryWrite(record);
    }
}
