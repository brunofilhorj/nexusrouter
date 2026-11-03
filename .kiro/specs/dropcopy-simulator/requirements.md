# DropCopy Simulator — Requisitos Funcionais e Não Funcionais

> **Status do Documento:** Rascunho v1.0
> **Classificação:** Especificação de Engenharia Interna
> **Domínio:** Ferramenta de Teste — Simulador do DropCopy B3 EntryPoint (FIX 4.4)
> **Referência de Protocolo:** `.kiro/steering/b3-guidelines.md` + `docs/FIX44Entrypoint.xml`

---

## 1. Visão Geral do Sistema

O **DropCopy Simulator** é uma ferramenta de teste que imita o lado DropCopy do B3 EntryPoint. Ele existe para exercitar o NexusRouter de ponta a ponta sem depender do ambiente de certificação da B3.

O simulador tem dois componentes:

1. **SimCore (backend .NET 10):** um **acceptor FIX 4.4** que escuta conexões, completa o handshake de Logon como se fosse o gateway da B3, e envia um fluxo controlado de mensagens de aplicação (primariamente `ExecutionReport`, MsgType=8) para o cliente conectado — o NexusRouter. Expõe também uma API de controle HTTP + WebSocket para o front.
2. **SimPanel (frontend React + Vite):** um painel de controle web que comanda o SimCore: derrubar/restaurar a conexão, iniciar/pausar/parar a geração de ordens, e configurar os parâmetros de simulação (quantidade de ordens, frequência, contas, instrumentos, etc.).

**Relação com o NexusRouter:** o NexusRouter é o **cliente FIX inbound** (ver `nexusrouter/requirements.md`, FR-ING-01). O simulador é o **servidor (acceptor)** que ele disca. O fluxo de mensagens é B3 → NexusRouter: o simulador produz as execuções fake que o NexusRouter ingere, roteia e distribui.

```
┌──────────────┐        ┌──────────────────────────────┐        ┌──────────────┐
│  SimPanel    │  HTTP  │          SimCore (.NET)        │  FIX   │ NexusRouter  │
│ (React/Vite) │ ◄────► │                                │ 4.4    │ (FixSession  │
│              │  WS    │  ┌──────────┐   ┌───────────┐  │ TCP    │  inbound)    │
│  controles   │ ◄────► │  │ Control  │   │ FIX        │ │ ◄───── │              │
│  + telemetria│        │  │ API      │   │ Acceptor   │ │        │ disca p/     │
└──────────────┘        │  └──────────┘   │ + Gerador  │ │        │ o acceptor   │
                        │                 └───────────┘  │        └──────────────┘
                        └──────────────────────────────┘
```

---

## 2. Requisitos Funcionais

### 2.1 Acceptor FIX 4.4 — Identidade B3 (FR-ACC)

| ID | Requisito |
|----|-----------|
| FR-ACC-01 | O SimCore DEVE operar como um **acceptor FIX 4.4**: abre um `TcpListener` em endereço e porta configuráveis e aceita conexões inbound. NÃO DEVE discar para o NexusRouter — é o NexusRouter que conecta. |
| FR-ACC-02 | O acceptor DEVE suportar no mínimo uma sessão conectada por vez (o NexusRouter). Suporte a múltiplas sessões concorrentes é desejável, mas não é requisito da v1. |
| FR-ACC-03 | A identidade da sessão DEVE ser configurável: `BeginString` (fixo `FIX.4.4`), `SenderCompID` (lado simulador, representa o gateway B3, ex.: `ENTRYPOINT-FIRM-0001`), `TargetCompID` esperado do cliente (ex.: `CLIENT1`), `HeartBtInt` (default `20`, conforme B3). |
| FR-ACC-04 | No Logon inbound (MsgType=A), o acceptor DEVE validar: `BeginString = FIX.4.4`, `TargetCompID` (tag 56) corresponde ao `SenderCompID` do simulador, e `MsgType = A`. Em caso de divergência, DEVE responder com `Reject` (MsgType=3) ou `Logout` (MsgType=5) com `Text` explicativo e encerrar. |
| FR-ACC-05 | O acceptor DEVE responder o Logon aceito com um Logon ACK (MsgType=A) espelhando `HeartBtInt` e preenchendo `ApplicationLogonRspCode (6930) = 0` (sucesso). Um modo de falha de Logon configurável (`ApplicationLogonRspCode` ≠ 0) DEVE ser suportado para testar o tratamento de erro do NexusRouter (ver FR-FAULT). |
| FR-ACC-06 | O acceptor DEVE honrar o protocolo de nível de sessão FIX 4.4: responder `TestRequest` (MsgType=1) com `Heartbeat` (MsgType=0) contendo o `TestReqID`, emitir `Heartbeat` a cada `HeartBtInt` quando ocioso, e processar `ResendRequest` (MsgType=2) e `SequenceReset` (MsgType=4) de forma básica (gap-fill). |
| FR-ACC-07 | O acceptor DEVE gerenciar o `MsgSeqNum` (tag 34) de nível de sessão de forma independente e monotônica por sessão. `ResetSeqNumFlag=Y` no Logon do cliente DEVE resetar o sequence number da sessão para 1. |
| FR-ACC-08 | Em `Logout` (MsgType=5) recebido, o acceptor DEVE responder com `Logout` e encerrar a sessão graciosamente. Em desconexão TCP sem Logout, o acceptor DEVE registrar o evento e liberar a sessão. |
| FR-ACC-09 | O enquadramento de mensagens DEVE seguir o framing FIX padrão (BeginString + BodyLength + ... + CheckSum), com cálculo de `CheckSum` (tag 10) conforme a regra padrão B3 (`.kiro/steering/b3-guidelines.md` §15). |

