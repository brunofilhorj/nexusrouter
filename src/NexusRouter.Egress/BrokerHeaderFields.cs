using System.Buffers;
using System.Globalization;

using NexusRouter.Domain.Models;

namespace NexusRouter.Egress;

/// <summary>
/// Tags FIX carimbadas nos brokers e o template de routing key do RabbitMQ.
/// Sem tags, o publish segue só com o header de idempotência.
/// </summary>
public static class BrokerHeaderFields
{
    internal const string MissingValueMarker = "_missing";

    /// <summary>
    /// Confere a lista de tags. Vazia é válida.
    /// </summary>
    /// <param name="headerFields">Tags configuradas. Não pode ser nula.</param>
    /// <exception cref="ArgumentNullException">A lista é nula.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Alguma tag não é positiva.</exception>
    /// <exception cref="ArgumentException">A lista repete uma tag.</exception>
    public static void EnsureTags(IReadOnlyList<int>? headerFields)
    {
        if (headerFields is null)
        {
            throw new ArgumentNullException(nameof(headerFields));
        }

        string? error = TagsError(headerFields);
        if (error is null)
        {
            return;
        }

        foreach (int tag in headerFields)
        {
            if (tag <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(headerFields), tag, error);
            }
        }

        throw new ArgumentException(error, nameof(headerFields));
    }

    /// <summary>
    /// Confere o template. Vazio ou nulo mantém a routing key fixa.
    /// </summary>
    /// <param name="template">Template, por exemplo <c>fix.&lt;1&gt;.&lt;55&gt;</c>.</param>
    /// <param name="headerFields">Tags já conferidas por <see cref="EnsureTags"/>.</param>
    /// <exception cref="ArgumentException">O template não fecha ou cita tag de fora da lista.</exception>
    public static void EnsureRoutingKeyTemplate(string? template, IReadOnlyList<int>? headerFields)
    {
        string? error = RoutingKeyTemplateError(template, headerFields);
        if (error is not null)
        {
            throw new ArgumentException(error, nameof(template));
        }
    }

    /// <summary>
    /// Mensagem de validação da lista de tags, ou nulo quando ela fecha.
    /// </summary>
    /// <param name="headerFields">Tags lidas da configuração.</param>
    /// <returns>A mensagem, ou nulo quando a lista fecha.</returns>
    public static string? TagsError(IReadOnlyList<int>? headerFields)
    {
        if (headerFields is null)
        {
            return "HeaderFields é obrigatório.";
        }

        var seen = new HashSet<int>(headerFields.Count);
        foreach (int tag in headerFields)
        {
            if (tag <= 0)
            {
                return "HeaderFields só aceita tag FIX positiva.";
            }

            if (!seen.Add(tag))
            {
                return $"HeaderFields repete a tag {tag}.";
            }
        }

        return null;
    }

    /// <summary>
    /// Mensagem de validação do template, ou nulo quando ele está ausente ou fecha.
    /// </summary>
    /// <param name="template">Template lido da configuração.</param>
    /// <param name="headerFields">Tags que podem aparecer como placeholder.</param>
    /// <returns>A mensagem, ou nulo quando o template fecha.</returns>
    public static string? RoutingKeyTemplateError(string? template, IReadOnlyList<int>? headerFields)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return null;
        }

        if (headerFields is null)
        {
            return "HeaderFields é obrigatório.";
        }

        bool any = false;
        for (int i = 0; i < template.Length; i++)
        {
            char ch = template[i];
            if (ch == '>')
            {
                return "RoutingKeyTemplate tem '>' sem '<'.";
            }

            if (ch != '<')
            {
                continue;
            }

            int end = template.IndexOf('>', i + 1);
            if (end < 0)
            {
                return "RoutingKeyTemplate tem '<' sem '>'.";
            }

            if (!int.TryParse(
                    template.AsSpan(i + 1, end - i - 1),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int tag)
                || tag <= 0)
            {
                return "RoutingKeyTemplate só aceita placeholder <tag> com tag FIX positiva.";
            }

            if (!Contains(headerFields, tag))
            {
                return $"RoutingKeyTemplate usa a tag {tag}, ausente em HeaderFields.";
            }

            any = true;
            i = end;
        }

        return any ? null : "RoutingKeyTemplate precisa de ao menos um placeholder <tag>.";
    }

    /// <summary>
    /// Lê somente as tags configuradas, reutilizando o snapshot parseado quando disponível.
    /// </summary>
    /// <param name="payload">Payload cujo buffer permanece válido durante o envio.</param>
    /// <param name="headerFields">Tags que serão publicadas nos headers.</param>
    /// <returns>Valores alinhados à lista de tags; nulo indica que a tag não existe.</returns>
    /// <remarks>Chamado no consume loop, nunca na thread do router.</remarks>
    internal static ReadOnlyMemory<byte>?[] ReadValues(DispatchPayload payload, IReadOnlyList<int> headerFields)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(headerFields);

        if (headerFields.Count == 0)
        {
            return [];
        }

        IMemoryOwner<byte>? rawBuffer = payload.RawBuffer;
        if (rawBuffer is null)
        {
            throw new InvalidOperationException("O payload não tem buffer para leitura das tags FIX.");
        }

        var values = new ReadOnlyMemory<byte>?[headerFields.Count];
        if (payload.TryGetParsedMessage(out FixMessage message))
        {
            ReadOnlySpan<FixField> fields = message.Fields;
            int found = 0;
            for (int fieldIndex = 0; fieldIndex < fields.Length; fieldIndex++)
            {
                FixField field = fields[fieldIndex];
                for (int tagIndex = 0; tagIndex < headerFields.Count; tagIndex++)
                {
                    if (field.Tag == headerFields[tagIndex] && values[tagIndex] is null)
                    {
                        values[tagIndex] = field.Value;
                        found++;
                    }
                }

                if (found == headerFields.Count)
                {
                    break;
                }
            }

            return values;
        }

        Scan(rawBuffer.Memory, headerFields, values);
        return values;
    }

    /// <summary>
    /// Nome estável do header que transporta o valor de uma tag FIX.
    /// </summary>
    /// <param name="tag">Tag configurada.</param>
    /// <returns>Nome no formato <c>FIX-{tag}</c>.</returns>
    internal static string HeaderName(int tag) =>
        string.Concat("FIX-", tag.ToString(CultureInfo.InvariantCulture));

    private static void Scan(
        ReadOnlyMemory<byte> frame,
        IReadOnlyList<int> headerFields,
        ReadOnlyMemory<byte>?[] values)
    {
        const byte Soh = 0x01;
        ReadOnlySpan<byte> bytes = frame.Span;
        int index = 0;
        int found = 0;

        while (index < bytes.Length && found < headerFields.Count)
        {
            if (bytes[index] == Soh)
            {
                index++;
                continue;
            }

            if (!TryReadTag(bytes, ref index, out int tag))
            {
                break;
            }

            int valueStart = index;
            int separator = bytes[index..].IndexOf(Soh);
            int valueLength = separator < 0 ? bytes.Length - valueStart : separator;
            index = separator < 0 ? bytes.Length : valueStart + valueLength + 1;

            for (int tagIndex = 0; tagIndex < headerFields.Count; tagIndex++)
            {
                if (tag == headerFields[tagIndex] && values[tagIndex] is null)
                {
                    values[tagIndex] = frame.Slice(valueStart, valueLength);
                    found++;
                    break;
                }
            }
        }
    }

    private static bool TryReadTag(ReadOnlySpan<byte> bytes, ref int index, out int tag)
    {
        int start = index;
        tag = 0;
        while (index < bytes.Length)
        {
            byte digit = bytes[index];
            if (digit is < (byte)'0' or > (byte)'9')
            {
                break;
            }

            int numeric = digit - (byte)'0';
            if (tag > (int.MaxValue - numeric) / 10)
            {
                tag = 0;
                return false;
            }

            tag = (tag * 10) + numeric;
            index++;
        }

        if (index == start || index >= bytes.Length || bytes[index] != (byte)'=')
        {
            tag = 0;
            return false;
        }

        index++;
        return true;
    }

    private static bool Contains(IReadOnlyList<int> headerFields, int tag)
    {
        foreach (int candidate in headerFields)
        {
            if (candidate == tag)
            {
                return true;
            }
        }

        return false;
    }
}
