using System.Net;

using Microsoft.Extensions.Options;

namespace NexusRouter.Host.Configuration;

/// <summary>
/// Predicados de configuração usados pelos POCOs. Nenhum deles lê o valor de um segredo.
/// </summary>
internal static class ConfigurationChecks
{
    /// <summary>
    /// Converte a lista de falhas no resultado do options validator.
    /// </summary>
    /// <param name="failures">Mensagens já prontas para o operador.</param>
    /// <returns>Sucesso quando a lista está vazia.</returns>
    public static ValidateOptionsResult From(List<string> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>
    /// Acrescenta uma falha quando o predicado devolveu texto.
    /// </summary>
    /// <param name="failures">Lista em construção.</param>
    /// <param name="failure">Mensagem, ou nulo quando o campo fecha.</param>
    public static void Add(List<string> failures, string? failure)
    {
        ArgumentNullException.ThrowIfNull(failures);
        if (failure is not null)
        {
            failures.Add(failure);
        }
    }

    /// <summary>
    /// Prefixa as falhas de um objeto aninhado com o caminho dele na seção.
    /// </summary>
    /// <param name="failures">Lista em construção.</param>
    /// <param name="prefix">Caminho do objeto, por exemplo <c>Sessions[0]</c>.</param>
    /// <param name="result">Resultado do validador filho.</param>
    public static void Append(List<string> failures, string prefix, ValidateOptionsResult result)
    {
        ArgumentNullException.ThrowIfNull(failures);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        if (!result.Failed || result.Failures is null)
        {
            return;
        }

        foreach (string failure in result.Failures)
        {
            failures.Add($"{prefix}: {failure}");
        }
    }

    /// <summary>
    /// Valida o bloco TLS e, nos acceptors, exige certificado quando o TLS está ligado.
    /// </summary>
    /// <param name="failures">Lista em construção.</param>
    /// <param name="tls">Bloco TLS da sessão ou do acceptor.</param>
    /// <param name="requireCertificateWhenEnabled">
    /// <see langword="true"/> no acceptor, que precisa de certificado de servidor.
    /// A sessão de ingresso pode ligar o TLS só para validar o servidor.
    /// </param>
    public static void AppendTls(List<string> failures, TlsOptions? tls, bool requireCertificateWhenEnabled)
    {
        ArgumentNullException.ThrowIfNull(failures);
        if (tls is null)
        {
            failures.Add("Tls é obrigatório.");
            return;
        }

        Append(failures, "Tls", tls.Validate(null, tls));
        if (requireCertificateWhenEnabled && tls.Enabled && string.IsNullOrWhiteSpace(tls.CertificatePath))
        {
            failures.Add("Tls: CertificatePath é obrigatório quando Enabled é true.");
        }
    }

    /// <summary>
    /// Exige texto não vazio.
    /// </summary>
    /// <param name="value">Valor lido da configuração.</param>
    /// <param name="property">Nome do campo na mensagem.</param>
    /// <returns>A mensagem, ou nulo quando o campo fecha.</returns>
    public static string? Required(string? value, string property)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return $"{property} é obrigatório.";
        }