### 2.2 Geração de Mensagens de Aplicação (FR-GEN)

| ID | Requisito |
|----|-----------|
| FR-GEN-01 | O SimCore DEVE gerar `ExecutionReport` (MsgType=8) sintéticos e enviá-los para a sessão logada, representando o fluxo DropCopy da B3. |
| FR-GEN-02 | Cada `ExecutionReport` gerado DEVE conter, no mínimo, os campos obrigatórios da definição do `FIX44Entrypoint.xml`: `OrderID (37)`, `ExecID (17)`, `ExecType (150)`, `OrdStatus (39)`, `Side (54)`, componente `Instrument`, `LeavesQty (151)`, `CumQty (14)`, `AvgPx (6)`, componente `Parties`. Campos recomendados para auditoria (`.kiro/steering/b3-guidelines.md` §7): `ClOrdID (11)`, `LastQty (32)`, `LastPx (31)`. |
| FR-GEN-03 | O gerador DEVE produzir campos B3-específicos de header e aplicação para realismo: `BusinessSeqNum (10016)` monotônico atribuído pelo simulador, `BTSFinalTxOrdStatus (10029)`, e, quando aplicável, `MMProtectionReset (9773)`. Esses campos DEVEM ser preservados conforme a `.kiro/steering/b3-guidelines.md`. |
| FR-GEN-04 | O gerador DEVE modelar um ciclo de vida de ordem plausível: `ExecType=0` (New) → `ExecType=1` (PartialFill) × N → `ExecType=2` (Fill), ou `ExecType=4` (Canceled)/`ExecType=8` (Rejected). O proporcional de cada desfecho DEVE ser configurável. `OrdStatus (39)` DEVE ser coerente com o `ExecType` e com a quantidade restante (`LeavesQty`), conforme a regra de independência dos dois campos (`.kiro/steering/b3-guidelines.md` §6.6). |
| FR-GEN-05 | `ClOrdID (11)` DEVE ser único por ordem e respeitar a restrição B3: máximo 20 caracteres, apenas alfanumérico/`-`/`_`. Estratégia de geração recomendada: `{YYYYMMDD}-{seq:D8}` ou equivalente dentro do limite. `TransactTime (60)` DEVE estar em UTC no formato `YYYYMMDD-HH:MM:SS.sss`. |
| FR-GEN-06 | A identificação de instrumento DEVE usar `SecurityID (48)` + `SecurityIDSource (22) = 8` quando um `SecurityID` numérico for configurado, com `Symbol (55)` e `SecurityExchange (207) = BVMF`. A lista de instrumentos DEVE ser configurável (ver FR-CFG). |
| FR-GEN-07 | O componente `Parties` (NoPartyIDs, tag 453) DEVE ser populado com, no mínimo, `PartyRole=4` (Clearing Firm) — obrigatório para renda variável da B3 — e `PartyRole=3` (Client ID) a partir da lista de contas configurada. |
| FR-GEN-08 | O gerador DEVE suportar envio de mensagens de aplicação além de `ExecutionReport`, de forma opcional e configurável, para testar roteamento: `OrderCancelReject (9)` e `BusinessMessageReject (j)`. v1 DEVE implementar ao menos `ExecutionReport`; os demais são extensões dentro da mesma spec. |
| FR-GEN-09 | O gerador DEVE marcar as mensagens com `TestMessageIndicator (464) = Y` quando o simulador estiver em modo de teste (default), para espelhar o comportamento de certificação da B3. Esse flag DEVE ser configurável. |

