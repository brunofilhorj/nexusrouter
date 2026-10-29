using System.Buffers;

using NexusRouter.Domain.Enums;

namespace NexusRouter.Domain.Models;

/// <summary>
/// Envelope de uma mensagem já roteada, compartilhado entre o router e um dispatcher.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FixMessage"/> é um <c>ref struct</c> e não pode ser campo desta classe.
/// O snapshot parseado fica em buffers alugados e é reexposto por <see cref="TryGetParsedMessage"/>.
/// </para>
/// <para>
/// <see cref="RawBuffer"/> pertence a quem consumir o payload. <see cref="Reset"/> apenas solta a referência;
/// não chama <see cref="IDisposable.Dispose"/>, para não alterar a contagem de um buffer compartilhado
/// que o dispatcher já devolveu. Quem devolve o payload ao pool dispõe o buffer antes, se ainda for o dono.
/// </para>
/// </remarks>
public sealed class DispatchPayload
{
    private byte[]? _msgTypeBuffer;
    private int _msgTypeLength;
    private FixField[]? _parsedFields;
    private int _parsedFieldCount;
    private bool _hasParsedMessage;

    /// <summary>
    /// Sessão que originou a mensagem.
    /// </summary>
    public SessionId SessionId { get; set; }

    /// <summary>
    /// Modo de entrega usado para produzir este payload.
    /// </summary>
    public DeliveryMode DeliveryMode { get; set; }

    /// <summary>
    /// Buffer alugado com a cópia da mensagem. Nulo até o router assumir a propriedade dos bytes.
    /// </summary>
    public IMemoryOwner<byte>? RawBuffer { get; set; }

    /// <summary>
    /// Instante de ingresso, em ticks do relógio usado pela sessão.
    /// </summary>
    public long IngressTimestampTicks { get; set; }

    /// <summary>
    /// Sequência interna de roteamento, para ordenação e deduplicação.
    /// </summary>
    public long RoutingSequence { get; set; }

    /// <summary>
    /// Instante em que o router concluiu o fan-out, em ticks do mesmo relógio de ingresso.
    /// </summary>
    public long EgressTimestampTicks { get; set; }

    /// <summary>
    /// Indica se <see cref="SetParsedMessage"/> foi chamado desde o último <see cref="Reset"/>.
    /// </summary>
    public bool HasParsedMessage => _hasParsedMessage;

    /// <summary>
    /// Guarda o resultado da tokenização sem reter o <c>ref struct</c>.
    /// </summary>
    /// <param name="msgType">Bytes de MsgType. São copiados para um buffer do <see cref="ArrayPool{T}"/>.</param>
    /// <param name="fields">Campos tokenizados. Os structs são copiados; os valores continuam apontando para o buffer bruto.</param>
    /// <remarks>
    /// Aluga do <see cref="ArrayPool{T}"/>. Em regime estacionário não cria arrays novos.
    /// Os spans recebidos só precisam permanecer válidos durante a chamada.
    /// </remarks>
    public void SetParsedMessage(ReadOnlySpan<byte> msgType, ReadOnlySpan<FixField> fields)
    {
        byte[] msgTypeBuffer = ArrayPool<byte>.Shared.Rent(msgType.Length == 0 ? 1 : msgType.Length);
        msgType.CopyTo(msgTypeBuffer);

        FixField[]? fieldBuffer = null;
        if (fields.Length > 0)
        {
            fieldBuffer = ArrayPool<FixField>.Shared.Rent(fields.Length);
            fields.CopyTo(fieldBuffer);
        }

        ReleaseParsedStorage();

        _msgTypeBuffer = msgTypeBuffer;
        _msgTypeLength = msgType.Length;
        _parsedFields = fieldBuffer;
        _parsedFieldCount = fields.Length;
        _hasParsedMessage = true;
    }

    /// <summary>
    /// Materializa o <see cref="FixMessage"/> sobre o snapshot guardado.
    /// </summary>
    /// <param name="message">Mensagem parseada quando o método retorna <see langword="true"/>; valor default caso contrário.</param>
    /// <returns><see langword="true"/> quando há snapshot parseado.</returns>
    /// <remarks>
    /// Zero allocation. Seguro para chamada no hot path.
    /// O <paramref name="message"/> permanece válido até o próximo <see cref="SetParsedMessage"/> ou <see cref="Reset"/>
    /// desta instância, e os valores dos campos permanecem válidos enquanto <see cref="RawBuffer"/> não for reutilizado.
    /// </remarks>
    public bool TryGetParsedMessage(out FixMessage message)
    {
        if (!_hasParsedMessage || _msgTypeBuffer is null)
        {
            message = default;
            return false;
        }

        ReadOnlySpan<FixField> fields = _parsedFields is null
            ? ReadOnlySpan<FixField>.Empty
            : _parsedFields.AsSpan(0, _parsedFieldCount);

        message = new FixMessage(_msgTypeBuffer.AsSpan(0, _msgTypeLength), fields);
        return true;
    }

    /// <summary>
    /// Limpa todos os campos para devolução ao pool de payloads.
    /// </summary>
    /// <remarks>
    /// Solta as referências de objeto para o pool não reter mensagem nem buffer.
    /// Devolve ao <see cref="ArrayPool{T}"/> apenas os arrays internos do snapshot parseado.
    /// Não dispõe <see cref="RawBuffer"/>.
    /// </remarks>
    public void Reset()
    {
        SessionId = default;
        DeliveryMode = default;
        RawBuffer = null;
        IngressTimestampTicks = 0;
        RoutingSequence = 0;
        EgressTimestampTicks = 0;
        ReleaseParsedStorage();
    }

    private void ReleaseParsedStorage()
    {
        if (_msgTypeBuffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_msgTypeBuffer, clearArray: true);
            _msgTypeBuffer = null;
            _msgTypeLength = 0;
        }

        if (_parsedFields is not null)
        {
            ArrayPool<FixField>.Shared.Return(_parsedFields, clearArray: true);
            _parsedFields = null;
            _parsedFieldCount = 0;
        }

        _hasParsedMessage = false;
    }
}
