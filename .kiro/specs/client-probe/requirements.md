# Client Probe — Requisitos Funcionais e Não Funcionais

> **Status do Documento:** Rascunho v1.0
> **Classificação:** Especificação de Engenharia Interna
> **Domínio:** Ferramenta de laboratório — prova de saída dos dispatchers do NexusRouter
> **Referências:** `nexusrouter/design.md` §3.3 e §3.3.1; `src/NexusRouter.Host/appsettings.json` (defaults de egress); `dropcopy-simulator` (isolamento e linguagem visual do SimPanel)

---

## 1. Visão Geral do Sistema

O **Client Probe** é uma ferramenta de laboratório que fica do lado da **saída** do NexusRouter. O simulador DropCopy continua do lado da entrada. O probe consome o que os dispatchers publicam e mostra o fluxo numa página.

Um único projeto .NET é o executável. Kafka, RabbitMQ e FIX não abrem no browser: esses clientes rodam no processo ASP.NET. A SPA React + Vite mora em `ClientApp`, dentro do mesmo csproj. O Kestrel entrega página, API e telemetria em `http://localhost:8100`.

O dispatcher WebSocket do NexusRouter é o único canal que a página abre direto. Esse acceptor está desenhado em `nexusrouter/design.md` §3.3.1 e ainda não existe no código (`nexusrouter` T-49 a T-53 abertas). Até lá a tela existe e explica o bloqueio.

```
┌─────────────────────────────────────────────┐
│  Browser  http://localhost:8100             │
│  ClientPanel (React, servido pelo Kestrel)  │
│    /v1/state  /v1/connect  /v1/disconnect   │
│    /v1/ws  telemetria (mesma origem)        │
│    ws direto ──► NexusRouter WebSocket      │
│                  (só quando T-49 existir)   │
└──────────────┬──────────────────────────────┘
               │ mesmo processo
┌──────────────▼──────────────────────────────┐
│  ClientProbe (.NET)                         │
│   Kafka consumer │ Rabbit consumer │ FIX    │
│                  │                 │ initiator
└────────┬─────────┴────────┬────────┴────────┘
         │                  │            │
         ▼                  ▼            ▼
   Kafka :19092      Rabbit :5672   FIX acceptor
   fix.ordexec.raw   fix.orders /   127.0.0.1:6001
                     fix.raw        (NexusRouter)
```

O fluxo de laboratório é: `docker compose` sobe Kafka e Rabbit; o simulador gera DropCopy; o NexusRouter roteia; o ClientProbe consome a saída. O probe não substitui o SimPanel e não entra em `NexusRouter.slnx`.

---

## 2. Requisitos Funcionais

### 2.1 Isolamento e forma do executável (FR-ISO)

| ID | Requisito |
|----|-----------|
| FR-ISO-01 | O ClientProbe DEVE ser uma aplicação autônoma sob `utils/client/`, com solução própria (`ClientProbe.slnx`) e **um** csproj de aplicação. A `ClientApp` NÃO é um segundo projeto da solução. |
| FR-ISO-02 | O probe NÃO DEVE referenciar projetos do NexusRouter nem do DropCopy. O scanner FIX e o iniciador são cópia enxuta local, no estilo do `MinimalFixClient` do simulador. |
| FR-ISO-03 | `utils/client/` DEVE ter `Directory.Build.props` e `Directory.Packages.props` próprios, para não herdar TFM, warnings nem CPM da raiz. |
| FR-ISO-04 | Excluir `utils/client/` NÃO DEVE alterar `dotnet build NexusRouter.slnx` nem os workflows em `.github/workflows/`. O probe NÃO entra na `NexusRouter.slnx` e NÃO altera `docker-compose.yml`. |
| FR-ISO-05 | O simulador DropCopy DEVE passar a viver em `utils/sim/` (irmão do client), por `git mv` de `sim/`, preservando o histórico. Relatórios antigos em `.agents/pipeline/` NÃO são reescritos. |
| FR-ISO-06 | Depois do move, os caminhos que definem o layout do simulador DEVEM apontar para `utils/sim/`: `README.md` da raiz, `utils/sim/README.md`, `.cursor/rules/spec-task.mdc`, `.kiro/specs/dropcopy-simulator/design.md` §5 e os trechos de localização em `dropcopy-simulator/requirements.md` (NFR-MAINT-04, NFR-MAINT-05 e a seção 4). |

### 2.2 Host, página e mesma origem (FR-HOST)

