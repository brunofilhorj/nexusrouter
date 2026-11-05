# DropCopy Simulator — Documento de Design de Arquitetura

> **Status do Documento:** Rascunho v1.0
> **Classificação:** Especificação de Engenharia Interna
> **Domínio:** Ferramenta de Teste — Simulador do DropCopy B3 EntryPoint (FIX 4.4)
> **Referência de Protocolo:** `.kiro/steering/b3-guidelines.md` + `docs/FIX44Entrypoint.xml`

---

## 1. Filosofia Arquitetural

O simulador é uma **ferramenta de teste**, não um componente de produção. A restrição definidora não é latência, e sim **fidelidade ao protocolo B3 EntryPoint** e **controlabilidade**: o operador precisa ligar/desligar a conexão, iniciar/pausar/parar o fluxo e moldar o conteúdo das mensagens a partir de um painel web, enquanto o NexusRouter ingere o resultado como se fosse o DropCopy real da B3.

O design separa três preocupações:

1. **Protocolo (FIX acceptor):** aceita a conexão do NexusRouter e fala FIX 4.4 de nível de sessão com fidelidade à B3.
2. **Geração (conteúdo):** produz `ExecutionReport` e demais mensagens de aplicação sintéticas, com campos coerentes e B3-específicos.
3. **Controle (API + UI):** comanda a simulação e injeta falhas a partir do SimPanel, sem interferir no caminho de envio FIX.

A regra de acoplamento é assimétrica, espelhando o NexusRouter: o **caminho de envio FIX nunca é bloqueado** pela telemetria do painel. A telemetria flui por um channel separado, amostrado e descartável.

---

## 2. Arquitetura Conceitual de Alto Nível

```
┌───────────────────────────────────────────────────────────────────────────────────┐
│                               SimCore (processo .NET 10)                            │
│                                                                                     │
│  ┌──────────────────────────────┐        ┌──────────────────────────────────────┐  │
│  │     CONTROL TIER (HTTP + WS)  │        │            FIX ACCEPTOR TIER          │  │
│  │                               │        │                                       │  │
│  │  ┌────────────────────────┐   │        │  ┌─────────────────────────────────┐  │  │
│  │  │ Minimal API            │   │ comandos│  │ FixAcceptorService              │  │  │
│  │  │  /state /sim/* /fault/*│───┼────────┼─►│  TcpListener (porta B3 fake)    │  │  │
│  │  │  /config               │   │        │  │  AcceptorSession (1..N)         │  │  │
│  │  └────────────────────────┘   │        │  │   ├ FixSessionFsm (A/0/1/2/4/5) │  │  │
│  │  ┌────────────────────────┐   │ eventos│  │   ├ MsgSeqNum (34) por sessão   │  │  │
│  │  │ TelemetryHub (WS/SSE)  │◄──┼────────┼──┤   └ outbound writer (PipeWriter)│  │  │
│  │  │  estado + contadores   │   │        │  └──────────────▲──────────────────┘  │  │
│  │  │  + feed amostrado      │   │        │                 │ frames FIX           │  │
│  │  └────────────────────────┘   │        │  ┌──────────────┴──────────────────┐  │  │
│  └───────────────────────────────┘        │  │ GENERATION TIER                 │  │  │
│                                            │  │                                 │  │  │
│  ┌──────────────────────────────┐          │  │ SimulationController            │  │  │
│  │ SimulationState (FSM)        │◄─────────┼──┤  Idle/Running/Paused            │  │  │
│  │ SimulationConfig (options)   │          │  │ OrderFlowGenerator (RateLimiter)│  │  │
│  └──────────────────────────────┘          │  │ IFixMessageFactory              │  │  │
│                                            │  │  └ ExecutionReportFactory        │  │  │
│         Telemetria (Channel,               │  │  └ RejectFactory (opcional)      │  │  │
│         amostrada, não bloqueia o envio) ◄─┼──┤ BusinessSeqNum (10016) monotônico│  │  │
│                                            │  └─────────────────────────────────┘  │  │
│                                            └───────────────────────────────────────┘  │
└───────────────────────────────────────────────────────────────────────────────────┘
                         ▲ HTTP / WebSocket                      │ FIX 4.4 / TCP
                         │                                       ▼
            ┌────────────┴────────────┐              ┌───────────────────────────┐
            │   SimPanel (React/Vite)  │              │ NexusRouter (FixSession    │
            │  controles + telemetria  │              │  inbound, disca o acceptor)│
            └──────────────────────────┘              └───────────────────────────┘
```

