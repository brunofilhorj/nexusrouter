# DropCopy Simulator — Plano de Tarefas Incremental

> **Status do Documento:** Rascunho v1.0
> **Classificação:** Especificação de Engenharia Interna
> **Metodologia:** Spec-Driven Development (SDD) — Execução Sequencial de Tarefas

---

## Modelo de Execução

As tarefas são executadas **uma por vez**, em ordem. Cada tarefa é autocontida, tem entradas/saídas explícitas e produz um artefato verificável. Fases posteriores dependem da saída das anteriores. Não execute duas tarefas em paralelo no mesmo working tree.

Ao concluir uma tarefa, marque `- [x]`. As entradas referenciam `requirements.md` e `design.md` desta spec, além de `.kiro/steering/b3-guidelines.md`, `.kiro/steering/coding-standards.md` e `docs/FIX44Entrypoint.xml`.

**Visão Geral das Fases:**

| Fase | Área de Foco | Tarefas |
|------|-------------|---------|
| Fase 1 | Scaffold do backend (`sim/backend`) isolado | T-01 → T-03 |
| Fase 2 | Núcleo FIX (framing, writer, tags) | T-04 → T-06 |
| Fase 3 | Acceptor FIX 4.4 (sessão + protocolo) | T-07 → T-11 |
| Fase 4 | Geração de Mensagens de Aplicação | T-12 → T-16 |
| Fase 5 | Controle de Simulação e Injeção de Falhas | T-17 → T-19 |
| Fase 6 | API de Controle + Telemetria (Host) | T-20 → T-23 |
| Fase 7 | Frontend SimPanel (React + Vite) | T-24 → T-30 |
| Fase 8 | Testes e Verificação de Fidelidade | T-31 → T-34 |

---

## Fase 1 — Scaffold do Backend (`sim/backend`) Isolado

- [x] T-01 — Inicializar a Solução `DropCopySimulator` e os Projetos .NET 10

**Objetivo:** Criar a solução separada do simulador e os projetos esqueleto.

**Entradas:** `requirements.md` (restrição de isolamento total, NFR-MAINT-05), `design.md` (Seções 5, 5.1)

**Passos:**
1. Criar a pasta `sim/backend/` e `sim/backend/DropCopySimulator.slnx` (formato de solução XML do .NET 10). **Não** adicionar à `NexusRouter.slnx`.
2. Criar `src/DropCopySim.Core` como `classlib` em `sim/backend/`.
3. Criar `src/DropCopySim.Host` como projeto `web` (Minimal API / `WebApplication`) em `sim/backend/`.
4. Criar projetos de teste `xunit` em `sim/backend/tests/`: `DropCopySim.Core.Tests` e `DropCopySim.Integration.Tests`.
5. Referências projeto-a-projeto: Host → Core; ambos os testes → Core. **Nenhum** `ProjectReference` para projetos do NexusRouter — o teste de integração (T-34) usa um cliente FIX mínimo próprio.
6. **Barreira de herança (crítico para o isolamento):** criar `sim/backend/Directory.Build.props` com `<TargetFramework>net10.0</TargetFramework>`, `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` (coding-standards §1), e `sim/backend/Directory.Packages.props` (habilitando Central Package Management com `<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>` e as versões fixadas do simulador). A presença desses arquivos impede que os `.csproj` do simulador subam a árvore e herdem as props/CPM da raiz do workspace.
7. Adicionar `sim/.gitignore` cobrindo o mundo Node do frontend (`node_modules/`, `dist/`) além de `bin/`/`obj/` (caso o `.gitignore` da raiz não cubra esses caminhos sob `sim/`).

**Saída:** `dotnet build sim/backend/DropCopySimulator.slnx` conclui com zero avisos. **Verificação de isolamento:** `dotnet build NexusRouter.slnx` não enxerga nenhum projeto do simulador, e remover `sim/` não altera a build principal nem a CI.

---

- [x] T-02 — Definir `SimulationConfig`, `AcceptorOptions` e enums de controle

**Objetivo:** POCOs de configuração fortemente tipados do SimCore.

**Entradas:** `requirements.md` (NFR-CFG), `design.md` (Seções 3.4, 6)

**Passos:**
1. Criar em `DropCopySim.Core/Generation/`: `SimulationConfig.cs` (campos da Seção 6), `OutcomeMix.cs`, `Instrument.cs`, e os enums `AccountSelection` (`RoundRobin`, `Random`).
2. Criar em `DropCopySim.Core/Acceptor/`: `AcceptorOptions.cs` (listen, comp IDs, `HeartBtIntSeconds`, `LogonMode`, TLS) e o enum `LogonMode` (`Success`, `InvalidCredentials`, `NotAuthorized`).
3. Validação via `DataAnnotations` ou `IValidateOptions<T>`. Nenhum secret no POCO — token referencia nome de env var (coding-standards §9).
4. Documentação XML em todos os tipos públicos.