        return null;
    }

    /// <summary>
    /// Exige um host sem espaço. Aceita nome DNS ou IP.
    /// </summary>
    /// <param name="value">Host lido da configuração.</param>
    /// <param name="property">Nome do campo na mensagem.</param>
    /// <returns>A mensagem, ou nulo quando o campo fecha.</returns>
    public static string? Host(string? value, string property)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return $"{property} é obrigatório.";
        }

        if (value.AsSpan().ContainsAny(" \t\r\n"))
        {
            return $"{property} não pode conter espaço.";
        }

        return null;
    }

    /// <summary>
    /// Exige uma porta TCP.
    /// </summary>
    /// <param name="value">Porta lida da configuração.</param>
    /// <param name="property">Nome do campo na mensagem.</param>
    /// <returns>A mensagem, ou nulo quando a porta fecha.</returns>
    public static string? Port(int value, string property)
    {
        if (value is >= 1 and <= 65535)
        {
            return null;
        }

        return $"{property} deve estar entre 1 e 65535.";
    }

    /// <summary>
    /// Exige um inteiro maior que zero.
    /// </summary>
    /// <param name="value">Valor lido da configuração.</param>
    /// <param name="property">Nome do campo na mensagem.</param>
    /// <returns>A mensagem, ou nulo quando o valor fecha.</returns>
    public static string? Positive(int value, string property)
    {
        if (value > 0)
        {
            return null;
        }

        return $"{property} precisa ser maior que zero.";
    }

    /// <summary>
    /// Exige um inteiro dentro da faixa, inclusive nos extremos.
    /// </summary>
    /// <param name="value">Valor lido da configuração.</param>
    /// <param name="min">Mínimo aceito.</param>
    /// <param name="max">Máximo aceito.</param>
    /// <param name="property">Nome do campo na mensagem.</param>
    /// <returns>A mensagem, ou nulo quando o valor fecha.</returns>
    public static string? Between(int value, int min, int max, string property)
    {
        if (value >= min && value <= max)
        {
            return null;
        }

        return $"{property} deve estar entre {min} e {max}.";
    }

    /// <summary>
    /// Exige um inteiro zero ou positivo.
    /// </summary>
    /// <param name="value">Valor lido da configuração.</param>
    /// <param name="property">Nome do campo na mensagem.</param>
    /// <returns>A mensagem, ou nulo quando o valor fecha.</returns>
    public static string? NonNegative(int value, string property)
    {
        if (value >= 0)
        {
            return null;
        }

        return $"{property} não pode ser negativo.";
    }

    /// <summary>
    /// Exige um fator de jitter finito e zero ou positivo.
    /// </summary>
    /// <param name="value">Fator lido da configuração.</param>
    /// <param name="property">Nome do campo na mensagem.</param>
    /// <returns>A mensagem, ou nulo quando o fator fecha.</returns>
    public static string? Jitter(double value, string property)
    {
        if (double.IsFinite(value) && value >= 0d)
        {
            return null;
        }

        return $"{property} deve ser finito e não negativo.";
    }

    /// <summary>
    /// Rejeita um membro de enum que não foi declarado.
    /// </summary>
    /// <typeparam name="TEnum">Tipo do enum.</typeparam>
    /// <param name="value">Valor já convertido.</param>
    /// <param name="property">Nome do campo na mensagem.</param>
    /// <returns>A mensagem, ou nulo quando o membro existe.</returns>
    public static string? Defined<TEnum>(TEnum value, string property)
        where TEnum : struct, Enum
    {
        if (Enum.IsDefined(value))
        {
            return null;
        }

        return $"{property} tem um valor desconhecido.";
    }

    /// <summary>
    /// Exige um identificador de sessão, dispatcher ou rota: letras, dígitos, ponto, hífen e sublinhado.
    /// </summary>
    /// <param name="value">Identificador lido da configuração.</param>
    /// <param name="property">Nome do campo na mensagem.</param>
    /// <returns>A mensagem, ou nulo quando o identificador fecha.</returns>
    public static string? RoutingId(string? value, string property)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return $"{property} é obrigatório.";
        }

        foreach (char ch in value)
        {
            bool ok = ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_' or '.';
            if (!ok)
            {
                return $"{property} aceita letras, dígitos, ponto, hífen e sublinhado.";
            }
        }

        return null;
    }

    /// <summary>
    /// Exige um token FIX em ASCII, sem SOH e sem <c>=</c>.
    /// </summary>
    /// <param name="value">Token lido da configuração.</param>
    /// <param name="property">Nome do campo na mensagem.</param>
    /// <param name="required"><see langword="false"/> quando o campo pode ficar de fora.</param>
    /// <returns>A mensagem, ou nulo quando o token fecha.</returns>
    public static string? FixToken(string? value, string property, bool required)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return required ? $"{property} é obrigatório." : null;
        }

        foreach (char ch in value)
        {
            if (ch > 127 || ch == '=' || ch == '\u0001')
            {
                return $"{property} deve ser ASCII e não pode conter SOH nem '='.";
            }
        }

        return null;
    }

    /// <summary>
    /// Exige o nome de uma variável de ambiente, nunca o segredo.
    /// </summary>
    /// <param name="value">Nome lido da configuração.</param>
    /// <param name="property">Nome do campo na mensagem.</param>
    /// <param name="required"><see langword="false"/> quando o mecanismo não usa senha.</param>
    /// <returns>A mensagem, ou nulo quando o nome fecha.</returns>
    public static string? EnvVar(string? value, string property, bool required)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return required
                ? $"{property} é obrigatório e deve ser o nome de uma variável de ambiente."
                : null;
        }

        char first = value[0];
        if (first != '_' && !char.IsAsciiLetter(first))
        {
            return $"{property} deve ser o nome de uma variável de ambiente, sem o valor do segredo.";
        }

        foreach (char ch in value)
        {
            if (ch != '_' && !char.IsAsciiLetterOrDigit(ch))
            {
                return $"{property} deve ser o nome de uma variável de ambiente, sem o valor do segredo.";
            }
        }

        return null;
    }

    /// <summary>
    /// Exige um endereço IP literal, como o acceptor faz no bind do socket.
    /// </summary>
    /// <param name="value">Endereço lido da configuração.</param>
    /// <param name="property">Nome do campo na mensagem.</param>
    /// <returns>A mensagem, ou nulo quando o endereço fecha.</returns>
    public static string? Ip(string? value, string property)
    {
        if (string.IsNullOrWhiteSpace(value) || !IPAddress.TryParse(value, out _))
        {
            return $"{property} deve ser um endereço IP.";
        }

        return null;
    }

    /// <summary>
    /// Exige uma origem absoluta, no formato do header <c>Origin</c>.
    /// </summary>
    /// <param name="value">Origem lida da configuração.</param>
    /// <param name="property">Nome do campo na mensagem.</param>
    /// <returns>A mensagem, ou nulo quando a origem fecha.</returns>
    public static string? Origin(string? value, string property)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || string.IsNullOrEmpty(uri.Host))
        {
            return $"{property} deve ser uma origem absoluta, por exemplo https://app.example.com.";
        }

        return null;
    }

    /// <summary>
    /// Exige um UUID.
    /// </summary>
    /// <param name="value">Texto lido da configuração.</param>
    /// <param name="property">Nome do campo na mensagem.</param>
    /// <returns>A mensagem, ou nulo quando o texto é um UUID.</returns>
    public static string? GuidValue(string? value, string property)
    {
        if (string.IsNullOrWhiteSpace(value) || !Guid.TryParse(value, out _))
        {
            return $"{property} deve ser um UUID.";
        }

        return null;
    }
}
