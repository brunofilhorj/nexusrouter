namespace DropCopySim.Core.Acceptor;

/// <summary>Estados do ciclo de vida da sessão FIX no lado acceptor.</summary>
public enum AcceptorSessionState
{
    /// <summary>Sessão encerrada ou ainda sem conexão TCP.</summary>
    Disconnected = 0,

    /// <summary>Conexão TCP aceita; aguardando Logon do cliente.</summary>
    Connected = 1,

    /// <summary>Logon (MsgType=A) recebido; aguardando envio do Logon ACK.</summary>
    LogonReceived = 2,

    /// <summary>Sessão autenticada; mensagens administrativas e de aplicação permitidas.</summary>
    Active = 3,

    /// <summary>Logout enviado ou em curso; aguardando encerramento.</summary>
    LogoutSent = 4,
}