**Saída:** POCOs compilam; valores inválidos de config falham rápido na inicialização.

---

- [x] T-03 — Definir os tipos de estado e telemetria

**Objetivo:** Tipos compartilhados de estado da simulação e eventos de telemetria.

**Entradas:** `design.md` (Seções 3.3, 3.4)

**Passos:**
1. Criar `DropCopySim.Core/Generation/SimulationState.cs` — enum `Idle`, `Running`, `Paused`.
2. Criar `DropCopySim.Core/Telemetry/TelemetryEvent.cs` — hierarquia (ou union discriminada via `record`) com `SessionStateChanged`, `SimulationStateChanged`, `CountersSnapshot`, `MessageSample` (campos da Seção 3.3).
3. Criar um `SimulationCounters.cs` com total enviado, por `ExecType`, erros, início da rodada (contadores via `Interlocked`).
4. Documentação XML.

**Saída:** Tipos compilam e são serializáveis em JSON (`System.Text.Json`).

---

## Fase 2 — Núcleo FIX

- [x] T-04 — Implementar `FixTags` e `Fix44FrameScanner`

**Objetivo:** Constantes de tags B3 e detecção de limite de frame FIX 4.4.

**Entradas:** `.kiro/steering/b3-guidelines.md` (§2, §5, §15), `docs/FIX44Entrypoint.xml`

**Passos:**
1. Criar `DropCopySim.Core/Fix/FixTags.cs` com constantes das tags usadas: padrão FIX (8, 9, 10, 34, 35, 49, 56, 52, 98, 108, 141, 553, 554) e B3-específicas (10016, 10029, 9773, 464, 6930, 35020, 35036).
2. Criar `DropCopySim.Core/Fix/Fix44FrameScanner.cs`: dado um buffer (`ReadOnlySequence<byte>`/`Span<byte>`), localiza um frame completo (de `8=FIX.4.4\x01` até `10=XXX\x01`). Reaproveitar a lógica do `Fix44FrameDecoder` do NexusRouter como referência (cópia enxuta, sem exigência de zero-alloc).
3. Documentação XML.

**Saída:** O scanner isola frames completos de um stream, inclusive quando o frame chega em segmentos.

---

- [x] T-05 — Implementar `B3FixMessageWriter` (BodyLength + CheckSum)

**Objetivo:** Escrever campos FIX em um buffer e finalizar `BodyLength (9)` e `CheckSum (10)` conforme a regra B3.

**Entradas:** `.kiro/steering/b3-guidelines.md` (§15), `design.md` (Seção 3.2)

**Passos:**
1. Criar `DropCopySim.Core/Fix/B3FixMessageWriter.cs`:
   - Métodos para escrever um campo (`int tag`, valor como `ReadOnlySpan<byte>`/`long`/string ASCII) em um `Span<byte>` avançando um cursor.
   - Método de finalização: computa `BodyLength` (bytes entre o fim da tag 9 e o início da tag 10) e o `CheckSum` (soma de todos os bytes até, exclusive, `10=`, mod 256, 3 dígitos ASCII com zero à esquerda).
   - Helper para `TransactTime`/`SendingTime` em UTC `YYYYMMDD-HH:MM:SS.sss`.
2. Documentação XML referenciando a regra de checksum da steering.

**Saída:** O writer produz frames FIX com `BodyLength` e `CheckSum` corretos. Verificável em T-31 contra um parser.

---

- [x] T-06 — Implementar helpers de header B3 e `Parties`

**Objetivo:** Centralizar a escrita do header FIX + campos B3 proprietários e do componente `Parties`.

**Entradas:** `.kiro/steering/b3-guidelines.md` (§2, §9), `docs/FIX44Entrypoint.xml`

**Passos:**
1. Estender `B3FixMessageWriter` (ou criar `B3HeaderWriter`) para escrever: BeginString, MsgType, SenderCompID, TargetCompID, MsgSeqNum, SendingTime, e `BusinessSeqNum (10016)`.
2. Criar helper para o grupo `Parties` (NoPartyIDs 453) com `PartyID (448)`, `PartyIDSource (447)`, `PartyRole (452)` — mínimo `PartyRole=4` (Clearing Firm) e `PartyRole=3` (Client ID).
3. Documentação XML.