---

## 3. Detalhamento dos Tiers

### 3.1 FIX Acceptor Tier

O coração do protocolo. O `FixAcceptorService` é um `IHostedService` que abre um `TcpListener` em `Acceptor:ListenAddress`/`Acceptor:ListenPort` e aceita conexões. Cada conexão vira uma `AcceptorSession`.

**Framing e I/O.** Reutiliza `System.IO.Pipelines` para leitura, mas sem a exigência de zero-alloc do NexusRouter — o simulador pode usar buffers mais simples. A detecção de limite de frame FIX (BeginString + BodyLength ... CheckSum) reaproveita a mesma lógica do `Fix44FrameDecoder` do NexusRouter quando prático, ou uma cópia simplificada local. O cálculo de `CheckSum`/`BodyLength` segue `.kiro/steering/b3-guidelines.md` §15.

**`AcceptorSession`.** Representa uma conexão. Contém:
- `FixSessionFsm`: ciclo de vida `Disconnected → Connected → LogonReceived → Active → LogoutSent → Disconnected`. Diferente do NexusRouter (que é cliente), aqui o Logon é **recebido** e **respondido**.
- `MsgSeqNum` outbound da sessão (tag 34), monotônico via `Interlocked.Increment`, resetável no Logon com `ResetSeqNumFlag=Y` (FR-ACC-07).
- Writer outbound: um `PipeWriter`/`Stream` para enviar frames ao cliente. O `OrderFlowGenerator` enfileira frames aqui.
- Timer de heartbeat: emite `Heartbeat` (MsgType=0) a cada `HeartBtInt` quando ocioso; responde `TestRequest` com `Heartbeat` ecoando `TestReqID`.

**Logon (FR-ACC-04/05).** No primeiro frame:
- Valida `BeginString = FIX.4.4`, `MsgType = A`, `TargetCompID (56) = Acceptor:SenderCompId`.
- Honra `ResetSeqNumFlag (141)`.
- Responde Logon ACK (MsgType=A) com `HeartBtInt (108)` espelhado, `EncryptMethod (98) = 0` e `ApplicationLogonRspCode (6930) = 0`, salvo quando o modo de falha de Logon estiver configurado (FR-FAULT-03), caso em que envia o código de erro e encerra.

**Mensagens administrativas (FR-ACC-06).** `TestRequest`, `Heartbeat`, `ResendRequest`/`SequenceReset` (gap-fill básico) e `Logout` são tratados dentro da FSM. Mensagens de aplicação eventualmente recebidas do cliente (v1 drop copy) são apenas logadas e ignoradas no nível de negócio.

**Injeção de falha (FR-FAULT).** A `AcceptorSession` expõe operações comandadas pelo Control Tier:
- `DropConnection()`: fecha o socket sem Logout (simula queda de rede / possível COD).
- `ForceLogout(text)`: envia `Logout` com `Text` arbitrário (cenários "MsgSeqNum too low", "Password will expire").
- `InjectSequenceGap()`: pula N sequence numbers para provocar `ResendRequest` do cliente; responde com `SequenceReset (GapFillFlag=Y)`.
- `InjectMalformed(kind)`: emite um frame com checksum inválido ou campo faltando (opcional, FR-FAULT-05).

### 3.2 Generation Tier

**`SimulationController`.** Dono da máquina de estados da rodada (`Idle/Running/Paused`) e dos contadores. Expõe `Start/Pause/Resume/Stop` e valida as transições (FR-SIM-02). Transições inválidas lançam `InvalidSimulationStateException`, traduzida pela API em HTTP 409.

**`OrderFlowGenerator`.** Loop de background (uma `Task`) que, enquanto `Running`:
1. Aguarda o tick do `RateLimiter` derivado de `SimulationConfig.RatePerSecond` (FR-SIM-03). Implementação sugerida: `System.Threading.RateLimiting.TokenBucketRateLimiter` ou um laço com `PeriodicTimer` dimensionado ao alvo.
2. Pede ao `IFixMessageFactory` o próximo frame (ou sequência de frames do ciclo de vida da ordem).
3. Atribui `MsgSeqNum` da sessão (via `AcceptorSession`) e `BusinessSeqNum (10016)` monotônico do gerador.
4. Escreve o frame no writer outbound da `AcceptorSession`.
5. Faz `TryWrite` (não bloqueante) de um evento de telemetria amostrado no channel do Control Tier.
6. Incrementa contadores e, se um limite finito foi atingido (FR-SIM-04), transiciona para `Idle` e notifica.

