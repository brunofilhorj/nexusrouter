using System.Diagnostics.CodeAnalysis;

namespace NexusRouter.Domain.Models;

/// <summary>
/// Chave estável de um cliente de saída. Vira nome de pasta no log de sessão, então só aceita um alfabeto seguro.
/// </summary>
/// <remarks>
/// Cliente FIX: o <c>SenderCompID</c> (tag 49) do Logon, sem cadastro. Cliente WebSocket: o <c>ClientId</c> (UUID).
/// A chave vem de entrada de rede. Aceita 1 a 64 caracteres de <c>[A-Za-z0-9_-]</c> e recusa nomes reservados do Windows.
/// </remarks>
public readonly record struct ClientKey
{
    /// <summary>
    /// Tamanho máximo da chave.
    /// </summary>
    public const int MaxLength = 64;

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private ClientKey(string value) => Value = value;

    /// <summary>
    /// Texto da chave. Nulo apenas no valor <see langword="default"/>.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Informa se <paramref name="value"/> serve como chave de cliente ou como nome de pasta do log.
    /// </summary>
    /// <param name="value">Texto candidato.</param>
    /// <returns><see langword="true"/> quando tem 1 a 64 caracteres de <c>[A-Za-z0-9_-]</c> e não é nome reservado.</returns>
    public static bool IsValid([NotNullWhen(true)] string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength || Reserved.Contains(value))
        {
            return false;
        }

        foreach (char ch in value)
        {
            if (ch is not ((>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Cria a chave de um cliente FIX a partir do <c>SenderCompID</c> do Logon.
    /// </summary>
    /// <param name="senderCompId">Tag 49 recebida.</param>
    /// <param name="key">A chave, quando o valor é válido.</param>
    /// <returns><see langword="false"/> quando o valor não passa em <see cref="IsValid"/>.</returns>
    public static bool TryCreateFix(string? senderCompId, out ClientKey key)
    {
        if (IsValid(senderCompId))
        {
            key = new ClientKey(senderCompId);
            return true;
        }

        key = default;
        return false;
    }

    /// <summary>
    /// Cria a chave de um cliente WebSocket a partir do UUID cadastrado.
    /// </summary>
    /// <param name="clientId">UUID do cliente.</param>
    /// <returns>A chave no formato <c>D</c> do <see cref="Guid"/> (36 caracteres).</returns>
    /// <exception cref="ArgumentException"><paramref name="clientId"/> é vazio.</exception>
    public static ClientKey ForWebSocket(Guid clientId)
    {
        if (clientId == Guid.Empty)
        {
            throw new ArgumentException("O ClientId WebSocket não pode ser vazio.", nameof(clientId));
        }

        return new ClientKey(clientId.ToString("D", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;
}
