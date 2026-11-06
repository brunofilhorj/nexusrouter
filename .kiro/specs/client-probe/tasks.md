# Client Probe — Plano de Tarefas Incremental

> **Status do Documento:** Rascunho v1.0
> **Classificação:** Especificação de Engenharia Interna
> **Metodologia:** Spec-Driven Development (SDD) — Execução Sequencial de Tarefas

---

## Modelo de Execução

As tarefas são executadas **uma por vez**, em ordem. Cada tarefa é autocontida, tem entradas/saídas explícitas e produz um artefato verificável. Não execute duas tarefas em paralelo no mesmo working tree.

Ao concluir uma tarefa, marque `- [x]`. As entradas referenciam `requirements.md` e `design.md` desta spec. Tarefas que escrevem C# também leem `.kiro/steering/coding-standards.md`. Tarefas de FIX leem `.kiro/steering/b3-guidelines.md` §15 e `docs/FIX44Entrypoint.xml` só como referência de framing. Não há `ProjectReference` ao NexusRouter nem ao simulador.

**Visão Geral das Fases:**

| Fase | Área de Foco | Tarefas |
|------|-------------|---------|
| Fase 1 | Mover o simulador para `utils/sim/` | T-01 |
| Fase 2 | Um csproj com ClientApp em `:8100` | T-02 → T-03 |
| Fase 3 | UI estática (topbar, menu, quatro telas) | T-04 → T-05 |
| Fase 4 | Contrato, API e telemetria vazia | T-06 |
| Fase 5 | Adapters Kafka e Rabbit | T-07 → T-08 |
| Fase 6 | Iniciador FIX | T-09 → T-10 |
| Fase 7 | Tela WebSocket no contrato da §3.3.1 | T-11 → T-12 |

A aplicação é **um** csproj. `ClientApp` mora dentro dele. O projeto de teste é irmão, na mesma solução, e não referencia o NexusRouter.

---

## Fase 1 — Mover o Simulador

- [x] T-01 — `git mv sim utils/sim` e acertar os caminhos do layout

**Objetivo:** Simulador e client passam a ser utilitários irmãos. O histórico segue o arquivo.

**Entradas:** `requirements.md` (FR-ISO-05, FR-ISO-06), `design.md` (Seção 6.1)

**Passos:**
1. `git mv sim utils/sim`. Não reescrever relatórios em `.agents/pipeline/`.
2. Atualizar o link e a frase de isolamento no `README.md` da raiz para `utils/sim/`.
3. Atualizar `utils/sim/README.md`: comandos `dotnet run` e `npm run dev` com o prefixo `utils/sim/`.
4. Atualizar `.cursor/rules/spec-task.mdc`: a solução do `dropcopy-simulator` passa a ser `utils/sim/backend/DropCopySimulator.slnx`.
5. Atualizar `.kiro/specs/dropcopy-simulator/design.md` §5 (árvore, §5.1 e §5.2) para `utils/sim/`. A spec é a fonte do layout.
6. Atualizar em `.kiro/specs/dropcopy-simulator/requirements.md` só os trechos que definem o caminho: NFR-MAINT-04, NFR-MAINT-05 e a seção 4 (localização e isolamento). Não reabrir outros requisitos.
7. Não adicionar `utils/` à `NexusRouter.slnx`. Não alterar `docker-compose.yml` nem `.github/workflows/`.

**Saída:** `dotnet test utils/sim/backend/DropCopySimulator.slnx` verde. `npm run typecheck` em `utils/sim/frontend` verde. `dotnet build NexusRouter.slnx` não enxerga o simulador.

---

## Fase 2 — Csproj Único e Mesma Origem

- [x] T-02 — Criar a solução isolada e o csproj com `ClientApp`

**Objetivo:** Um projeto de aplicação publicável, com barreira de herança e a pasta do Vite dentro do csproj.

**Entradas:** `requirements.md` (FR-ISO-01 a FR-ISO-04, FR-HOST-04, NFR-MAINT-01), `design.md` (Seções 6 e 6.1), `.kiro/steering/coding-standards.md`