O ciclo de vida da ordem (New → PartialFill* → Fill | Canceled | Rejected) é modelado por um pequeno gerador de estado por ordem, respeitando a proporção configurada de desfechos (FR-GEN-04). `OrdStatus (39)` é derivado de `ExecType (150)` + `LeavesQty (151)`, nunca copiado cegamente (`.kiro/steering/b3-guidelines.md` §6.6).

**`IFixMessageFactory`.** Abstração de geração (NFR-MAINT-02):

```
IFixMessageFactory
├── Name : string
└── int Build(Span<byte> destination, in OrderContext ctx, long seqNum, long businessSeqNum)
    // Escreve um frame FIX completo (header B3 + body + trailer com BodyLength e CheckSum)
    // em destination e retorna o número de bytes escritos.
```

Implementações v1:
- `ExecutionReportFactory` (MsgType=8) — obrigatória.
- `OrderCancelRejectFactory` (MsgType=9) — opcional.
- `BusinessMessageRejectFactory` (MsgType=j) — opcional.

`OrderContext` é um `readonly struct` com os dados resolvidos para uma ordem: `ClOrdID`, `OrderID`, `ExecID`, `ExecType`, `OrdStatus`, `Side`, instrumento (`SecurityID`/`Symbol`), quantidades (`OrderQty`/`LastQty`/`LeavesQty`/`CumQty`), preço, conta e `Parties`.

**Builder de FIX.** Um `B3FixMessageWriter` encapsula a escrita de campos `tag=value\x01`, o cálculo de `BodyLength (9)` (pós-header) e o `CheckSum (10)` (soma mod 256, 3 dígitos). Mantém o prefixo de header (BeginString, SenderCompID, TargetCompID) pré-formatado e injeta os campos B3-específicos de header: `BusinessSeqNum (10016)`, e quando aplicável `AdministrativeMessage (35020)`, `ProducerID (35036)`.

### 3.3 Control Tier (API + Telemetria)

**Host.** `WebApplication` (Minimal API do ASP.NET Core) no mesmo processo do `FixAcceptorService`. A API HTTP comanda; o WebSocket/SSE transmite telemetria.

**Endpoints (FR-API-01).**

| Método | Rota | Função |
|--------|------|--------|
| `GET` | `/v1/state` | Estado consolidado: sessão FIX + simulação + config atual |
| `POST` | `/v1/sim/start` | Inicia a rodada (body: config opcional) |
| `POST` | `/v1/sim/pause` | Pausa |
| `POST` | `/v1/sim/resume` | Retoma |
| `POST` | `/v1/sim/stop` | Para e zera o estado da rodada |
| `PUT` | `/v1/config` | Atualiza a `SimulationConfig` (permitido em `Idle`/`Paused`) |
| `POST` | `/v1/fault/drop` | Derruba a conexão FIX ativa |
| `POST` | `/v1/fault/logout` | Logout forçado (body: `{ text }`) |
| `POST` | `/v1/fault/logon-mode` | Define o modo de resposta ao Logon (sucesso / código de erro) |
| `POST` | `/v1/fault/seq-gap` | Injeta um gap de sequence number (opcional) |
| `GET` | `/v1/ws` | Upgrade WebSocket para o feed de telemetria |

**`TelemetryHub`.** Mantém a lista de clientes WebSocket conectados e um `Channel<TelemetryEvent>` bounded (overflow = DropOldest). O `OrderFlowGenerator` e a `AcceptorSession` apenas fazem `TryWrite` nesse channel; um consumidor de background serializa em JSON e faz broadcast para os sockets. O feed de mensagens é **amostrado** (ex.: no máximo K eventos/s ou 1 a cada N mensagens) para não saturar o browser (FR-API-03, NFR-PERF-02). Nenhuma dessas operações bloqueia o caminho de envio FIX.

**Tipos de evento de telemetria:**
- `SessionStateChanged { state, senderCompId, targetCompId, msgSeqNum, lastHeartbeatUtc }`
- `SimulationStateChanged { state, reason }`
- `CountersSnapshot { totalSent, ratePerSec, byExecType, errors, elapsedMs }` (emitido periodicamente, ex.: a cada 500 ms)
- `MessageSample { msgType, clOrdId, orderId, execType, ordStatus, side, instrument, businessSeqNum, tsUtc }`

**Autenticação (FR-API-06).** Quando `Control:AuthTokenEnvVar` estiver definido e a env var tiver valor, a API exige `Authorization: Bearer <token>` (comparação em tempo constante) e o WebSocket exige o mesmo token no primeiro frame ou em query de curta duração. Sem token configurado, modo aberto de laboratório (documentado).

