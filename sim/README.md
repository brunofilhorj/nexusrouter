# DropCopy Simulator

Simulador do gateway FIX 4.4 (B3 EntryPoint) para testar o NexusRouter sem depender da B3.
Composto pelo backend `DropCopySim.Host` (acceptor FIX + API de controle Minimal API + WebSocket
de telemetria) e pelo frontend `SimPanel`.

## API de controle — autenticação por token (opcional)

A API de controle e o WebSocket de telemetria suportam um token bearer **opcional**, controlado por
variável de ambiente. O comportamento é definido pela chave de configuração
`DropCopySim:Control:AuthTokenEnvVar` (default `SIM_CONTROL_TOKEN`), que nomeia **a variável de
ambiente** onde o token reside — nunca o valor do token.

### Modo protegido

Quando a variável de ambiente nomeada em `AuthTokenEnvVar` tiver um valor não vazio, toda requisição
precisa apresentar o token:

- **HTTP:** cabeçalho `Authorization: Bearer <token>`.
- **WebSocket (navegador):** como o handshake de upgrade não permite cabeçalhos customizados, o token
  pode vir no parâmetro de query de curta duração `access_token` da URL de upgrade.

Requisições sem token válido recebem `401 Unauthorized`. A comparação do token é feita em **tempo
constante** (`CryptographicOperations.FixedTimeEquals`), evitando vazamento por timing.

```bash
# Define o token apenas no ambiente do processo (nunca em arquivo versionado).
export SIM_CONTROL_TOKEN="um-segredo-forte"
dotnet run --project sim/backend/src/DropCopySim.Host

# Chamada autenticada:
curl -H "Authorization: Bearer um-segredo-forte" http://localhost:8099/
```

### Modo aberto de laboratório

Quando a variável de ambiente estiver **ausente ou vazia**, a API opera em **modo aberto de
laboratório**: nenhuma autenticação é exigida. Esse modo destina-se **exclusivamente a
desenvolvimento local**. Não exponha a API em rede não confiável sem um token configurado.

### Regras de segurança

- O token **nunca** é armazenado em configuração versionada, em URL persistida ou em log
  (FR-API-06, NFR-CFG-02).
- A configuração guarda apenas o **nome** da variável de ambiente, não o token.
- Para desenvolvimento local, prefira definir o token via variável de ambiente do shell ou
  `dotnet user-secrets`.