| ID | Requisito |
|----|-----------|
| FR-HOST-01 | `dotnet run` DEVE subir só o Kestrel em `http://localhost:8100`, configurável. Página, API e telemetria saem dessa origem. NÃO há CORS e NÃO há URL base de API no browser. |
| FR-HOST-02 | O build de publicação DEVE rodar `npm run build` em `ClientApp` e copiar `dist/` para `wwwroot`. `UseStaticFiles` e o fallback para `index.html` entregam a SPA. |
| FR-HOST-03 | Em desenvolvimento, o mesmo `dotnet run` DEVE subir o Vite e o Kestrel DEVE encaminhar os assets da página para ele. O browser permanece em `:8100`, com hot reload. `/v1/*` continua no Kestrel. |
| FR-HOST-04 | `ClientApp/package.json` DEVE expor os scripts `dev`, `build` e `typecheck`. React, Vite e TypeScript ficam pinados. |

### 2.3 Contrato dos adapters (FR-ADP)

| ID | Requisito |
|----|-----------|
| FR-ADP-01 | O processo DEVE ter um `IProbeAdapter` por transporte de egress implementado: Kafka, RabbitMQ e FIX. Cada um expõe transporte, estado (`Disconnected`, `Connecting`, `Live`, `Faulted`), `ConnectAsync(profile)`, `DisconnectAsync` e contadores. |
| FR-ADP-02 | Um `ProbeController` DEVE manter um adapter por transporte. Conectar um NÃO derruba os outros. O stop do processo fecha os três. |
| FR-ADP-03 | DEVE haver no máximo uma conexão ativa por transporte. Um segundo connect no mesmo transporte desconecta a anterior antes de abrir a nova, ou recusa com erro claro. |
| FR-ADP-04 | A tela WebSocket NÃO passa pelo `ProbeController`. O socket do egress é aberto pela página. |

### 2.4 Kafka (FR-KAF)

| ID | Requisito |
|----|-----------|
| FR-KAF-01 | O adapter Kafka DEVE ser um consumer `Confluent.Kafka` no processo ClientProbe. O NexusRouter publica em `fix.ordexec.raw` (`127.0.0.1:19092`); o valor é o frame FIX cru e a chave é nula. |
| FR-KAF-02 | O grupo de consumo DEVE ser configurável, com prefixo default `probe-`. O offset default DEVE ser `latest`. |
| FR-KAF-03 | O body de cada record DEVE ser tratado como ASCII FIX e virar um `ProbeEvent`. Falha de decode incrementa o contador de parse e não derruba o consumer. |

### 2.5 RabbitMQ (FR-RAB)

| ID | Requisito |
|----|-----------|
| FR-RAB-01 | O adapter Rabbit DEVE usar `RabbitMQ.Client` no mesmo processo. O NexusRouter faz `BasicPublish` em `fix.orders` / `fix.raw` (`127.0.0.1:5672`) e não declara exchange nem fila. |
| FR-RAB-02 | No connect, o probe DEVE declarar uma fila exclusiva auto-delete e fazer bind na exchange e routing key do perfil. |
| FR-RAB-03 | No primeiro connect, o probe DEVE declarar a exchange se ela não existir: tipo `topic`, durável. O README DEVE deixar explícito que o probe cria a topologia que o router assume. Sem essa exchange, a publicação do NexusRouter falha no broker. |
| FR-RAB-04 | O body publicado é o frame FIX cru. O tratamento de decode é o mesmo de FR-KAF-03. |
| FR-RAB-05 | A credencial default DEVE ser lida de `RABBITMQ_DEFAULT_USER` e `RABBITMQ_DEFAULT_PASS`, o mesmo par do compose. O perfil guarda o **nome** da variável, não o segredo. |

### 2.6 FIX outbound (FR-FIX)

| ID | Requisito |
|----|-----------|
| FR-FIX-01 | O adapter FIX DEVE ser um iniciador TCP mínimo no processo, contra o acceptor do NexusRouter. Default de laboratório: `127.0.0.1:6001`, `SenderCompID` do probe, `TargetCompID` `NEXUSROUTER` (o `SenderCompId` de `fix-acceptor-01`). `BeginString` fixo `FIX.4.4`. |
| FR-FIX-02 | Lista vazia de `AllowedSenderCompIds` no acceptor aceita qualquer `SenderCompID`. O probe DEVE completar Logon (`35=A`), emitir heartbeat no `HeartBtInt` negociado (default 20) e enviar Logout (`35=5`) no disconnect. |
| FR-FIX-03 | O `MsgSeqNum` local existe só para a sessão de prova. Não há sequence store compartilhado com o NexusRouter. |
| FR-FIX-04 | O enquadramento DEVE recortar frames `8=…10=xxx` com um scanner local. O probe NÃO importa o scanner do simulador nem o do NexusRouter. `BodyLength` e `CheckSum` das mensagens que o probe envia DEVEM seguir FIX 4.4. |
| FR-FIX-05 | Frames de aplicação recebidos (em especial `ExecutionReport`, `35=8`) DEVEM aparecer na telemetria. Heartbeat recebido mantém a sessão `Live` e não precisa lotar a tabela. |