**CORS (FR-API-04).** Origem do Vite (`http://localhost:5173` default) liberada via configuração `Control:AllowedOrigins`.

### 3.4 Estado e Configuração

**`SimulationState` (FSM).** `Idle → Running → Paused`. Transições: Start (`Idle→Running`), Pause (`Running→Paused`), Resume (`Paused→Running`), Stop (`Running|Paused→Idle`), e término automático (`Running→Idle`) ao atingir o limite finito.

**`SimulationConfig`.** POCO de options com reconfiguração **híbrida por campo** (FR-SIM-06). Os campos marcados `hot` podem ser trocados durante `Running` e entram em vigor no próximo ciclo do gerador; os demais só mudam em `Idle`/`Paused`:

```
SimulationConfig
├── TotalOrders        : long      // travado — 0 = ilimitado
├── RatePerSecond      : int       // hot — taxa alvo de msg/s
├── Accounts           : string[]  // travado — contas/clientes
├── AccountSelection   : enum      // travado — RoundRobin | Random
├── Instruments        : Instrument[]  // travado — SecurityID, Symbol, SecurityExchange
├── Outcome            : OutcomeMix // hot — proporção Fill/PartialFill/Canceled/Rejected
├── PartialFillsMax     : int       // travado — nº máx de partial fills antes do Fill
├── TestMessageIndicator : bool     // travado — tag 464 = Y quando true
└── EnabledMessageTypes : string[]  // travado — {"8"} v1; opcional {"9","j"}
```

**Política de reconfiguração.** O `SimulationController.UpdateConfig(SimulationConfig candidate)` compara o candidato com a config vigente e classifica o delta:
- Em `Idle`/`Paused`: aplica qualquer campo.
- Em `Running`: se o delta tocar apenas `RatePerSecond` e/ou `Outcome`, aplica de forma atômica (o `OrderFlowGenerator` lê a config vigente por referência volátil no topo de cada ciclo, então a troca de taxa/proporção vale a partir do próximo tick — nenhuma ordem em andamento é corrompida). Se o delta tocar qualquer campo travado, rejeita a atualização **inteira** (HTTP 409), sem aplicar o trecho hot — comportamento tudo-ou-nada para evitar estado parcial.

O `OrderFlowGenerator` mantém a config vigente em um campo lido via `Volatile.Read`/`Interlocked.Exchange` da referência imutável; o controller publica uma nova instância (snapshot) em vez de mutar a existente, garantindo que o loop sempre veja uma config coerente.

---

## 4. Frontend — SimPanel (React + Vite + TypeScript)

### 4.1 Estrutura

```
sim/frontend/
├── index.html
├── vite.config.ts
├── package.json
├── tsconfig.json
├── src/
│   ├── main.tsx
│   ├── App.tsx
│   ├── api/
│   │   ├── client.ts            # fetch wrapper + token
│   │   └── types.ts             # DTOs espelhando o contrato da API
│   ├── ws/
│   │   └── telemetry.ts         # conexão WS + reconexão automática
│   ├── state/
│   │   └── store.ts             # estado da app (contexto/reducer ou zustand)
│   ├── theme/
│   │   ├── tokens.css           # variáveis de tema (light/dark) via [data-theme] (Seção 4.5)
│   │   └── useTheme.ts          # hook de tema: prefers-color-scheme + persistência
│   └── components/
│       ├── SessionStatus.tsx    # FR-UI-02
│       ├── SimulationStatus.tsx # FR-UI-03
│       ├── SimulationControls.tsx # FR-UI-04
│       ├── ConfigForm.tsx       # FR-UI-05
│       ├── FaultControls.tsx    # FR-UI-06
│       ├── MessageFeed.tsx      # FR-UI-07
│       ├── ConnectivityBadge.tsx # FR-UI-08
│       ├── ThemeToggle.tsx      # alterna claro/escuro (Seção 4.5)
│       └── ConfirmDialog.tsx    # modal de confirmação de ações importantes (Seção 4.5)
```

### 4.2 Camada de API e tipos

`api/types.ts` define os DTOs em TypeScript espelhando o contrato do SimCore (NFR-MAINT-03). `api/client.ts` injeta o `Authorization: Bearer` quando um token estiver configurado (via variável de ambiente do Vite `VITE_SIM_TOKEN`, usada só em dev). Base URL do SimCore via `VITE_SIM_API` (default `http://localhost:8099`).