**Saída:** Header B3 e `Parties` escritos de forma reutilizável pelos factories de mensagem.

---

## Fase 3 — Acceptor FIX 4.4

- [x] T-07 — Implementar `FixSessionFsm` (lado acceptor)

**Objetivo:** Máquina de estados de sessão do lado servidor (Logon recebido/respondido).

**Entradas:** `requirements.md` (FR-ACC-04 até FR-ACC-08), `design.md` (Seção 3.1)

**Passos:**
1. Criar `DropCopySim.Core/Acceptor/FixSessionFsm.cs`: estados `Disconnected → Connected → LogonReceived → Active → LogoutSent → Disconnected`, transições validadas (lançar `InvalidSessionStateException` em transição ilegal).
2. Rastreio de heartbeat: timestamp da última atividade; método de staleness.
3. Documentação XML.

**Saída:** FSM compila e cobre o ciclo de vida do acceptor.

---

- [x] T-08 — Implementar `AcceptorSession` — socket, leitura e writer outbound

**Objetivo:** Representar uma conexão aceita com leitura via Pipelines e envio de frames.

**Entradas:** `requirements.md` (FR-ACC-01, FR-ACC-02, FR-ACC-07, FR-ACC-09), `design.md` (Seção 3.1)

**Passos:**
1. Criar `DropCopySim.Core/Acceptor/AcceptorSession.cs`:
   - Recebe o socket aceito, `AcceptorOptions`, `ILogger`.
   - Loop de leitura com `PipeReader` + `Fix44FrameScanner`.
   - `MsgSeqNum` outbound via `Interlocked.Increment`, resetável.
   - `SendOutbound(ReadOnlySpan<byte> frame)`: escreve no writer; serializa acessos concorrentes de forma simples (fora do hot path do NexusRouter, lock leve é aceitável aqui).
2. Expor eventos/hooks de ciclo de vida para a telemetria.

**Saída:** Uma conexão aceita lê frames e envia frames de volta ao cliente.

---

- [x] T-09 — Implementar `FixAcceptorService` — `TcpListener` e aceitação

**Objetivo:** `IHostedService` que escuta e cria `AcceptorSession` por conexão.

**Entradas:** `requirements.md` (FR-ACC-01, FR-ACC-02, FR-FAULT-06)

**Passos:**
1. Criar `DropCopySim.Core/Acceptor/FixAcceptorService.cs` implementando `IHostedService`:
   - `StartAsync`: abre `TcpListener` em `ListenAddress`/`ListenPort`, inicia o loop de aceitação.
   - Cria uma `AcceptorSession` por conexão aceita; v1 mantém referência à sessão ativa para comandos de falha.
   - Opcional: wrap TLS de servidor quando `Tls.Enabled`.
   - Após Drop Connection, volta a aceitar reconexões (FR-FAULT-06).
   - `StopAsync`: encerra sessões e para o listener.
2. Documentação XML.

**Saída:** O acceptor escuta e aceita a conexão do NexusRouter.

---

- [x] T-10 — Implementar o handshake de Logon e o protocolo administrativo

**Objetivo:** Validar e responder o Logon, tratar Heartbeat/TestRequest/Logout.

**Entradas:** `requirements.md` (FR-ACC-04, FR-ACC-05, FR-ACC-06, FR-ACC-08), `.kiro/steering/b3-guidelines.md` (§1, §5.1)

**Passos:**
1. Em `AcceptorSession`/`FixSessionFsm`:
   - No primeiro frame, validar `BeginString=FIX.4.4`, `MsgType=A`, `TargetCompID(56)=SenderCompId` do acceptor; honrar `ResetSeqNumFlag(141)`.
   - Responder Logon ACK (MsgType=A) com `HeartBtInt(108)`, `EncryptMethod(98)=0`, `ApplicationLogonRspCode(6930)=0` (salvo modo de falha — T-19).
   - Emitir `Heartbeat(0)` a cada `HeartBtInt` ocioso; responder `TestRequest(1)` com `Heartbeat` ecoando `TestReqID`.
   - Tratar `Logout(5)` com Logout de resposta e encerramento gracioso.
2. Usar `B3FixMessageWriter` para montar os frames administrativos.

**Saída:** O NexusRouter completa o Logon contra o simulador e mantém heartbeats.

---

- [x] T-11 — Implementar `ResendRequest`/`SequenceReset` (gap-fill básico)

**Objetivo:** Recuperação de sequência de nível de sessão FIX.

**Entradas:** `requirements.md` (FR-ACC-06), `.kiro/steering/b3-guidelines.md` (§12.1)