### 2.3 Controle de Simulação (FR-SIM)

| ID | Requisito |
|----|-----------|
| FR-SIM-01 | O SimCore DEVE expor um controlador de simulação com os comandos: **Start** (iniciar geração), **Pause** (suspender sem perder o estado/contadores), **Resume** (retomar de onde pausou) e **Stop** (encerrar a geração e zerar o estado da rodada). |
| FR-SIM-02 | O estado da simulação DEVE ser uma máquina de estados explícita: `Idle → Running → Paused → Running → ... → Idle`. Transições inválidas (ex.: Pause quando `Idle`) DEVEM ser rejeitadas com erro claro na API. |
| FR-SIM-03 | A **frequência de envio** DEVE ser configurável em mensagens por segundo (taxa alvo). O simulador DEVE respeitar a taxa de forma aproximada usando um temporizador, sem exigir precisão de hard real-time. |
| FR-SIM-04 | A **quantidade total de ordens** por rodada DEVE ser configurável. Um valor `0` ou "ilimitado" DEVE significar geração contínua até Pause/Stop. Quando um limite finito for atingido, a simulação DEVE transicionar automaticamente para `Idle` e notificar o front. |
| FR-SIM-05 | O **conjunto de contas** usado na geração DEVE ser configurável (lista de identificadores de conta/cliente). Cada ordem gerada DEVE escolher uma conta da lista (round-robin ou aleatória, configurável) e refleti-la no `Account (1)` e/ou no `Parties` (`PartyRole=3`). |
| FR-SIM-06 | A simulação DEVE suportar reconfiguração com uma política **híbrida** por campo. Campos **ajustáveis ao vivo (hot)** — `RatePerSecond` e `Outcome` (proporção de desfechos) — DEVEM poder ser alterados durante `Running` e entrar em vigor no próximo ciclo do gerador, sem parar o fluxo. Campos **travados** — `TotalOrders`, `Accounts`, `Instruments`, `AccountSelection`, `PartialFillsMax`, `TestMessageIndicator`, `EnabledMessageTypes` — só DEVEM ser reconfiguráveis em `Idle` ou `Paused`. Tentar alterar um campo travado durante `Running` DEVE ser rejeitado com erro claro (HTTP 409), sem aplicar nenhuma parte da atualização. |
| FR-SIM-07 | O SimCore DEVE manter contadores de telemetria da rodada: total enviado, taxa efetiva (msg/s), contagem por `ExecType`, contagem de erros de envio, e tempo decorrido. Esses contadores DEVEM ser consultáveis via API e transmitidos ao front em tempo quase real. |
| FR-SIM-08 | A API de atualização de configuração DEVE validar o escopo da mudança contra o estado atual antes de aplicar: em `Running`, aceitar um corpo que altere apenas campos hot; recusar (sem efeito parcial) qualquer corpo que toque em campo travado. Em `Idle`/`Paused`, aceitar qualquer campo. |

### 2.4 Injeção de Falhas e Comportamento da Conexão (FR-FAULT)