### 4.3 Telemetria em tempo real

`ws/telemetry.ts` abre o WebSocket (`/v1/ws`), processa os eventos tipados e atualiza o store. Implementa reconexão com backoff simples e expõe o estado de conectividade para o `ConnectivityBadge` (FR-UI-08). O `MessageFeed` mantém um ring buffer das últimas N amostras no cliente para não crescer indefinidamente.

### 4.4 Controles e habilitação por estado

`SimulationControls` deriva o `disabled` de cada botão do estado atual recebido por telemetria:

| Estado | Start | Pause | Resume | Stop |
|--------|-------|-------|--------|------|
| Idle    | ✅ | ❌ | ❌ | ❌ |
| Running | ❌ | ✅ | ❌ | ✅ |
| Paused  | ❌ | ❌ | ✅ | ✅ |

`ConfigForm` aplica a política híbrida de FR-SIM-06 na UI. Em `Running`, apenas os campos hot (`RatePerSecond` e `Outcome`) ficam editáveis e submetíveis; os campos travados ficam desabilitados com uma dica ("pause para alterar"). Em `Idle`/`Paused`, todos os campos são editáveis. O envio em `Running` manda só o delta hot, evitando o 409 do servidor; o 409 permanece como rede de segurança caso o estado mude entre a leitura e o submit.

### 4.5 UX e layout

O SimPanel é uma **ferramenta de operador**, não um produto de cliente. O desenho prioriza densidade de informação, controle direto e baixa chance de ação acidental, em vez de identidade visual. Ele não adota o design system da MonteBravo nem nenhuma biblioteca de componentes de UI — os componentes são próprios, montados com CSS Modules e variáveis de tema. Isso mantém o simulador leve e sem acoplamento a padrões de produto.

**Princípios.**
- A UI é majoritariamente **reativa**: reflete o estado empurrado por telemetria (Seção 4.3). Os poucos pontos de ação (controles, config, falhas) são deliberados.
- **Estado comunicado por cor + rótulo**, nunca só por cor (acessibilidade). Convenção: Idle = neutro, Running = verde, Paused = âmbar, desconectado/erro = vermelho, sempre acompanhados de texto.
- Ação de **risco assimétrico** recebe atrito proporcional: parar a rodada e injetar falhas exigem confirmação explícita (ver abaixo).

**Layout.** Dashboard de página única, sem roteamento. Uma topbar fixa no topo e duas colunas abaixo:
- **Topbar (sempre visível):** `ConnectivityBadge` (API/WS up/down), o estado corrente da simulação (Idle/Running/Paused) como fonte da verdade sempre à vista, e o seletor de tema.
- **Coluna esquerda — Controle:** `SimulationControls`, `ConfigForm` e `FaultControls`. A zona de falhas é visualmente separada e marcada como perigosa (moldura/realce de alerta).
- **Coluna direita — Observação:** `SessionStatus` e `SimulationStatus` como cards compactos de métricas no topo, e o `MessageFeed` ocupando o restante — tabela rolável, dados FIX em fonte monoespaçada, item mais recente no topo, com opção de pausar o auto-scroll para inspeção.

**Tema claro e escuro.** O painel suporta tema **claro e escuro**, com alternância na topbar e preferência persistida (`localStorage`); respeita `prefers-color-scheme` na primeira carga, com **escuro como padrão**. As cores são definidas por variáveis CSS (design tokens) trocadas por um atributo `data-theme` na raiz, de modo que nenhum componente referencia cores fixas diretamente.

**Confirmação de ações importantes.** Ações destrutivas ou de alto impacto — `Stop` da simulação e todas as injeções de falha (`Drop Connection`, `Force Logout`, `Sequence Gap`) — abrem um **modal de confirmação** que nomeia a ação e seu efeito, com um botão primário de confirmação e cancelamento. Não se usa duplo clique. Ações de baixo risco (`Start`, `Pause`, `Resume`, salvar config) são diretas, sem modal. Erros retornados pela API (ex.: 409) aparecem como notificação discreta (toast), não como modal.

---

## 5. Estrutura do Projeto

O simulador inteiro vive sob `sim/`, dividido em `backend/` (.NET) e `frontend/` (React + Vite). As duas pilhas são independentes entre si e **completamente isoladas** do NexusRouter (ver Seção 5.1).