**Passos:**
1. Criar `utils/client/ClientProbe.slnx` com um projeto de aplicação. Não adicionar à `NexusRouter.slnx`.
2. Criar `utils/client/Directory.Build.props` (`net10.0`, nullable enable, implicit usings, warnings as errors) e `utils/client/Directory.Packages.props` com `Confluent.Kafka` e `RabbitMQ.Client` pinados. Sem herdar o CPM da raiz.
3. Criar `utils/client/src/ClientProbe/ClientProbe.csproj` (`Microsoft.NET.Sdk.Web`). A `ClientApp` não é um segundo projeto.
4. Criar `ClientApp/` com `package.json` (React, Vite, TypeScript pinados; scripts `dev`, `build`, `typecheck`), `index.html`, `vite.config.ts` e um `src/main.tsx` mínimo.
5. Target de build: `npm run build` em `ClientApp` e cópia de `dist/` para `wwwroot/`. `wwwroot` pode começar vazio.
6. Criar `utils/client/tests/ClientProbe.Tests` (xunit) na solução. Sem `ProjectReference` ao NexusRouter ou ao simulador. O projeto de teste referencia só `ClientProbe`.

**Saída:** `dotnet build utils/client/ClientProbe.slnx -warnaserror` conclui com zero avisos. `npm run typecheck` em `ClientApp` conclui sem erro.

---

- [x] T-03 — Servir a página, `/v1/state` e `/v1/ws` em `:8100`

**Objetivo:** Uma origem para a SPA, a API e a telemetria. Sem eventos ainda.

**Entradas:** `requirements.md` (FR-HOST-01, FR-HOST-02, FR-HOST-03, FR-API-01, FR-API-04), `design.md` (Seções 4 e 7)

**Passos:**
1. `Program.cs`: `UseStaticFiles`, fallback para `index.html`, sem CORS.
2. `GET /v1/state` devolve JSON com os três transportes em `Disconnected` e contadores zerados. `GET /v1/ws` aceita o upgrade e não emite evento.
3. `appsettings.json` com `Urls` em `http://localhost:8100` e os defaults da Seção 7. Nenhum segredo no arquivo.
4. Em `Development`, subir o Vite (`npm run dev`) e encaminhar ao Vite tudo que não é `/v1/*`. O browser permanece em `:8100`.
5. Mapear `/v1` antes do fallback da SPA.

**Saída:** `dotnet run` abre a página e `/v1/state` na mesma origem. O modo dev recarrega a UI sem segunda URL.

---

## Fase 3 — UI Estática

- [x] T-04 — Topbar, tema, menu e quatro telas

**Objetivo:** A casca do ClientPanel, no visual do SimPanel, sem adapter ao vivo.

**Entradas:** `requirements.md` (FR-UI-01 a FR-UI-05, FR-UI-10), `design.md` (Seções 5.1 e 5.2)

**Passos:**
1. Copiar de forma enxuta, para `ClientApp/src/theme/`, os tokens e o `useTheme` do SimPanel (`utils/sim/frontend` depois de T-01). Escuro como padrão. Sem pacote compartilhado.
2. `ThemeToggle` e `ConnectivityBadge` (API e `/v1/ws` na mesma origem; estado real entra na T-06).
3. `DispatcherNav` com Kafka, Rabbit, FIX e WebSocket. Hash `#/kafka`, `#/rabbit`, `#/fix`, `#/ws`. Sem `react-router`.
4. `App.tsx`: topbar (`ClientPanel`, subtítulo, badge, engrenagem, tema) + menu + área da tela.
5. Quatro telas estáticas com `ConnectionCard`, `StatsRow` e `EventTable` vazia. Textos de endpoint iguais a FR-UI-10. A tela WebSocket diz que o acceptor não está no egress.

**Saída:** As quatro telas renderizam em `:8100`. O hash sobrevive ao refresh. `npm run typecheck` verde.

---

- [x] T-05 — Modal, store e cliente HTTP relativo

**Objetivo:** Configuração por aba e o contrato de fetch, ainda sem ligar adapter.

**Entradas:** `requirements.md` (FR-UI-06, FR-UI-07, FR-UI-08, NFR-SEC-01), `design.md` (Seções 5.3 e 5.4)

**Passos:**
1. `ConfigModal` com abas Kafka, Rabbit, FIX e WebSocket. Host, porta e identificadores podem ir para `localStorage`. Senha e token ficam só em memória e somem no unload.
2. `api/client.ts` com `fetch` relativo: `/v1/state`, `/v1/connect`, `/v1/disconnect`.
3. `state/store.ts`: uma tela por vez e um buffer de eventos por transporte (capacidade 500).
4. `ws/telemetry.ts`: conecta em `/v1/ws` com backoff. Sem evento, o badge reflete só o socket aberto ou fechado.

**Saída:** O modal não cobre a navegação. Nenhum segredo aparece em `localStorage`. Typecheck verde.

---