| ID | Requisito |
|----|-----------|
| FR-FAULT-01 | O SimCore DEVE expor um comando **Drop Connection** que derruba a sessão FIX ativa (fecha o socket TCP, sem Logout) para testar a reconexão com exponential backoff do NexusRouter (`nexusrouter/requirements.md` FR-ING-04). |
| FR-FAULT-02 | O SimCore DEVE expor um comando de **Logout forçado** que envia `Logout` (MsgType=5) com `Text` configurável, incluindo os cenários da `.kiro/steering/b3-guidelines.md` §13: `Text` contendo "MsgSeqNum too low" e `Text` contendo "Password will expire". |
| FR-FAULT-03 | O SimCore DEVE permitir configurar a resposta do Logon para falha: `ApplicationLogonRspCode (6930)` = `1` (credenciais inválidas) ou `2` (não autorizado), para verificar que o NexusRouter não tenta reconectar sem intervenção. |
| FR-FAULT-04 | O SimCore DEVE suportar injeção opcional de cenários de sequência: forçar um gap de `MsgSeqNum` (pular números) para disparar um `ResendRequest` do cliente, e responder com `SequenceReset` (GapFill). Esse recurso é desejável na v1 e obrigatório para cobertura de recuperação de sequência. |
| FR-FAULT-05 | O SimCore DEVE suportar injeção opcional de mensagens malformadas (ex.: checksum inválido, campo obrigatório ausente) de forma explicitamente comandada pelo front, para exercitar o caminho de `Reject` (MsgType=3) do protocolo. Esse recurso é desejável, não obrigatório para a v1. |
| FR-FAULT-06 | Após um **Drop Connection**, o acceptor DEVE voltar a escutar e aceitar a reconexão do NexusRouter, preservando ou resetando o sequence number conforme a configuração (`ResetSeqNumFlag` do novo Logon). |

### 2.5 API de Controle e Telemetria (FR-API)

| ID | Requisito |
|----|-----------|
| FR-API-01 | O SimCore DEVE expor uma API HTTP de controle para o front, com endpoints para: ler o estado atual (sessão + simulação + config), iniciar/pausar/retomar/parar a simulação, derrubar conexão, forçar logout, e atualizar a configuração de simulação. |
| FR-API-02 | O SimCore DEVE expor um canal **WebSocket** (ou Server-Sent Events) para enviar ao front, em tempo quase real: transições de estado da sessão FIX, transições de estado da simulação, contadores de telemetria e um feed amostrado das últimas mensagens enviadas (para visualização). |
| FR-API-03 | O feed de mensagens enviado ao front DEVE ser **amostrado/limitado** (ex.: últimas N mensagens ou taxa máxima de eventos) para não saturar o browser sob alta frequência de geração. O envio FIX ao NexusRouter NÃO DEVE ser atrasado ou bloqueado pela telemetria do front. |
| FR-API-04 | A API DEVE habilitar **CORS** para a origem do SimPanel em desenvolvimento (ex.: `http://localhost:5173` do Vite) de forma configurável. |
| FR-API-05 | Todo payload da API DEVE ser JSON. Erros DEVEM retornar um corpo estruturado (`{ code, message }`) e código HTTP apropriado (400 para transição inválida/validação, 404, 409 para conflito de estado). |
| FR-API-06 | A API de controle DEVE ser protegida por um token simples quando `AUTH_TOKEN` estiver configurado via variável de ambiente. O token NÃO DEVE aparecer em config versionada, URL ou log. Em desenvolvimento local sem token configurado, a API pode operar aberta (documentado como uso somente de laboratório). |

### 2.6 Frontend — SimPanel (FR-UI)

| ID | Requisito |
|----|-----------|
| FR-UI-01 | O SimPanel DEVE ser uma SPA em **React + Vite + TypeScript**, servida em desenvolvimento pelo dev server do Vite e consumindo a API/WebSocket do SimCore. |
| FR-UI-02 | A UI DEVE exibir o **status da sessão FIX** em tempo real: estado (Disconnected/Logon/Active/Logout), `SenderCompID`/`TargetCompID` negociados, `MsgSeqNum` atual e timestamp do último heartbeat. |
| FR-UI-03 | A UI DEVE exibir o **status da simulação**: estado (Idle/Running/Paused), total enviado, taxa efetiva (msg/s), contagem por `ExecType`, erros e tempo decorrido. |
| FR-UI-04 | A UI DEVE prover controles para **iniciar, pausar, retomar e parar** a simulação, habilitados/desabilitados conforme o estado atual (ex.: Pause desabilitado quando `Idle`). |
| FR-UI-05 | A UI DEVE prover um formulário de **configuração da simulação**: quantidade de ordens (ou ilimitado), frequência (msg/s), lista de contas, lista de instrumentos, estratégia de seleção de conta (round-robin/aleatória), proporção de desfechos (`Fill`/`PartialFill`/`Canceled`/`Rejected`) e flag `TestMessageIndicator`. |
| FR-UI-06 | A UI DEVE prover controles de **injeção de falha**: botão Drop Connection, botão Force Logout (com escolha do texto/cenário), e configuração do modo de resposta ao Logon (sucesso ou código de falha). |
| FR-UI-07 | A UI DEVE exibir um **feed ao vivo** das últimas mensagens enviadas (amostrado), mostrando `MsgType`, `ClOrdID`, `OrderID`, `ExecType`, `OrdStatus`, `Side`, instrumento e `BusinessSeqNum`. |
| FR-UI-08 | A UI DEVE indicar claramente o estado de conectividade com o SimCore (API/WebSocket up ou down) e tentar reconectar o WebSocket automaticamente. |