```
sim/
├── README.md                              # como subir backend + frontend e testar (ver Seção 5.2)
├── backend/
│   ├── DropCopySimulator.slnx
│   ├── Directory.Build.props              # BARREIRA: corta a herança das props da raiz
│   ├── Directory.Packages.props           # versões de NuGet próprias (CPM isolado)
│   ├── src/
│   │   ├── DropCopySim.Core/              # protocolo + geração (sem ASP.NET)
│   │   │   ├── Fix/
│   │   │   │   ├── B3FixMessageWriter.cs   # escrita de campos, BodyLength, CheckSum
│   │   │   │   ├── Fix44FrameScanner.cs    # detecção de limite de frame
│   │   │   │   └── FixTags.cs              # constantes de tags B3
│   │   │   ├── Acceptor/
│   │   │   │   ├── FixAcceptorService.cs   # IHostedService, TcpListener
│   │   │   │   ├── AcceptorSession.cs      # uma conexão, FSM, MsgSeqNum, writer
│   │   │   │   ├── FixSessionFsm.cs
│   │   │   │   └── AcceptorOptions.cs
│   │   │   ├── Generation/
│   │   │   │   ├── SimulationController.cs  # FSM Idle/Running/Paused + contadores
│   │   │   │   ├── OrderFlowGenerator.cs    # loop de envio + rate limiting
│   │   │   │   ├── IFixMessageFactory.cs
│   │   │   │   ├── ExecutionReportFactory.cs
│   │   │   │   ├── OrderCancelRejectFactory.cs # opcional
│   │   │   │   ├── BusinessMessageRejectFactory.cs # opcional
│   │   │   │   ├── OrderContext.cs
│   │   │   │   ├── OrderLifecycleModel.cs
│   │   │   │   └── SimulationConfig.cs
│   │   │   └── Telemetry/
│   │   │       ├── TelemetryHub.cs
│   │   │       └── TelemetryEvent.cs
│   │   └── DropCopySim.Host/               # Minimal API + wiring de DI
│   │       ├── Program.cs
│   │       ├── appsettings.json
│   │       ├── Endpoints/
│   │       │   ├── SimulationEndpoints.cs
│   │       │   ├── FaultEndpoints.cs
│   │       │   └── StateEndpoints.cs
│   │       └── Auth/
│   │           └── TokenAuth.cs
│   └── tests/
│       ├── DropCopySim.Core.Tests/
│       │   ├── B3FixMessageWriterTests.cs
│       │   ├── ExecutionReportFactoryTests.cs
│       │   ├── FixSessionFsmTests.cs
│       │   └── SimulationControllerTests.cs
│       └── DropCopySim.Integration.Tests/
│           ├── MinimalFixClient.cs          # cliente FIX 4.4 mínimo de teste (sem NexusRouter)
│           └── AcceptorRoundTripTests.cs     # Logon + ingestão + falhas via cliente mínimo
└── frontend/                               # SimPanel (React + Vite) — ver Seção 4
```

### 5.1 Isolamento total do NexusRouter

O simulador **não** tem nenhuma relação de build, pipeline ou referência com o NexusRouter. O critério de aceite é concreto: **excluir a pasta `sim/` não pode alterar em nada a build principal** (`dotnet build NexusRouter.slnx`) nem a CI existente.

| Ponto de acoplamento potencial | Como é neutralizado |
|--------------------------------|---------------------|
| Herança de `Directory.Build.props` / `Directory.Packages.props` da raiz | `sim/backend/` tem as suas próprias; o MSBuild para de subir a árvore ao encontrá-las. O simulador define o próprio TFM, warnings e versões de NuGet, sem herdar nada da raiz. |
| Solução principal `NexusRouter.slnx` | Lista projetos explicitamente (sem glob); nunca inclui `sim/`. O simulador tem a sua `sim/backend/DropCopySimulator.slnx`. |
| CI `.github/workflows/build-and-test.yml` e deploys | Operam sobre `NexusRouter.slnx` por caminho explícito. O simulador **não** adiciona nem altera nenhum step nesses workflows. CI própria do simulador está fora do escopo desta spec. |
| Dependência de código / assemblies do core | **Nenhuma.** Sem `ProjectReference` ao NexusRouter. A lógica de wire (framing/checksum) é cópia enxuta local. `docs/FIX44Entrypoint.xml` e as steering servem só como referência de leitura. |
| Teste de fidelidade (T-34) | Usa um cliente FIX 4.4 mínimo próprio (`MinimalFixClient`), **não** o `FixSession` do NexusRouter. O round-trip contra o NexusRouter real é um procedimento manual do README (Seção 5.2), não um teste linkado. |

### 5.2 README do simulador (`sim/README.md`)