## Fase 4 — Contrato e Telemetria

- [x] T-06 — `IProbeAdapter`, `ProbeController`, `TelemetryHub` e os POST

**Objetivo:** Ligar e desligar um transporte, com broadcast que não bloqueia.

**Entradas:** `requirements.md` (FR-ADP, FR-API-01 a FR-API-06), `design.md` (Seções 3.1, 3.5, 3.6 e 4), `.kiro/steering/coding-standards.md`

**Passos:**
1. Tipos `ProbeTransport`, `ProbeState`, `ProbeProfile`, `ProbeCounters`, `ProbeEvent`, `IProbeAdapter`.
2. `ProbeController`: um adapter por transporte. Connect de um não derruba os outros. Stop do processo fecha os três. Segundo connect no mesmo transporte substitui a conexão anterior.
3. `TelemetryHub`: channel bounded, `TryWrite` não bloqueia, overflow incrementa `dropped`, amostragem acima de `SampleAbovePerSecond` incrementa `sampled`. Broadcast JSON em `/v1/ws`.
4. `POST /v1/connect` e `POST /v1/disconnect`. Erro de validação 400, conflito 409, corpo `{ code, message }`. `GET /v1/state` passa a ler o controller.
5. Adapters concretos ainda podem ser stubs `Faulted` com erro "não implementado" — T-07 em diante os substitui. O extrator de campos curtos (tags 35, 11, 55, 54, 38, 39 e `rawPreview` truncado) entra aqui, porque os três adapters o usam.
6. Documentação XML nos tipos públicos.

**Saída:** `/v1/state` reflete estado e contadores. Um teste do projeto irmão cobre a amostragem e o descarte sem bloquear o produtor.

---

## Fase 5 — Kafka e Rabbit

- [ ] T-07 — `KafkaConsumerAdapter`

**Objetivo:** Consumir o frame FIX cru publicado pelo `KafkaMessageDispatcher`.

**Entradas:** `requirements.md` (FR-KAF), `design.md` (Seção 3.2)

**Passos:**
1. Consumer `Confluent.Kafka` com bootstrap, tópico, grupo e `AutoOffsetReset` do perfil. Default: `127.0.0.1:19092`, `fix.ordexec.raw`, grupo `probe-local`, `latest`.
2. O valor é ASCII FIX. Chave ignorada. Decode falho incrementa `parseFailures` e o loop continua.
3. Cada evento passa pelo extrator e pelo `TelemetryHub`. Estado `Live` depois do subscribe; `Faulted` com `LastError` se o broker recusar.
4. Disconnect cancela o loop e fecha o consumer.

**Saída:** Contra o compose e um NexusRouter com rota para `kafka-fix-raw`, o frame cru aparece na tabela do menu Kafka.

---

- [ ] T-08 — `RabbitConsumerAdapter`

**Objetivo:** Fila exclusiva ligada à exchange em que o router publica.

**Entradas:** `requirements.md` (FR-RAB, NFR-SEC-03), `design.md` (Seção 3.3)

**Passos:**
1. Conectar com `RabbitMQ.Client`. Credencial default: `RABBITMQ_DEFAULT_USER` e `RABBITMQ_DEFAULT_PASS`. Senha vinda do modal fica só na memória da conexão.
2. Declarar a exchange do perfil (`topic`, durável) e uma fila exclusiva auto-delete. Bind na routing key (`fix.raw` no default).
3. Body ASCII FIX, mesmo extrator e a mesma telemetria da T-07.
4. Disconnect fecha canal e conexão.

**Saída:** O frame cru aparece na tabela do menu Rabbit. O README (T-12) vai registrar que o probe declara a exchange; o comportamento já fica coberto aqui.

---

## Fase 6 — Iniciador FIX

- [ ] T-09 — `FixFrameScanner` e `FixSessionWriter`

**Objetivo:** Recortar e escrever frames FIX 4.4 sem importar o simulador nem o NexusRouter.

**Entradas:** `requirements.md` (FR-FIX-04), `design.md` (Seção 3.4), `.kiro/steering/b3-guidelines.md` §15

**Passos:**
1. `FixFrameScanner`: de um buffer, localiza `8=FIX.4.4\x01` até `10=XXX\x01`, inclusive quando o frame chega em partes.
2. `FixSessionWriter`: Logon (`35=A`), Heartbeat (`35=0`) e Logout (`35=5`) com `BodyLength` e `CheckSum` corretos. `MsgSeqNum` é parâmetro; não há store.
3. Testes no projeto irmão para frame partido, checksum e as três mensagens de sessão.