---

## 3. Requisitos Não Funcionais

### 3.1 Fidelidade ao Protocolo (NFR-FID)

| ID | Requisito |
|----|-----------|
| NFR-FID-01 | As mensagens produzidas DEVEM ser aceitas e parseadas pelo NexusRouter sem `Reject`, exceto quando a injeção de falha for comandada. A fidelidade é verificada ingerindo o fluxo no NexusRouter e confirmando a ausência de rejeições no log de auditoria. |
| NFR-FID-02 | O cálculo de `BodyLength (9)` e `CheckSum (10)` DEVE seguir exatamente o padrão FIX 4.4 / B3 (`.kiro/steering/b3-guidelines.md` §15). |
| NFR-FID-03 | Os campos e enumerações DEVEM respeitar o `docs/FIX44Entrypoint.xml` como referência autoritativa. Qualquer divergência DEVE ser documentada. |

### 3.2 Performance (NFR-PERF)

| ID | Requisito |
|----|-----------|
| NFR-PERF-01 | O SimCore DEVE sustentar uma taxa de geração configurável de ao menos **50.000 msg/s** para uma sessão, suficiente para exercitar o hot path do NexusRouter sob carga. Taxas menores (ex.: 1–100 msg/s) DEVEM ser suportadas para inspeção manual. |
| NFR-PERF-02 | O caminho de envio FIX (gerador → socket) NÃO DEVE ser bloqueado pela telemetria do front nem pela serialização JSON do feed. O feed ao front DEVE usar amostragem e um channel não bloqueante, descartando eventos de telemetria sob pressão em vez de atrasar o envio FIX. |
| NFR-PERF-03 | O simulador é uma ferramenta de teste e NÃO está sujeito às restrições de latência sub-50µs do NexusRouter. Alocações no caminho de geração são aceitáveis, mas o design DEVE evitar desperdício óbvio (ex.: realocar buffers por mensagem) para atingir a taxa alvo. |

### 3.3 Configurabilidade (NFR-CFG)

| ID | Requisito |
|----|-----------|
| NFR-CFG-01 | Toda configuração (endereço/porta de escuta, comp IDs, `HeartBtInt`, defaults de simulação, instrumentos, contas, CORS, token) DEVE vir de `appsettings.json` + variáveis de ambiente, sem recompilação. Nenhum endpoint, conta ou credencial DEVE ser hardcoded no código. |
| NFR-CFG-02 | Secrets (token da API de controle) NÃO DEVEM estar em arquivo versionado; DEVEM vir de variável de ambiente nomeada na configuração. |
| NFR-CFG-03 | A configuração padrão DEVE estar alinhada ao ambiente local do NexusRouter: o acceptor escuta em uma porta conhecida (ex.: `localhost:9876`) para a qual uma sessão do NexusRouter pode ser apontada sem ajustes adicionais. |

### 3.4 Observabilidade (NFR-OBS)

| ID | Requisito |
|----|-----------|
| NFR-OBS-01 | O SimCore DEVE emitir logs estruturados via `Microsoft.Extensions.Logging` com `Serilog`, cobrindo ciclo de vida da sessão, transições da simulação e erros de envio. Bytes brutos de FIX só em nível `Debug`/`Trace`. |
| NFR-OBS-02 | O SimCore DEVE expor contadores básicos (mensagens geradas, enviadas, erros) de forma consultável; integração com Prometheus é desejável, não obrigatória. |

### 3.5 Manutenibilidade (NFR-MAINT)

