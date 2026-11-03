namespace NexusRouter.Host.Configuration;

/// <summary>
/// Implementação de <c>ISessionFrameDecoder</c> usada por uma sessão de ingresso.
/// </summary>
public enum FrameDecoderKind
{
    /// <summary>
    /// Framing FIX 4.4, delimitado por BeginString e BodyLength.
    /// </summary>
    Fix44 = 0,

    /// <summary>
    /// Framing FIX 5.0 / FIXT.
    /// </summary>
    Fix50 = 1,

    /// <summary>
    /// Framing SBE. A implementação de ingresso ainda é um stub.
    /// </summary>
    Sbe = 2,
}