### 2.7 WebSocket de egress na página (FR-WS)

| ID | Requisito |
|----|-----------|
| FR-WS-01 | A página DEVE abrir o WebSocket **direto** no acceptor do NexusRouter, com subprotocolo `nexusrouter.v1` e `encoding` `json`. O ClientProbe NÃO faz proxy desse socket. |
| FR-WS-02 | O primeiro frame DEVE ser o Logon da §3.3.1: `type=logon`, `clientId`, `token`, `encoding=json`, `replay=false`. A página consome `logonAck`, `exec`, `gap`, `heartbeat` e `reject`. |
| FR-WS-03 | Enquanto `nexusrouter` T-49 não estiver fechada, o item de menu existe e a tela DEVE explicar que o acceptor não está no egress. A ação de conectar permanece disponível para o mesmo build servir depois de T-49. |
| FR-WS-04 | Com T-49 fechada, `logonAck` e frames `exec` em JSON DEVEM entrar na mesma tabela dos outros transportes. O operador cadastra um `clientId` em `NexusRouter:Clients` apontando para o `DispatcherId` WebSocket, e inclui `http://localhost:8100` em `AllowedOrigins`. Lista vazia recusa `Origin`, e o browser sempre manda `Origin`. Esse cadastro é configuração do NexusRouter, documentada no README do probe; o probe não edita `src/` do router. |
| FR-WS-05 | O token do Logon fica só na memória da página. Não vai para `localStorage`, para arquivo versionado, para log nem para a API do ClientProbe. |

### 2.8 API e telemetria do processo (FR-API)

| ID | Requisito |
|----|-----------|
| FR-API-01 | O processo DEVE expor `GET /v1/state`, `POST /v1/connect` e `POST /v1/disconnect`. O payload é JSON. Erros devolvem corpo estruturado (`{ code, message }`) e HTTP 400 ou 409 conforme validação ou conflito. |
| FR-API-02 | `GET /v1/state` DEVE devolver o snapshot dos três adapters: estado, endpoint efetivo, último erro, contadores (recebidas, taxa, último evento, falhas de decode, amostradas, descartadas). |
| FR-API-03 | `POST /v1/connect` e `POST /v1/disconnect` recebem o transporte (`kafka`, `rabbit`, `fix`) e, no connect, o perfil daquele transporte. |
| FR-API-04 | `GET /v1/ws` (upgrade WebSocket na mesma origem) DEVE transmitir eventos de telemetria dos três adapters. A página assina esse canal no carregamento, no mesmo papel do `getState` e da telemetria do SimPanel. |
| FR-API-05 | O feed DEVE ser limitado: anel das últimas 500 amostras na página. Acima da taxa configurada, o processo amostra e incrementa o contador de amostradas. `TryWrite` no channel de telemetria NÃO bloqueia o consume. Overflow descarta a amostra e incrementa `dropped`. |
| FR-API-06 | Cada `ProbeEvent` DEVE carregar: `transport`, `receivedAt`, `msgType`, campos curtos `11`, `55`, `54`, `38` e `39` quando presentes, e `rawPreview` truncado. |

### 2.9 Frontend — ClientPanel (FR-UI)

| ID | Requisito |
|----|-----------|
| FR-UI-01 | A UI DEVE ser uma página única em React + Vite + TypeScript, com CSS Modules e variáveis em `tokens.css`. Sem `react-router`. O hash (`#/kafka`, `#/rabbit`, `#/fix`, `#/ws`) guarda a tela no refresh. |
| FR-UI-02 | A linguagem visual segue o SimPanel: escuro por padrão, claro no toggle, cor de estado sempre com rótulo. Tokens, `ThemeToggle`, o padrão do modal e o anel do feed saem do SimPanel por **cópia enxuta**, não por pacote compartilhado. |
| FR-UI-03 | A topbar DEVE mostrar o nome `ClientPanel`, o subtítulo de prova de saída em `localhost:8100`, o badge de conectividade da API e do `/v1/ws`, a engrenagem do modal e o tema. |
| FR-UI-04 | O miolo é um menu lateral com quatro itens (Kafka, Rabbit, FIX, WebSocket), não o dashboard de duas colunas do SimPanel. |
| FR-UI-05 | Cada tela de dispatcher DEVE mostrar, nesta ordem: cartão de conexão (endpoint, estado, último erro, conectar e desconectar); quatro números (recebidas, msg/s, último evento, falhas de parse); tabela com hora, transporte, `35=`, `11=`, `55=`, `39=` e trecho do raw. |
| FR-UI-06 | O modal de configuração NÃO ocupa a página. Abas: Kafka, Rabbit, FIX, WebSocket. Cada aba guarda host, porta e identificadores. Senha e token são campos da sessão, limpos ao fechar o browser, e não vão para `localStorage`. |
| FR-UI-07 | O fetch da API DEVE ser relativo à origem da página: `/v1/state`, `/v1/connect`, `/v1/disconnect`. |
| FR-UI-08 | O store mantém uma tela por vez e um buffer de eventos por transporte, para trocar de menu sem apagar o anel dos outros. |
| FR-UI-09 | Na tela FIX o cartão mostra `SenderCompID`, `TargetCompID` e `MsgSeqNum`. Na tela WebSocket o badge de egress reflete o socket direto, separado do badge de `/v1/ws`. |
| FR-UI-10 | Defaults de tela, alinhados ao `appsettings.json` do Host: Kafka `127.0.0.1:19092` · tópico `fix.ordexec.raw` · grupo `probe`; Rabbit `127.0.0.1:5672` · exchange `fix.orders` · key `fix.raw`; FIX `127.0.0.1:6001` · Target `NEXUSROUTER`. |

