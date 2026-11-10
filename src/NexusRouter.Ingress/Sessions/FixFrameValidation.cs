namespace NexusRouter.Ingress.Sessions;

/// <summary>
/// Resultado da validação de sessão de um frame de entrada.
/// </summary>
/// <remarks>
/// Os códigos de <see cref="SessionRejectReason"/> seguem a tag 373 do FIX 4.4.
/// <see cref="Text"/> é sempre uma constante: criar o resultado não aloca.
/// </remarks>
internal readonly struct FixFrameValidation
{
    internal const int RequiredTagMissing = 1;
    internal const int ValueIncorrect = 5;
    internal const int IncorrectDataFormat = 6;
    internal const int CompIdProblem = 9;
    internal const int SendingTimeAccuracyProblem = 10;

    private FixFrameValidation(int sessionRejectReason, int refTagId, long refSeqNum, string text)
    {
        SessionRejectReason = sessionRejectReason;
        RefTagId = refTagId;
        RefSeqNum = refSeqNum;
        Text = text;
    }

    /// <summary>
    /// Verdadeiro quando o frame passou em todas as regras.
    /// </summary>
    public bool IsValid => SessionRejectReason == 0;

    /// <summary>
    /// SessionRejectReason (373). Zero quando o frame é válido.
    /// </summary>
    public int SessionRejectReason { get; }

    /// <summary>
    /// RefTagID (371): tag que motivou a rejeição.
    /// </summary>
    public int RefTagId { get; }

    /// <summary>
    /// RefSeqNum (45): MsgSeqNum do frame rejeitado, ou zero quando ausente ou ilegível.
    /// </summary>
    public long RefSeqNum { get; }

    /// <summary>
    /// Text (58) da rejeição. Constante ASCII.
    /// </summary>
    public string Text { get; }

    internal static FixFrameValidation Valid { get; } = default;

    internal static FixFrameValidation Reject(int reason, int refTagId, long refSeqNum, string text) =>
        new(reason, refTagId, refSeqNum, text);
}