**Passos:**
1. Em `AcceptorSession`: ao receber `ResendRequest(2)`, responder com `SequenceReset(4)` `GapFillFlag=Y` cobrindo o intervalo solicitado (v1 não reenvia o conteúdo original — gap-fill é suficiente).
2. Suportar reset absoluto quando aplicável.

**Saída:** Um `ResendRequest` do cliente é respondido com gap-fill coerente.

---

## Fase 4 — Geração de Mensagens de Aplicação

- [ ] T-12 — Definir `IFixMessageFactory`, `OrderContext` e `OrderLifecycleModel`

**Objetivo:** Abstração de geração e o modelo de ciclo de vida da ordem.

**Entradas:** `requirements.md` (FR-GEN-04, NFR-MAINT-02), `design.md` (Seção 3.2)

**Passos:**
1. Criar `DropCopySim.Core/Generation/IFixMessageFactory.cs` com `Name` e `int Build(Span<byte> destination, in OrderContext ctx, long seqNum, long businessSeqNum)`.
2. Criar `OrderContext.cs` (`readonly struct`) com os campos resolvidos de uma ordem (IDs, `ExecType`, `OrdStatus`, `Side`, instrumento, quantidades, preço, conta).
3. Criar `OrderLifecycleModel.cs`: produz a sequência de `OrderContext` de uma ordem (New → PartialFill* → Fill|Canceled|Rejected), com `OrdStatus` derivado de `ExecType`+`LeavesQty` (`b3-guidelines` §6.6) e proporção de desfechos de `OutcomeMix`.
4. Documentação XML.

**Saída:** O modelo de ciclo de vida gera sequências coerentes de contexto de ordem.

---

- [ ] T-13 — Implementar `ExecutionReportFactory` (MsgType=8)

**Objetivo:** Produzir `ExecutionReport` fiéis ao `FIX44Entrypoint.xml`.

**Entradas:** `requirements.md` (FR-GEN-01 até FR-GEN-07, FR-GEN-09), `.kiro/steering/b3-guidelines.md` (§6, §7, §8, §9), `docs/FIX44Entrypoint.xml` (message `ExecutionReport`)

**Passos:**
1. Criar `DropCopySim.Core/Generation/ExecutionReportFactory.cs` implementando `IFixMessageFactory`:
   - Campos obrigatórios: `OrderID(37)`, `ExecID(17)`, `ExecType(150)`, `OrdStatus(39)`, `Side(54)`, `Instrument`, `LeavesQty(151)`, `CumQty(14)`, `AvgPx(6)`, `Parties`.
   - Recomendados: `ClOrdID(11)`, `LastQty(32)`, `LastPx(31)`, `TransactTime(60)`.
   - B3: `BusinessSeqNum(10016)`, `BTSFinalTxOrdStatus(10029)`, e `MMProtectionReset(9773)` quando aplicável.
   - Instrumento via `SecurityID(48)`+`SecurityIDSource(22)=8`, `Symbol(55)`, `SecurityExchange(207)=BVMF`.
   - `TestMessageIndicator(464)=Y` quando configurado.
   - `ClOrdID` ≤ 20 chars, alfanumérico/`-`/`_`.
2. Documentação XML.

**Saída:** `ExecutionReportFactory` produz um frame FIX 4.4 válido e parseável.

---

- [ ] T-14 — Implementar os factories opcionais `OrderCancelReject` (9) e `BusinessMessageReject` (j)

**Objetivo:** Ampliar a cobertura de tipos de mensagem para roteamento.

**Entradas:** `requirements.md` (FR-GEN-08), `.kiro/steering/b3-guidelines.md` (§10), `docs/FIX44Entrypoint.xml`

**Passos:**
1. Criar `OrderCancelRejectFactory.cs` (MsgType=9): `OrderID(37)`, `ClOrdID(11)`, `OrigClOrdID(41)`, `OrdStatus(39)`, `CxlRejResponseTo(434)`, `CxlRejReason(102)`.
2. Criar `BusinessMessageRejectFactory.cs` (MsgType=j): `RefMsgType(372)`, `BusinessRejectReason(380)`, `Text(58)`, opcional `SanityValidation(10031)`.
3. Habilitação controlada por `SimulationConfig.EnabledMessageTypes`.

**Saída:** Factories opcionais compilam; desligados por default (`EnabledMessageTypes=["8"]`).

---

- [ ] T-15 — Implementar seleção de conta e instrumento

**Objetivo:** Resolver conta e instrumento por ordem segundo a config.

**Entradas:** `requirements.md` (FR-SIM-05, FR-GEN-06, FR-GEN-07)