Segue o baseline de documentação MonteBravo e ensina a usar os **dois** projetos no teste:
1. **Pré-requisitos:** .NET 10 SDK; Node 20+.
2. **Backend:** `dotnet run --project sim/backend/src/DropCopySim.Host` — acceptor FIX em `localhost:9876`, controle HTTP/WS em `localhost:8099`.
3. **Frontend:** `npm install && npm run dev` em `sim/frontend/` — painel em `localhost:5173`.
4. **Round-trip com o NexusRouter real (verificação de fidelidade manual):** apontar uma `Session` do NexusRouter para `localhost:9876` (`SenderCompId=CLIENT1`, `TargetCompId=ENTRYPOINT-FIRM-0001`, `FrameDecoder=Fix44`, `DeliveryMode=Parse`), subir o NexusRouter, operar a simulação pelo painel e confirmar no log de auditoria / TimescaleDB a ausência de `Reject`.
5. **Testes do simulador:** `dotnet test sim/backend/DropCopySimulator.slnx`.

---

## 6. Modelo de Configuração (SimCore)

```
DropCopySim:
  Acceptor:
    ListenAddress: "0.0.0.0"
    ListenPort: 9876                 # NexusRouter aponta a sessão inbound para cá
    BeginString: "FIX.4.4"
    SenderCompId: "ENTRYPOINT-FIRM-0001"   # lado B3 (simulador)
    TargetCompId: "CLIENT1"                # SenderCompID esperado do NexusRouter
    HeartBtIntSeconds: 20
    LogonMode: Success               # Success | InvalidCredentials | NotAuthorized
    Tls:
      Enabled: false
      CertificatePath: ""
      CertificatePasswordEnvVar: "SIM_CERT_PWD"

  Simulation:
    TotalOrders: 0                   # 0 = ilimitado
    RatePerSecond: 10
    Accounts: ["ACC001", "ACC002", "ACC003"]
    AccountSelection: RoundRobin     # RoundRobin | Random
    Instruments:
      - SecurityId: 200001
        Symbol: "PETR4"
        SecurityExchange: "BVMF"
      - SecurityId: 200002
        Symbol: "VALE3"
        SecurityExchange: "BVMF"
    Outcome:
      FillPct: 70
      PartialFillPct: 20
      CanceledPct: 7
      RejectedPct: 3
    PartialFillsMax: 3
    TestMessageIndicator: true
    EnabledMessageTypes: ["8"]

  Control:
    HttpPort: 8099
    AllowedOrigins: ["http://localhost:5173"]
    AuthTokenEnvVar: "SIM_CONTROL_TOKEN"   # vazio/ausente = modo aberto de laboratório
    TelemetryChannelCapacity: 4096
    MessageSampleMaxPerSecond: 50
    CountersIntervalMs: 500
```

---

## 7. Fluxo de Dados — Sequência End-to-End

```
[SimPanel] --POST /v1/sim/start--> [SimulationEndpoints]
        │                                  │
        │                                  ▼
        │                        [SimulationController.Start()]  (Idle → Running)
        │                                  │
        │                                  ▼
        │                        [OrderFlowGenerator loop (Task)]
        │                                  │
        │             ┌────────────────────┼─────────────────────────┐
        │             │ a cada tick do RateLimiter                    │
        │             ▼                                               │
        │   [OrderLifecycleModel → OrderContext]                      │
        │             │                                               │
        │             ▼                                               │
        │   [IFixMessageFactory.Build(span, ctx, seqNum, bizSeq)]     │
        │             │  frame FIX (header B3 + body + BodyLength/CheckSum)
        │             ▼                                               │
        │   [AcceptorSession.SendOutbound(frame)] --FIX/TCP--> [NexusRouter FixSession]
        │             │                                               │
        │             ▼                                               │
        │   [TelemetryHub.TryWrite(MessageSample)]  (amostrado, não bloqueia)
        │             │                                               │
        └─────────────┤ WebSocket broadcast                          │
                      ▼                                               │
                 [SimPanel atualiza feed + contadores]               │
                                                                     │
--- Injeção de falha ---                                             │
[SimPanel] --POST /v1/fault/drop--> [FaultEndpoints] -> [AcceptorSession.DropConnection()]
                                                              │
                                                              ▼ socket fechado sem Logout
                                              [NexusRouter detecta queda → reconecta (backoff)]
                                                              │
                                              [FixAcceptorService volta a aceitar]
```

---

## 8. Principais Decisões de Design e Justificativas