---

## 3. Requisitos Não Funcionais

### 3.1 Segredos (NFR-SEC)

| ID | Requisito |
|----|-----------|
| NFR-SEC-01 | Senha de Rabbit e token do Logon WebSocket NÃO entram em arquivo versionado nem em `localStorage`. |
| NFR-SEC-02 | O processo não loga senha, token, nem o frame cru em nível que não seja `Debug`/`Trace`. `rawPreview` na telemetria é truncado. |
| NFR-SEC-03 | Uma senha digitada no modal, se enviada no connect, vive só na memória da conexão e não é escrita em `appsettings.json`. |

### 3.2 Performance da ferramenta (NFR-PERF)

| ID | Requisito |
|----|-----------|
| NFR-PERF-01 | O consume de Kafka, Rabbit e FIX NÃO espera a página. Telemetria usa channel bounded e descarte sob pressão. |
| NFR-PERF-02 | O probe é ferramenta de laboratório. Não está sujeito à latência do hot path do NexusRouter. |

### 3.3 Configurabilidade (NFR-CFG)

| ID | Requisito |
|----|-----------|
| NFR-CFG-01 | Defaults de broker, tópico, exchange, routing key, acceptor FIX e URL do Kestrel vêm de `appsettings.json` do ClientProbe, alinhados ao `appsettings.json` do `NexusRouter.Host`. Nada disso é hardcoded como única fonte. |
| NFR-CFG-02 | A página pode sobrepor host, porta e identificadores pelo modal. O que não for segredo pode persistir no browser. Segredos não. |

### 3.4 Manutenibilidade (NFR-MAINT)

| ID | Requisito |
|----|-----------|
| NFR-MAINT-01 | Alvo **.NET 10**, nullable enable, warnings as errors, versões de NuGet fixadas em `utils/client/Directory.Packages.props` (`Confluent.Kafka`, `RabbitMQ.Client`). |
| NFR-MAINT-02 | `utils/client/README.md` DEVE descrever: pré-requisitos (.NET 10 SDK, Node 20+), `dotnet run` em `:8100`, o modo dev na mesma origem, o fluxo com compose + simulador + NexusRouter, a declaração da exchange Rabbit, e o cadastro de `AllowedOrigins` / `clientId` para quando T-49 existir. |
| NFR-MAINT-03 | Testes automatizados, se existirem, ficam num projeto de teste ao lado, na mesma solução, sem `ProjectReference` ao NexusRouter nem ao simulador. A aplicação permanece um csproj. A ClientApp entra com `typecheck`, sem suíte extra. |
| NFR-MAINT-04 | Documentação XML nos tipos públicos do processo. |

---

## 4. Restrições e Premissas

- **Um executável.** O csproj é a aplicação. `ClientApp` é pasta de frontend dentro dele.
- **Quatro saídas, três adapters.** Kafka, Rabbit e FIX são adapters no processo. WebSocket de egress é cliente na página e depende de `nexusrouter` T-49 para conexão real.
- **Payload.** Kafka e Rabbit carregam o frame FIX cru, como o egress publica hoje.
- **Portas default.** Página/API `8100`. Kafka `19092`. Rabbit `5672`. FIX acceptor do router `6001`. O simulador segue no próprio par (`9876` / `8099` / Vite `5173`) depois do move para `utils/sim/`.
- **Fora de escopo.** Entrar na solução ou na CI do NexusRouter. Alterar `docker-compose.yml` ou `src/` do router. Proxy do WebSocket de egress. Pacote npm compartilhado com o SimPanel. Reescrever relatórios em `.agents/pipeline/`. Suíte de UI além de `typecheck`.
- **Infra.** O probe não sobe broker. Usa o compose já existente.
