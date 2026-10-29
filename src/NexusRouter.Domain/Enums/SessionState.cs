namespace NexusRouter.Domain.Enums;

/// <summary>
/// Estado do ciclo de vida de uma sessão FIX.
/// </summary>
/// <remarks>
/// Os valores numéricos são estáveis: a máquina de estados transiciona com
/// <see cref="System.Threading.Interlocked.CompareExchange(ref int, int, int)"/> sobre o inteiro subjacente.
/// </remarks>
public enum SessionState
{
    /// <summary>
    /// Não há conexão ativa com o gateway.
    /// </summary>
    Disconnected = 0,

    /// <summary>
    /// O socket está sendo estabelecido.
    /// </summary>
    Connecting = 1,

    /// <summary>
    /// O Logon foi enviado e a confirmação da contraparte ainda não chegou.
    /// </summary>
    LogonSent = 2,

    /// <summary>
    /// A sessão está autenticada e pode trocar mensagens de aplicação.
    /// </summary>
    Active = 3,

    /// <summary>
    /// O Logout foi enviado e o encerramento ainda não concluiu.
    /// </summary>
    LogoutSent = 4,
}
