# Requisitos — Correção de dois defeitos no DropCopy simulator

## Summary

O DropCopy simulator (pasta `sim/`, isolado do NexusRouter) apresenta dois defeitos na branch
`chore/disable-websocket-dispatcher`. O **Defeito A** é bloqueante: com a simulação em `Running`,
o `OrderFlowGenerator` roda seu loop de background mas não emite nenhuma mensagem — `totalSent`
permanece 0, taxa 0.0 msg/s, nenhum `ExecutionReport` (MsgType=8) é entregue à sessão do acceptor e
nenhum log/exceção aparece. O **Defeito B** é ruído de log: ao fechar/reabrir o WebSocket de
telemetria (comum com React StrictMode), `PumpUntilClosedAsync` tenta `CloseAsync` num socket já
abortado e lança `System.Net.WebSockets.WebSocketException` ("The WebSocket is in an invalid state
(Aborted) for this operation"), poluindo o log a cada reconexão sem impedir a telemetria.

O trabalho se restringe à pasta `sim/`. Não deve haver commit, push ou PR — as mudanças ficam no
working tree para revisão. Trabalhar na branch atual, sem criar worktree.

### Causa raiz investigada (contexto para o design)

- **Defeito A:** `OrderFlowGenerator.SendNextAsync` chama `ActiveSession()`, que retorna a sessão
  publicada via `AttachSession(...)` somente quando ela está em `AcceptorSessionState.Active`. Quando
  `ActiveSession()` retorna `null`, `SendNextAsync` retorna `true` (no-op silencioso) e o loop
  continua rodando sem emitir — sem exceção e sem log. **`OrderFlowGenerator.AttachSession` nunca é
  chamado em lugar nenhum do host/core.** O `FixAcceptorService` cria a sessão e a expõe em
  `ActiveSession`, além de a `AcceptorSession` oferecer o evento `StateChanged`, mas não existe
  nenhuma ponte (DI/wiring) ligando a sessão aceita ao gerador. Logo o gerador nunca enxerga uma
  sessão ativa e o loop fica inerte. Isso responde ao ponto aberto do planner: **o gerador exige uma
  sessão FIX ativa para emitir** (é por design, conforme o `ActiveSession()` guard); o defeito é a
  ausência do wiring que entrega a sessão ao gerador, não a necessidade da sessão em si.
- **Defeito B:** em `TelemetryEndpoints.PumpUntilClosedAsync`, o `CloseAsync` executado após receber
  um frame de Close (ou quando o socket já foi abortado pelo cliente/hub) lança `WebSocketException`
  quando o socket está no estado `Aborted`. O `catch (WebSocketException)` externo do endpoint hoje
  registra em Debug **com a exceção**, o que ainda emite stack trace quando Debug está habilitado. O
  fechamento abortado precisa ser tratado graciosamente, sem exceção não tratada nem stack trace.

### Avaliação das edições WIP já presentes na branch (commit `49b39aa`)

As edições parciais em andamento **não** endereçam nenhum dos dois defeitos e devem ser preservadas
(não atrapalham), mas são insuficientes:

- `FixAcceptorService.cs`: adiciona guard de `OperationCanceledException` no `StopAsync` para o prazo
  de shutdown — não tem relação com o Defeito A.
- `TelemetryHub.cs`: serializa enums como nomes no feed WebSocket — melhoria de contrato de
  telemetria, não relacionada aos defeitos.
- `App.tsx`, `store.ts`, `telemetry.ts` (frontend): hidratam o store via `GET /v1/state` na carga e a
  cada reconexão do feed — melhoria de UX, não corrige o Defeito A nem o B.

A correção do Defeito A **não existe** na WIP e deve ser implementada (o wiring da sessão ao gerador).
A correção do Defeito B **não existe** na WIP e deve ser implementada em `TelemetryEndpoints.cs`.

## Functional Requirements

### Defeito A — geração de ordens não emite

- FR-A-01: Enquanto a simulação está em `Running` e existe uma sessão FIX ativa no acceptor, o
  `OrderFlowGenerator` deve emitir mensagens de aplicação à sessão na taxa configurada
  (`RatePerSecond`), respeitando `TotalOrders` (0 = ilimitado) e `EnabledMessageTypes`.
- FR-A-02: A sessão aceita pelo `FixAcceptorService` deve ser entregue ao `OrderFlowGenerator` (via
  `AttachSession`) quando ela se torna `Active`, e removida (via `AttachSession(null)`) quando a
  sessão se desconecta/é descartada, para que `ActiveSession()` reflita a sessão real.
- FR-A-03: A emissão deve funcionar tanto para uma sessão FIX já conectada antes do `POST
  /v1/sim/start` quanto para o fluxo em que o `start` ocorre e a sessão conecta depois (o loop retoma
  a emissão assim que uma sessão ativa estiver disponível).
- FR-A-04: Após `POST /v1/sim/start`, `GET /v1/state` deve refletir `totalSent` crescente e taxa > 0
  enquanto houver sessão ativa; os contadores por `ExecType` devem evoluir conforme as mensagens
  emitidas.
- FR-A-05: O comportamento de no-op silencioso quando não há sessão ativa deve permanecer (sem erro):
  o loop não emite enquanto não houver sessão `Active`, mas retoma automaticamente quando ela surgir.

### Defeito B — ruído de log no WebSocket de telemetria

- FR-B-01: O fechamento do WebSocket de telemetria num socket em estado `Aborted` (ou qualquer estado
  que não permita `CloseAsync`) deve ser tratado graciosamente, sem propagar `WebSocketException` não
  tratada e sem emitir stack trace no log.
- FR-B-02: Um ciclo de fechar/reabrir a conexão pelo cliente (SimPanel sob React StrictMode) não deve
  gerar exceção com stack trace a cada reconexão; no máximo uma entrada de log informativa/trace sem
  stack trace é aceitável.
- FR-B-03: A telemetria deve continuar funcionando normalmente para clientes conectados; a limpeza da
  conexão (remoção do client do hub) deve ocorrer em todos os caminhos de saída.

## Non-Functional Requirements

- NFR-01: As correções ficam contidas na pasta `sim/`; nenhum arquivo do NexusRouter é alterado.
- NFR-02: As correções seguem `.kiro/steering/coding-standards.md` (estilo C#) e, se o frontend for
  tocado, mantêm o typecheck limpo.
- NFR-03: A correção do Defeito A não deve introduzir bloqueio no caminho de envio FIX nem regressões
  de concorrência (o acesso à sessão no gerador já usa `Volatile`; o wiring deve respeitar isso).
- NFR-04: Nenhum segredo é adicionado, lido ou commitado; nenhuma config de produção é alterada.

## Acceptance Criteria

1. Subindo o simulador (`dotnet run --project sim/backend/src/DropCopySim.Host`), conectando um
   cliente FIX no acceptor (porta 9876) e chamando `POST http://127.0.0.1:8099/v1/sim/start`, o
   `GET http://127.0.0.1:8099/v1/state` passa a mostrar `totalSent` crescente e taxa > 0.
2. Com uma sessão FIX ativa, `ExecutionReports` (MsgType=8) são de fato gerados e entregues à sessão
   do acceptor (confirmável pelo cliente FIX e pelos contadores por `ExecType`).
3. Sem sessão FIX ativa, `start` responde 204, o estado fica `Running` e nenhuma exceção é lançada; ao
   conectar uma sessão, a emissão começa automaticamente sem novo `start`.
4. Fechar e reabrir o WebSocket de telemetria (`GET /v1/ws`) a partir do cliente não gera
   `WebSocketException` não tratada nem stack trace no log do host, em nenhuma reconexão.
5. A telemetria continua entregando eventos a um cliente conectado após o tratamento gracioso do
   fechamento abortado.
6. `dotnet build sim/backend/DropCopySimulator.slnx` compila sem erros.
7. `dotnet test sim/backend/DropCopySimulator.slnx` passa com todos os testes existentes verdes.
8. Se qualquer arquivo em `sim/frontend` for alterado, `cd sim/frontend && npm run typecheck` passa
   sem erros.
9. Nenhum commit, push ou PR é criado; as mudanças permanecem no working tree da branch
   `chore/disable-websocket-dispatcher`.

## Out of Scope

- Qualquer alteração no NexusRouter (fora de `sim/`).
- Reescrita das melhorias de frontend já presentes na WIP (hidratação via `GET /v1/state`) além do
  necessário para os dois defeitos; elas podem ser mantidas como estão.
- Mudanças no contrato da API de controle, nos tipos de mensagem FIX suportados ou na política de
  reconfiguração do `SimulationController`.
- Rotação de segredos, TLS do acceptor, mTLS e demais itens marcados como TODO no baseline de
  segurança.
- Commit/push/PR (explicitamente proibido pelo usuário neste fluxo).

## Assumptions

- A1: O gerador **exige** uma sessão FIX `Active` para emitir (comportamento por design via
  `ActiveSession()` guard); o Defeito A é a ausência do wiring que entrega a sessão ao gerador, e a
  correção é estabelecer esse wiring — não remover o guard de sessão.
- A2: O wiring sessão→gerador será feito no host/core (ex.: assinando o evento `StateChanged` da
  `AcceptorSession` ou no ciclo de aceitação do `FixAcceptorService`), chamando `AttachSession` na
  transição para `Active` e `AttachSession(null)` na desconexão/descarte. A escolha concreta do ponto
  de integração fica para o design.
- A3: As edições WIP (shutdown guard, serialização de enums, hidratação do frontend) devem ser
  preservadas; o design decide apenas se precisam de ajuste para coexistir com as correções.
- A4: O critério "nenhum stack trace" admite uma entrada de log de nível baixo (Debug/Trace) sem a
  exceção anexada para o fechamento abortado; o essencial é não haver exceção não tratada nem stack
  trace.