| ID | Requisito |
|----|-----------|
| NFR-MAINT-01 | O SimCore DEVE ter como alvo **.NET 10** e seguir `.kiro/steering/coding-standards.md` (nullable enable, warnings as errors, versões de NuGet fixadas, logging estruturado, sem secrets hardcoded). |
| NFR-MAINT-02 | A geração de mensagens FIX DEVE ser isolada atrás de uma abstração (`IFixMessageFactory` ou equivalente) para permitir adicionar novos tipos de mensagem sem alterar o acceptor ou o controlador de simulação. |
| NFR-MAINT-03 | O SimPanel DEVE usar TypeScript com tipos compartilhados/derivados do contrato da API (DTOs), para reduzir divergência entre front e back. |
| NFR-MAINT-04 | Documentação mínima (`MonteBravo` baseline): o simulador DEVE ter um `sim/README.md` descrevendo propósito, pré-requisitos (.NET 10 SDK + Node 20+), como subir o backend (`sim/backend`) e o frontend (`sim/frontend`), como apontar o NexusRouter para o acceptor e executar o round-trip de verificação de fidelidade, e como executar os testes do simulador. O README DEVE deixar claro o passo a passo de start dos **dois** projetos para uso em teste. |
| NFR-MAINT-05 | O simulador DEVE ser buildável e testável de forma autônoma via `sim/backend/DropCopySimulator.slnx`, sem depender da `NexusRouter.slnx` nem das props da raiz do workspace. |

---

## 4. Restrições e Premissas

- **Runtime do SimCore:** .NET 10, host genérico (`IHostedService`) + Minimal API, multiplataforma (Linux/Windows). Projeto/solução separado e isolado do NexusRouter. A lógica de wire FIX (framing/checksum) é implementada localmente por cópia enxuta — sem referência a assemblies do core (ver restrição de isolamento abaixo).
- **Protocolo:** FIX 4.4 (`BeginString = FIX.4.4`), seguindo o B3 EntryPoint (`.kiro/steering/b3-guidelines.md`). SBE e FIX 5.0 estão fora de escopo.
- **Direção do fluxo:** o simulador é o **acceptor**; o NexusRouter é o **cliente** que disca. O fluxo de aplicação é unidirecional B3 → NexusRouter (DropCopy). O simulador não processa ordens enviadas pelo cliente além do protocolo de sessão; v1 é drop copy.
- **Sessão única na v1:** o acceptor mantém **uma sessão FIX conectada por vez** (o NexusRouter). Suporte a múltiplas sessões concorrentes é uma extensão futura e não deve restringir o design inicial.
- **Portas padrão:** acceptor FIX em `localhost:9876`; API/WebSocket de controle em `localhost:8099`; dev server do SimPanel (Vite) em `localhost:5173`. Todas configuráveis.
- **Frontend:** React + Vite + TypeScript. Sem requisito de SSR. Build estático servível por qualquer host; em dev usa o dev server do Vite.
- **Escopo de teste:** ferramenta de laboratório/certificação interna. Não é um componente de produção e não precisa das garantias de latência/zero-alloc do NexusRouter.
- **Localização do código:** todo o simulador vive sob `sim/`, dividido em `sim/backend/` (.NET: `DropCopySimulator.slnx`) e `sim/frontend/` (React + Vite). O `README.md` do simulador fica em `sim/README.md`.
- **Isolamento total do NexusRouter:** o simulador NÃO DEVE ter nenhuma relação de build, pipeline ou referência de código com o NexusRouter. Critério de aceite: excluir a pasta `sim/` NÃO DEVE alterar em nada a build principal (`dotnet build NexusRouter.slnx`) nem os workflows de CI existentes. Especificamente: `sim/backend/` tem `Directory.Build.props` e `Directory.Packages.props` próprias (barreira de herança das props da raiz); `sim/` nunca é adicionado à `NexusRouter.slnx`; nenhum workflow em `.github/workflows/` é alterado para incluir o simulador; não há `ProjectReference` dos projetos do simulador para assemblies do NexusRouter (a lógica de wire é cópia enxuta local). `docs/FIX44Entrypoint.xml` e as steering são apenas referência de leitura.
- **Infra local:** o simulador não depende de Kafka/RabbitMQ/Postgres. Ele só precisa de uma porta TCP para o acceptor FIX e das portas HTTP/WS do painel. O NexusRouter, ao ingerir, usa sua própria stack (`docker-compose.yml`).