**Passos:**
1. Criar um resolvedor que escolhe conta da lista (`RoundRobin`/`Random`) e instrumento da lista configurada, preenchendo `OrderContext`.
2. Refletir a conta em `Account(1)` e em `Parties` (`PartyRole=3`).

**Saída:** Ordens geradas distribuem contas e instrumentos conforme a configuração.

---

- [ ] T-16 — Implementar `OrderFlowGenerator` (loop + rate limiting)

**Objetivo:** Loop de background que gera e envia frames na taxa alvo.

**Entradas:** `requirements.md` (FR-SIM-03, FR-SIM-04, FR-SIM-07, NFR-PERF-01, NFR-PERF-02), `design.md` (Seção 3.2)

**Passos:**
1. Criar `DropCopySim.Core/Generation/OrderFlowGenerator.cs`:
   - Loop em `Task` que, enquanto `Running`, lê a config vigente como snapshot imutável no topo de cada ciclo (`Volatile.Read` da referência publicada pelo controller) e respeita `RatePerSecond` (via `TokenBucketRateLimiter` ou `PeriodicTimer`). Assim, a troca ao vivo de `RatePerSecond`/`Outcome` (FR-SIM-06) vale a partir do próximo tick, sem corromper ordens em andamento.
   - Pede o próximo `OrderContext` ao `OrderLifecycleModel`, chama o factory, aplica `MsgSeqNum` e `BusinessSeqNum`, envia pela `AcceptorSession`.
   - Faz `TryWrite` amostrado de `MessageSample` no `TelemetryHub` (não bloqueante).
   - Atualiza `SimulationCounters`; ao atingir `TotalOrders` finito, encerra a rodada.
2. O envio FIX nunca espera a telemetria (NFR-PERF-02).

**Saída:** Com Start, o gerador envia frames na taxa configurada e para no limite.

---

## Fase 5 — Controle de Simulação e Injeção de Falhas

- [ ] T-17 — Implementar `SimulationController` (FSM Idle/Running/Paused)

**Objetivo:** Comandar a rodada e validar transições.

**Entradas:** `requirements.md` (FR-SIM-01, FR-SIM-02, FR-SIM-06, FR-SIM-08), `design.md` (Seção 3.4)

**Passos:**
1. Criar `DropCopySim.Core/Generation/SimulationController.cs`:
   - `Start/Pause/Resume/Stop`, validando transições; `InvalidSimulationStateException` em transição ilegal.
   - Controla o `OrderFlowGenerator` (iniciar/suspender/retomar/encerrar) e os contadores.
   - `UpdateConfig(SimulationConfig candidate)` com política **híbrida por campo**: em `Idle`/`Paused` aplica qualquer campo; em `Running` aceita delta que toque apenas campos hot (`RatePerSecond`, `Outcome`) e publica um novo snapshot imutável para o gerador. Delta que toque campo travado em `Running` é rejeitado por inteiro (lançar exceção traduzida em HTTP 409 — tudo-ou-nada).
   - Publicar a config como snapshot imutável via `Volatile.Write`/`Interlocked.Exchange` da referência, nunca mutar a instância vigente.
   - Emite `SimulationStateChanged` na telemetria a cada transição.
2. Documentação XML.

**Saída:** Transições de simulação corretas e observáveis; reconfiguração respeita a política híbrida sem aplicar estado parcial.

---

- [ ] T-18 — Implementar os comandos de injeção de falha na sessão

**Objetivo:** Drop, Force Logout e Sequence Gap.

**Entradas:** `requirements.md` (FR-FAULT-01, FR-FAULT-02, FR-FAULT-04), `.kiro/steering/b3-guidelines.md` (§13)

**Passos:**
1. Em `AcceptorSession`: `DropConnection()` (fecha socket sem Logout), `ForceLogout(string text)` (Logout com `Text`), `InjectSequenceGap(int skip)` (pula seq e responde `SequenceReset` GapFill ao `ResendRequest`).
2. Opcional (FR-FAULT-05): `InjectMalformed(kind)` com checksum inválido ou campo faltando.

**Saída:** Os comandos de falha executam sobre a sessão ativa.

---

- [ ] T-19 — Implementar o modo de resposta de Logon configurável

**Objetivo:** Suportar Logon com falha para testar o NexusRouter.

**Entradas:** `requirements.md` (FR-ACC-05, FR-FAULT-03), `.kiro/steering/b3-guidelines.md` (§13)

