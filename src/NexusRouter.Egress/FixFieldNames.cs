using System.Collections.Frozen;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Xml;

namespace NexusRouter.Egress;

/// <summary>
/// Nomes de campo FIX em snake_case, lidos uma vez de <c>docs/FIX44Entrypoint.xml</c>.
/// </summary>
internal static class FixFieldNames
{
    private const string ResourceName = "NexusRouter.Egress.FIX44Entrypoint.xml";

    private static readonly FrozenDictionary<int, string> Names = Load();

    /// <summary>
    /// Nome snake_case da tag, ou <c>tag_&lt;número&gt;</c> quando o dicionário não a conhece.
    /// </summary>
    /// <param name="tag">Número da tag FIX.</param>
    /// <returns>Chave JSON do campo.</returns>
    internal static string Name(int tag) =>
        Names.TryGetValue(tag, out string? name)
            ? name
            : string.Concat("tag_", tag.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Converte um nome FIX em snake_case.
    /// </summary>
    /// <remarks>
    /// Uma sequência de maiúsculas é uma sigla e vira um único token. A última maiúscula da sigla
    /// abre a palavra seguinte quando vem uma minúscula (<c>CFICode</c> → <c>cfi_code</c>),
    /// exceto um <c>s</c> final que pluraliza a sigla (<c>NoPartyIDs</c> → <c>no_party_ids</c>).
    /// <c>ID</c> no fim de uma sigla vira <c>_id</c> (<c>ClOrdID</c> → <c>cl_ord_id</c>,
    /// <c>IOIID</c> → <c>ioi_id</c>). Dígito permanece no token anterior (<c>OrderQty2</c> → <c>order_qty2</c>).
    /// </remarks>
    /// <param name="name">Nome do campo no dicionário, em PascalCase.</param>
    /// <returns>O mesmo nome em snake_case.</returns>
    internal static string ToSnakeCase(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var builder = new StringBuilder(name.Length + 8);
        int index = 0;
        while (index < name.Length)
        {
            char current = name[index];
            if (!char.IsUpper(current))
            {
                builder.Append(current);
                index++;
                continue;
            }

            int start = index;
            while (index < name.Length && char.IsUpper(name[index]))
            {
                index++;
            }

            int runEnd = index;
            if (runEnd < name.Length && char.IsLower(name[runEnd]) && start + 1 < runEnd)
            {
                bool pluralS = runEnd + 1 == name.Length && name[runEnd] == 's';
                if (!pluralS)
                {
                    runEnd--;
                }
            }

            if (builder.Length > 0)
            {
                builder.Append('_');
            }

            AppendRun(builder, name, start, runEnd);
            index = runEnd;
        }

        return builder.ToString();
    }

    private static void AppendRun(StringBuilder builder, string name, int start, int end)
    {
        int length = end - start;
        bool splitTrailingId = length > 2 && name[end - 2] == 'I' && name[end - 1] == 'D';
        int bodyEnd = splitTrailingId ? end - 2 : end;
        for (int i = start; i < bodyEnd; i++)
        {
            builder.Append(char.ToLowerInvariant(name[i]));
        }

        if (splitTrailingId)
        {
            builder.Append("_id");
        }
    }

    private static FrozenDictionary<int, string> Load()
    {
        Assembly assembly = typeof(FixFieldNames).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Recurso embutido '{ResourceName}' não encontrado.");

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
        };

        var names = new Dictionary<int, string>();
        var used = new Dictionary<string, int>(StringComparer.Ordinal);
        using var reader = XmlReader.Create(stream, settings);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "field")
            {
                continue;
            }

            string? numberText = reader.GetAttribute("number");
            string? fieldName = reader.GetAttribute("name");
            if (numberText is null || fieldName is null)
            {
                continue;
            }

            if (!int.TryParse(numberText, NumberStyles.None, CultureInfo.InvariantCulture, out int tag))
            {
                throw new InvalidOperationException($"Tag FIX '{numberText}' não é um número.");
            }

            string key = ToSnakeCase(fieldName);
            if (!names.TryAdd(tag, key))
            {
                throw new InvalidOperationException($"Tag FIX {tag} repetida no dicionário ('{fieldName}').");
            }

            if (!used.TryAdd(key, tag))
            {
                throw new InvalidOperationException(
                    $"Nome snake_case '{key}' da tag {tag} ({fieldName}) colide com a tag {used[key]}.");
            }
        }

        if (names.Count == 0)
        {
            throw new InvalidOperationException("O dicionário FIX não tem campos com número e nome.");
        }

        return names.ToFrozenDictionary();
    }
}