**Saída:** `dotnet test utils/client/ClientProbe.slnx` verde, sem referência ao NexusRouter.

---

- [ ] T-10 — `FixInitiatorAdapter`

**Objetivo:** Sessão de prova contra `fix-acceptor-01`.

**Entradas:** `requirements.md` (FR-FIX-01, FR-FIX-02, FR-FIX-03, FR-FIX-05, FR-UI-09), `design.md` (Seção 3.4)

**Passos:**
1. TCP para o host e porta do perfil (default `127.0.0.1:6001`). Logon com `SenderCompID` do probe e `TargetCompID` `NEXUSROUTER`. `HeartBtInt` default 20.
2. Estado `Live` após o Logon de volta. Heartbeat no intervalo. `DisconnectAsync` envia Logout e fecha.
3. `MsgSeqNum` só na sessão de prova. O snapshot expõe os CompIDs e o número para o `FixScreen`.
4. Frames de aplicação entram na telemetria. Heartbeat recebido não lotar a tabela; mantém a sessão viva.
5. Queda de TCP vai a `Faulted` com `LastError`.

**Saída:** Sessão `Live` com `MsgSeqNum` subindo. Disconnect manda Logout. Execuções do router aparecem na tabela FIX.

---

## Fase 7 — WebSocket de Egress na Página

- [ ] T-11 — `NexusSocket` e a tela WebSocket

**Objetivo:** Cliente do contrato `nexusrouter.v1` na página. Conexão real só quando o acceptor existir.

**Entradas:** `requirements.md` (FR-WS, FR-ADP-04, FR-UI-09), `design.md` (Seção 5.5), `nexusrouter/design.md` §3.3.1 (Logon, `exec`, `gap`, `reject`)

**Passos:**
1. `ws/nexusSocket.ts`: upgrade com `Sec-WebSocket-Protocol: nexusrouter.v1`. Primeiro frame `{ type: logon, clientId, token, encoding: json, replay: false }`. Token só em memória.
2. Tratar `logonAck`, `exec`, `gap`, `heartbeat` e `reject`. `exec` e `gap` entram no buffer `ws` da mesma tabela. Não passar pelo `ProbeController` nem por `/v1/connect`.
3. Sem URL configurada, a tela explica que o acceptor não está no egress (`nexusrouter` T-49 a T-53). Com host e porta, o botão conecta.
4. O badge dessa tela é o do socket direto. O badge da topbar continua sendo API e `/v1/ws`.

**Saída:** O menu mostra o item. A tela explica o bloqueio enquanto T-49 estiver aberta. O typecheck passa. Quando T-49 estiver fechada, `logonAck` e `exec` JSON caem na mesma tabela, desde que `AllowedOrigins` inclua `http://localhost:8100`.

---

- [ ] T-12 — `utils/client/README.md`

**Objetivo:** O operador sobe a prova sem ler a spec.

**Entradas:** `requirements.md` (NFR-MAINT-02, FR-RAB-03, FR-WS-04), `design.md` (Seção 6.2)

**Passos:**
1. Pré-requisitos (.NET 10 SDK, Node 20+), `dotnet run` em `:8100`, modo dev na mesma origem.
2. Fluxo com compose, `utils/sim` e NexusRouter.
3. Dizer com todas as letras que o primeiro connect Rabbit declara a exchange `fix.orders` (topic, durável) e uma fila exclusiva do probe.
4. Dizer que o WebSocket de egress depende de T-49, e que o operador então inclui `http://localhost:8100` em `AllowedOrigins` e um `clientId` apontando para o `DispatcherId`. O probe não edita `src/` do router.
5. Comando dos testes: `dotnet test utils/client/ClientProbe.slnx`. Typecheck: `npm run typecheck` em `ClientApp`.

**Saída:** O README cobre os seis pontos da Seção 6.2. Nenhum segredo no arquivo.

---

## Grafo de Dependências

```
T-01 (utils/sim)
  └── T-02 → T-03 → T-04 → T-05 → T-06
                                    ├── T-07 (Kafka)
                                    ├── T-08 (Rabbit)
                                    ├── T-09 → T-10 (FIX)
                                    └── T-11 (WebSocket na página)
                                          └── T-12 (README; pode citar T-07..T-11)
```

T-07, T-08 e T-09 dependem de T-06 e não dependem entre si. Executar uma por vez no mesmo working tree. T-11 não espera T-49 do `nexusrouter`; a verificação ao vivo do acceptor espera.