**Passos:**
1. Em `AcceptorSession`, honrar `AcceptorOptions.LogonMode`: `Success` → `6930=0`; `InvalidCredentials` → `6930=1`; `NotAuthorized` → `6930=2`, encerrando após responder.
2. Permitir alternar o modo em runtime via comando (consumido pelo endpoint em T-22).

**Saída:** Logon pode falhar deterministicamente com o código B3 escolhido.

---

## Fase 6 — API de Controle + Telemetria (Host)

- [ ] T-20 — Implementar `TelemetryHub` (channel + broadcast WebSocket)

**Objetivo:** Difundir estado e amostras ao front sem bloquear o envio FIX.

**Entradas:** `requirements.md` (FR-API-02, FR-API-03, NFR-PERF-02), `design.md` (Seção 3.3)

**Passos:**
1. Criar `DropCopySim.Core/Telemetry/TelemetryHub.cs`:
   - `Channel<TelemetryEvent>` bounded (`TelemetryChannelCapacity`, overflow DropOldest).
   - `TryPublish(TelemetryEvent)` não bloqueante.
   - Registro/remoção de clientes WebSocket; consumidor de background serializa JSON e faz broadcast.
   - Amostragem do feed de `MessageSample` por `MessageSampleMaxPerSecond`.
   - `CountersSnapshot` periódico (`CountersIntervalMs`).
2. Documentação XML.

**Saída:** Eventos publicados chegam a clientes WS; sob pressão, eventos são descartados, não atrasados.

---

- [ ] T-21 — Implementar `Program.cs` e wiring de DI do Host

**Objetivo:** Subir o `WebApplication` com acceptor, controller, telemetria e Serilog.

**Entradas:** `requirements.md` (NFR-CFG-01, NFR-OBS-01), `design.md` (Seções 3.3, 6)

**Passos:**
1. Criar `sim/backend/src/DropCopySim.Host/Program.cs`:
   - `WebApplication.CreateBuilder`; `UseSerilog`; bind de `DropCopySim` options.
   - Registrar `FixAcceptorService` (`IHostedService`), `SimulationController`, `OrderFlowGenerator`, factories (`IFixMessageFactory`), `TelemetryHub`.
   - Configurar CORS (`Control:AllowedOrigins`) e WebSockets.
2. Criar `appsettings.json` alinhado à Seção 6 (acceptor `localhost:9876`, HTTP `8099`, sem secrets — só nome de env var).
3. Pacotes NuGet fixados: `Serilog.AspNetCore`, sinks console/arquivo.

**Saída:** `dotnet run` sobe o acceptor e a API; logs de startup aparecem.

---

- [ ] T-22 — Implementar os endpoints de controle, estado e falha

**Objetivo:** Expor a API HTTP do SimPanel.

**Entradas:** `requirements.md` (FR-API-01, FR-API-05, FR-SIM-01, FR-FAULT-01 a FR-FAULT-04), `design.md` (Seção 3.3)

**Passos:**
1. Criar `Endpoints/StateEndpoints.cs` (`GET /v1/state`), `SimulationEndpoints.cs` (`POST /v1/sim/{start,pause,resume,stop}`, `PUT /v1/config`) e `FaultEndpoints.cs` (`POST /v1/fault/{drop,logout,logon-mode,seq-gap}`).
2. Mapear exceções de estado para HTTP 409 (inclui `PUT /v1/config` que tente alterar campo travado durante `Running` — FR-SIM-08, sem efeito parcial), validação para 400, UUID/recurso ausente para 404; corpo de erro `{ code, message }`.
3. Endpoint WebSocket `GET /v1/ws` que registra o cliente no `TelemetryHub`.

**Saída:** Todos os comandos do painel funcionam via HTTP; o WS entrega telemetria.

---

- [ ] T-23 — Implementar autenticação por token opcional

**Objetivo:** Proteger a API quando `SIM_CONTROL_TOKEN` estiver definido.

**Entradas:** `requirements.md` (FR-API-06, NFR-CFG-02)

**Passos:**
1. Criar `Auth/TokenAuth.cs`: middleware/filtro que exige `Authorization: Bearer <token>` quando a env var nomeada em `Control:AuthTokenEnvVar` tiver valor; comparação em tempo constante. WebSocket valida o mesmo token no handshake.
2. Sem token configurado → modo aberto de laboratório (documentar no README). Token nunca em log/URL/config versionada.

**Saída:** Com token definido, chamadas sem `Bearer` recebem 401; sem token, API aberta localmente.

---

## Fase 7 — Frontend SimPanel (React + Vite)

- [ ] T-24 — Inicializar o projeto Vite + React + TypeScript