| Decisão | Alternativa Considerada | Justificativa |
|---------|------------------------|---------------|
| Solução separada em `sim/backend/`, isolada por props-barreira | Projeto dentro da `NexusRouter.slnx`, ou herdando as props da raiz | Excluir `sim/` não afeta build nem CI do core; o simulador não herda props/CPM da raiz nem é listado na solução/CI principal (Seção 5.1) |
| T-34 com cliente FIX mínimo próprio; round-trip real no README | `ProjectReference` ao `FixSession` do NexusRouter no teste | Isolamento de código total (nenhuma dependência ao core); a verificação com o NexusRouter real vira procedimento manual, que é como a ferramenta é usada no dia a dia |
| Acceptor (servidor), NexusRouter é o cliente | Simulador disca o NexusRouter | Espelha a topologia real: o NexusRouter é quem conecta ao gateway B3 (`nexusrouter` FR-ING-01) |
| Minimal API + WebSocket no mesmo processo do acceptor | Serviço de controle separado | Simplicidade; comandos e envio FIX compartilham estado em memória sem IPC |
| Telemetria por channel amostrado e descartável | Emitir cada mensagem ao front | Alta frequência de geração saturaria o browser; o envio FIX nunca pode esperar o painel (NFR-PERF-02) |
| `IFixMessageFactory` por tipo de mensagem | Um gerador monolítico com `switch` | Adicionar `OrderCancelReject`/`BusinessMessageReject` sem tocar no acceptor nem no controller (NFR-MAINT-02) |
| `OrdStatus` derivado de `ExecType` + `LeavesQty` | Copiar um `OrdStatus` fixo | B3 trata os dois campos como independentes; o fluxo precisa ser coerente (`b3-guidelines` §6.6) |
| Reconfiguração híbrida (taxa/desfecho ao vivo; resto travado) | Rejeitar toda reconfig em `Running`, ou aceitar tudo | Teste de carga quer rampar a taxa sem parar o fluxo; travar total/contas/instrumentos evita corromper contadores e ordens em andamento (FR-SIM-06) |
| Config lida como snapshot imutável por ciclo (`Volatile`) | Mutar a config in-place durante `Running` | Garante que o loop sempre veja uma config coerente; a troca hot vale do próximo tick sem race |
| React + Vite + TypeScript | Framework server-side ou HTML puro | SPA leve, dev server rápido, tipos compartilhados com o contrato da API (NFR-MAINT-03) |
| Reaproveitar framing/checksum por cópia enxuta | Referenciar assemblies do NexusRouter | Desacopla versões; o simulador não precisa das garantias de hot path, só da regra de wire |
| `TestMessageIndicator=Y` por default | Produção-like por default | Espelha o ambiente de certificação B3 e evita confundir fluxo de teste com produção (`b3-guidelines` §14) |

---

## 9. Verificação de Fidelidade

A fidelidade é verificada em dois níveis, mantendo o simulador isolado de código do NexusRouter (Seção 5.1):

**Automatizado (T-34), sem NexusRouter.** Um cliente FIX 4.4 mínimo de teste (`MinimalFixClient`, dentro do próprio projeto de teste do simulador) conecta ao acceptor, completa o Logon, recebe N `ExecutionReport` e valida parsing, `BodyLength`/`CheckSum` e campos obrigatórios — sem referência ao `FixSession` do NexusRouter. Cobre também Drop Connection e o modo de Logon com falha.

**Manual (README, Seção 5.2), round-trip real.** A prova de ponta a ponta é o NexusRouter ingerir o fluxo sem rejeições (NFR-FID-01), executada como procedimento documentado:

1. Subir o SimCore (`DropCopySim.Host`) com o acceptor em `localhost:9876`.
2. Configurar uma `Session` do NexusRouter apontando `Host=localhost`, `Port=9876`, `SenderCompId=CLIENT1`, `TargetCompId=ENTRYPOINT-FIRM-0001`, `FrameDecoder=Fix44`, `DeliveryMode=Parse` (para validar o parsing completo).
3. Start na simulação pelo SimPanel a taxa baixa (ex.: 10 msg/s).
4. Verificar no log de auditoria / TimescaleDB do NexusRouter que as `ExecutionReport` chegam, são parseadas e roteadas, sem `Reject`.
5. Exercitar reconexão: Drop Connection no painel → confirmar que o NexusRouter reconecta com backoff e a sessão volta a `Active`.
6. Exercitar erro de Logon: `logon-mode = InvalidCredentials` → confirmar que o NexusRouter registra a falha e não entra em loop de reconexão (`b3-guidelines` §13).