**Objetivo:** Scaffold do SimPanel.

**Entradas:** `requirements.md` (FR-UI-01, NFR-MAINT-03), `design.md` (Seção 4.1)

**Passos:**
1. Criar `sim/frontend/` com Vite (template `react-ts`): `package.json`, `vite.config.ts`, `tsconfig.json`, `index.html`, `src/main.tsx`, `src/App.tsx`.
2. Configurar variáveis de ambiente do Vite: `VITE_SIM_API` (default `http://localhost:8099`) e `VITE_SIM_TOKEN` (opcional, dev).
3. Fixar versões no `package.json`.

**Saída:** `npm install && npm run dev` serve o app em `http://localhost:5173`.

---

- [ ] T-25 — Implementar a camada de API (`api/client.ts`, `api/types.ts`)

**Objetivo:** Cliente HTTP tipado espelhando o contrato do SimCore.

**Entradas:** `design.md` (Seções 3.3, 4.2), contrato dos endpoints (T-22)

**Passos:**
1. `api/types.ts`: DTOs (estado, config, eventos de telemetria) em TypeScript, espelhando os tipos do SimCore.
2. `api/client.ts`: wrapper de `fetch` com base URL de `VITE_SIM_API`, header `Authorization: Bearer` quando `VITE_SIM_TOKEN` existir, e tratamento de erro `{ code, message }`.

**Saída:** Chamadas tipadas a `/v1/state`, `/v1/sim/*`, `/v1/config`, `/v1/fault/*`.

---

- [ ] T-26 — Implementar o cliente de telemetria WebSocket

**Objetivo:** Consumir o feed em tempo real com reconexão automática.

**Entradas:** `requirements.md` (FR-API-02, FR-UI-08), `design.md` (Seção 4.3)

**Passos:**
1. `ws/telemetry.ts`: abre `/v1/ws`, processa eventos tipados, expõe estado de conectividade, reconecta com backoff simples.
2. Atualiza o store da app (`state/store.ts`) a cada evento; mantém ring buffer das últimas N `MessageSample`.

**Saída:** O painel recebe estado e amostras ao vivo e indica quedas do WS.

---

- [ ] T-27 — Implementar `SessionStatus` e `SimulationStatus`

**Objetivo:** Exibir estado da sessão FIX e da simulação.

**Entradas:** `requirements.md` (FR-UI-02, FR-UI-03)

**Passos:**
1. `components/SessionStatus.tsx`: estado, comp IDs, `MsgSeqNum`, último heartbeat.
2. `components/SimulationStatus.tsx`: estado, total enviado, taxa efetiva, contagem por `ExecType`, erros, tempo decorrido.

**Saída:** Os dois painéis refletem a telemetria em tempo real.

---

- [ ] T-28 — Implementar `SimulationControls` e `ConfigForm`

**Objetivo:** Controlar a rodada e editar a configuração.

**Entradas:** `requirements.md` (FR-UI-04, FR-UI-05, FR-SIM-06, FR-SIM-08), `design.md` (Seção 4.4)

**Passos:**
1. `components/SimulationControls.tsx`: botões Start/Pause/Resume/Stop com `disabled` derivado do estado (tabela da Seção 4.4).
2. `components/ConfigForm.tsx`: quantidade (ou ilimitado), frequência, contas, instrumentos, seleção de conta, proporção de desfechos, `TestMessageIndicator`. Política híbrida na UI: em `Running`, só os campos hot (`RatePerSecond`, `Outcome`) ficam editáveis/submetíveis e o submit manda apenas o delta hot; campos travados ficam desabilitados com dica "pause para alterar". Em `Idle`/`Paused`, todos os campos são editáveis. Tratar 409 do servidor como rede de segurança.

**Saída:** Operar a simulação e salvar config a partir da UI, respeitando a política híbrida por estado.

---

- [ ] T-29 — Implementar `FaultControls`

**Objetivo:** Injetar falhas pela UI.

**Entradas:** `requirements.md` (FR-UI-06, FR-FAULT-01 a FR-FAULT-04)

**Passos:**
1. `components/FaultControls.tsx`: botão Drop Connection, Force Logout (com escolha de texto/cenário: "MsgSeqNum too low", "Password will expire", custom), seletor do modo de resposta ao Logon (Success/InvalidCredentials/NotAuthorized) e botão de Sequence Gap.

**Saída:** Os comandos de falha são disparados pela UI e refletidos na telemetria.

---

- [ ] T-30 — Implementar `MessageFeed` e `ConnectivityBadge`

**Objetivo:** Feed ao vivo das últimas mensagens e indicador de conectividade.

**Entradas:** `requirements.md` (FR-UI-07, FR-UI-08)

**Passos:**
1. `components/MessageFeed.tsx`: tabela das últimas N amostras (`MsgType`, `ClOrdID`, `OrderID`, `ExecType`, `OrdStatus`, `Side`, instrumento, `BusinessSeqNum`).
2. `components/ConnectivityBadge.tsx`: indica API/WS up/down.

**Saída:** O operador acompanha o fluxo amostrado e o estado da conexão com o SimCore.

---

## Fase 8 — Testes e Verificação de Fidelidade

- [ ] T-31 — Testes unitários: `B3FixMessageWriter` e framing

**Objetivo:** Garantir `BodyLength`/`CheckSum` corretos e framing confiável.

**Entradas:** T-04, T-05, T-06, `.kiro/steering/coding-standards.md` (§7)

**Passos:**
1. `B3FixMessageWriterTests`: `BodyLength` e `CheckSum` conferem contra um cálculo de referência; `TransactTime` em formato UTC correto.
2. `Fix44FrameScannerTests`: isola frames completos, inclusive em segmentos divididos.
3. xUnit + FluentAssertions; nomenclatura `{Metodo}_{Cenario}_{Esperado}`.

**Saída:** Testes verdes cobrindo wire e checksum.

---

- [ ] T-32 — Testes unitários: factories e ciclo de vida da ordem

**Objetivo:** Validar os `ExecutionReport` e a coerência do ciclo de vida.

**Entradas:** T-12, T-13, `.kiro/steering/b3-guidelines.md` (§6.6)

**Passos:**
1. `ExecutionReportFactoryTests`: campos obrigatórios presentes; `ClOrdID` ≤ 20 e charset válido; instrumento com `SecurityIDSource=8`; `Parties` com `PartyRole=4`.
2. `OrderLifecycleModelTests`: sequências coerentes (`OrdStatus` × `ExecType` × `LeavesQty`); proporção de desfechos respeita `OutcomeMix` em grande amostra.

**Saída:** Fábricas e modelo de ciclo de vida verificados.

---

- [ ] T-33 — Testes unitários: FSMs de sessão e de simulação

**Objetivo:** Verificar transições válidas/ilegais.

**Entradas:** T-07, T-17

**Passos:**
1. `FixSessionFsmTests`: transições válidas passam; ilegais lançam `InvalidSessionStateException`.
2. `SimulationControllerTests`: Start/Pause/Resume/Stop corretos; Pause em `Idle` rejeitado; `UpdateConfig` em `Running` aceita delta só-hot (`RatePerSecond`/`Outcome`) e rejeita (409, sem efeito parcial) delta que toque campo travado; em `Idle`/`Paused` aceita qualquer campo.

**Saída:** Máquinas de estado cobertas por testes.

---

- [ ] T-34 — Teste de integração: round-trip com cliente FIX mínimo próprio

**Objetivo:** Provar a fidelidade do protocolo sem referência ao NexusRouter (isolamento total — `design.md` Seção 5.1).

**Entradas:** `requirements.md` (NFR-FID-01, restrição de isolamento), `design.md` (Seções 5.1, 9)

**Passos:**
1. Criar `sim/backend/tests/DropCopySim.Integration.Tests/MinimalFixClient.cs`: um cliente FIX 4.4 mínimo de teste (TCP + envio de Logon + leitura/parse de frames + checagem de `CheckSum`). **Sem** `ProjectReference` ao NexusRouter — código próprio do projeto de teste.
2. `AcceptorRoundTripTests`: subir o `FixAcceptorService` em porta efêmera; conectar o `MinimalFixClient` (`SenderCompId=CLIENT1`, `TargetCompId=ENTRYPOINT-FIRM-0001`).
3. Confirmar Logon ACK e envio/parse de N `ExecutionReport` com `BodyLength`/`CheckSum` válidos e campos obrigatórios presentes.
4. Drop Connection → verificar que o cliente detecta a queda e consegue reconectar ao acceptor.
5. `LogonMode=InvalidCredentials` → verificar que o acceptor responde `ApplicationLogonRspCode(6930)=1` e encerra.
6. Documentar no `sim/README.md` (ver NFR-MAINT-04) o round-trip **manual** contra o NexusRouter real como verificação de fidelidade de ponta a ponta (não automatizado aqui, para preservar o isolamento de código).

**Saída:** O teste automatizado demonstra, sem depender do NexusRouter, que o acceptor fala FIX 4.4 fiel à B3 e trata as falhas injetadas; o round-trip real fica documentado no README.
