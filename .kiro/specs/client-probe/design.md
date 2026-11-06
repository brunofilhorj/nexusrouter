# Client Probe — Documento de Design de Arquitetura

> **Status do Documento:** Rascunho v1.0
> **Classificação:** Especificação de Engenharia Interna
> **Relacionado a:** `requirements.md` desta spec; `nexusrouter/design.md` §3.3 e §3.3.1

---

## 1. Filosofia Arquitetural

O ClientProbe prova a saída. O NexusRouter continua dono do roteamento; o simulador continua dono da entrada. Este processo só consome.

Três regras fecham o desenho:

1. **Um projeto publicável.** O csproj sobe a página e os adapters. A SPA não é outro host e não tem origem própria.
2. **O browser não fala protocolo de broker.** Kafka, AMQP e FIX TCP ficam no processo. O único socket que a página abre para fora de `:8100` é o WebSocket de egress, porque esse é o contrato que o dispatcher vai exigir.
3. **Isolamento igual ao do simulador.** Sem `ProjectReference` ao NexusRouter ou ao DropCopy. `utils/client/` sai da árvore sem alterar a build principal.

O simulador permanece com backend e frontend separados, como a spec dele já definiu. Essa divisão não se repete aqui.

---

## 2. Arquitetura Conceitual

```
utils/client  (solução isolada)
└── src/ClientProbe          WebApplication, :8100
    ├── Probe/               contrato, controller, perfis
    ├── Kafka/  Rabbit/  Fix/
    ├── Telemetry/           channel bounded → /v1/ws
    ├── wwwroot/             dist do Vite (build)
    └── ClientApp/           React + Vite, fetch relativo
```

| Canal | Quem conecta | Onde mora o cliente | Por que não o outro caminho |
|-------|----------------|---------------------|-----------------------------|
| Kafka | Processo | `Confluent.Kafka` consumer, grupo `probe-*`, offset `latest` | O browser não fala o protocolo do broker. Kafka REST ou a UI em `:8080` medem outro caminho. |
| RabbitMQ | Processo | `RabbitMQ.Client`, fila exclusiva auto-delete, bind | AMQP 0-9-1 não existe no browser. STOMP não é o que o dispatcher publica. |
| FIX outbound | Processo | Iniciador mínimo, Logon em `127.0.0.1:6001` | TCP cru. Reusar `FixSession` do NexusRouter quebraria o isolamento. |
| WebSocket egress | Página | `NexusSocket`, subprotocolo `nexusrouter.v1` | Proxy no ClientProbe esconderia o contrato (Origin, subprotocolo, Logon JSON). |
| Telemetria da página | Página → processo | `/v1/ws` na mesma origem | Substitui o que o SimPanel faz com API e WS separados. |

---

## 3. Processo

### 3.1 `IProbeAdapter` e `ProbeController`

```csharp
enum ProbeTransport { Kafka, Rabbit, Fix }
enum ProbeState { Disconnected, Connecting, Live, Faulted }

interface IProbeAdapter
{
    ProbeTransport Transport { get; }
    ProbeState State { get; }
    string Endpoint { get; }
    string? LastError { get; }
    ProbeCounters Counters { get; }
    Task ConnectAsync(ProbeProfile profile, CancellationToken ct);
    Task DisconnectAsync(CancellationToken ct);
}
```

`ProbeCounters`: `received`, `parseFailures`, `sampled`, `dropped`, `lastReceivedAt`. A taxa (msg/s) é derivada no snapshot, não armazenada pelo adapter.

`ProbeController` registra um adapter por `ProbeTransport`. `Connect` de um transporte não chama `Disconnect` dos outros. `StopAsync` do host desconecta os três. Um connect com o mesmo transporte já `Live` ou `Connecting` encerra a conexão anterior e abre outra com o perfil novo.

`ProbeProfile` é um registro por transporte:

| Transporte | Campos |
|------------|--------|
| Kafka | `BootstrapServers`, `Topic`, `GroupId`, `AutoOffsetReset` (`Latest` default) |
| Rabbit | `Host`, `Port`, `VirtualHost`, `Exchange`, `RoutingKey`, `UsernameEnvVar`, `PasswordEnvVar`; senha explícita só em memória se o modal enviar |
| FIX | `Host`, `Port`, `SenderCompId`, `TargetCompId`, `HeartBtInt` |

### 3.2 Kafka

`KafkaConsumerAdapter` faz `Subscribe` no tópico do perfil. Cada `Consume` entrega `byte[]`. O valor vira string ASCII e segue para o parser de campos curtos. A chave é ignorada (o producer do router usa chave nula).

O loop de consume é uma `Task` do adapter. Cancelamento no disconnect. Erro de broker leva o estado a `Faulted`, preenche `LastError` e não relança para o host.

### 3.3 Rabbit

`RabbitConsumerAdapter`, no connect:

1. Abre conexão com o host do perfil. Usuário e senha saem das env vars nomeadas (`RABBITMQ_DEFAULT_USER` / `RABBITMQ_DEFAULT_PASS` no default).
2. Declara a exchange `topic` durável (idempotente). O README avisa que essa declaração é o que materializa a topologia que o `RabbitMQMessageDispatcher` assume.
3. Declara fila exclusiva, auto-delete, nome gerado pelo broker.
4. Faz bind na routing key do perfil.
5. Consome e decodifica o body como ASCII FIX.

Disconnect fecha canal e conexão. A fila exclusiva some com a conexão.

### 3.4 FIX

`FixInitiatorAdapter` abre `TcpClient`, envia Logon (`35=A`) com `SenderCompID`, `TargetCompID`, `HeartBtInt` e `MsgSeqNum` local começando em 1, e espera o Logon de volta. Estado `Live` depois do Logon aceito.

Um timer dispara Heartbeat (`35=0`) a cada `HeartBtInt` quando não houve envio. `DisconnectAsync` envia Logout (`35=5`) e fecha o socket. Queda de TCP sem Logout vai a `Faulted` ou `Disconnected` com `LastError`.

`FixFrameScanner` recorta um frame completo de um buffer: começa em `8=FIX.4.4\x01` e termina em `10=<3 dígitos>\x01`. Não referencia o scanner do simulador.

`FixSessionWriter` escreve os campos de sessão que o probe envia (Logon, Heartbeat, Logout) e fecha `BodyLength` e `CheckSum` na regra FIX 4.4. O sequence number não é persistido.

Frames recebidos passam pelo mesmo extrator de campos curtos. `35=0` e `35=1` atualizam o relógio da sessão; a tabela mostra mensagens de aplicação. `MsgSeqNum` de saída e o último de entrada ficam no snapshot para o cartão da tela FIX.

### 3.5 Campos curtos

Um extrator único varre o frame ASCII e preenche `msgType` (tag 35) e, se presentes, as tags 11, 55, 54, 38 e 39. `rawPreview` guarda no máximo 240 caracteres, com SOH renderizado como `|`. Frame sem tag 35 conta como falha de parse e ainda pode aparecer com o preview, para o operador ver o lixo.

### 3.6 `TelemetryHub`

Channel bounded (default 1024). O adapter chama `TryWrite`. `false` incrementa `dropped` e o consume segue.

Uma task lê o channel e faz broadcast JSON para os sockets de `/v1/ws`. Acima de `SampleAbovePerSecond` (default 50), o hub emite 1 a cada N eventos daquele transporte e incrementa `sampled`. O contador `received` do adapter conta o que chegou do broker, não o que a página desenhou.

Evento:

```json
{
  "transport": "kafka",
  "receivedAt": "2026-11-05T22:00:00.000Z",
  "msgType": "8",
  "fields": { "11": "ORD1", "55": "PETR4", "54": "1", "38": "100", "39": "2" },
  "rawPreview": "8=FIX.4.4|9=...|35=8|11=ORD1|..."
}
```

Campo ausente não entra em `fields`.

---

## 4. HTTP

Sem CORS. `Program.cs` mapeia:

| Método | Caminho | Efeito |
|--------|---------|--------|
| GET | `/v1/state` | Snapshot dos três adapters |
| POST | `/v1/connect` | `{ "transport": "kafka", "profile": { ... } }` |
| POST | `/v1/disconnect` | `{ "transport": "fix" }` |
| GET | `/v1/ws` | Upgrade. Broadcast de `ProbeEvent` e, em intervalo curto, snapshot de contadores |

Transporte desconhecido ou perfil inválido: HTTP 400, `{ "code", "message" }`. Connect enquanto o adapter está `Connecting`: HTTP 409.

Arquivos estáticos e fallback de SPA ficam **depois** do mapeamento de `/v1`, para o fallback não engolir a API. Em `Development`, requests que não são `/v1/*` são encaminhados ao Vite (porta interna, não é a URL do operador). Fora de Development, `wwwroot` serve o `dist`.

O target de build do csproj roda `npm run build` em `ClientApp` e copia `dist` para `wwwroot`. O target de dev sobe `npm run dev` quando o ambiente é Development.

---

## 5. Frontend — ClientPanel

### 5.1 Estrutura

Tudo sob `src/ClientProbe/ClientApp/`.

```
ClientApp/
├── package.json          # dev, build, typecheck
├── index.html
├── vite.config.ts
└── src/
    ├── App.tsx           # topbar + menu + tela
    ├── theme/tokens.css
    ├── theme/useTheme.ts
    ├── components/
    │   ├── ThemeToggle.tsx
    │   ├── ConfigModal.tsx
    │   ├── ConnectivityBadge.tsx
    │   ├── DispatcherNav.tsx
    │   ├── ConnectionCard.tsx
    │   ├── StatsRow.tsx
    │   └── EventTable.tsx
    ├── screens/
    │   ├── BrokerScreen.tsx    # Kafka e Rabbit
    │   ├── FixScreen.tsx
    │   └── WebSocketScreen.tsx
    ├── ws/telemetry.ts         # /v1/ws, backoff
    ├── ws/nexusSocket.ts       # nexusrouter.v1, só na tela WebSocket
    ├── state/store.ts
    └── api/client.ts           # fetch relativo
```

### 5.2 Navegação e layout

Hash, sem router: `#/kafka`, `#/rabbit`, `#/fix`, `#/ws`. Hash vazio abre Kafka.

Topbar: título `ClientPanel`, texto "Prova de saída do NexusRouter — localhost:8100", badge API/`/v1/ws`, engrenagem, tema.

Menu lateral, quatro itens. A área da tela é sempre:

1. `ConnectionCard` — endpoint, estado com rótulo, último erro, conectar / desconectar.
2. `StatsRow` — recebidas, msg/s, último evento, falhas de parse. Amostradas e descartadas aparecem quando forem maiores que zero.
3. `EventTable` — anel de 500, mais recente no topo, mono nos campos FIX.

Tema: `data-theme` na raiz, escuro default, persistido como no SimPanel. Cor de estado acompanha texto (desconectado neutro, connecting âmbar, live verde, faulted vermelho).

### 5.3 Modal

Quatro abas. Host, porta, tópico, grupo, exchange, routing key, CompIDs e `clientId` podem ir para `localStorage`. Senha Rabbit e token WebSocket ficam em estado de memória do modal e somem no unload. A senha, se preenchida, vai no corpo de `/v1/connect` e o processo não a grava. O token não vai para `/v1/connect`; só o `NexusSocket` o envia no Logon.

### 5.4 Store

Um transporte visível. Buffers separados por id (`kafka`, `rabbit`, `fix`, `ws`). Eventos de `/v1/ws` entram no buffer do transporte. Eventos do `NexusSocket` entram no buffer `ws`, no mesmo formato de linha (hora, transporte, 35, 11, 55, 39, preview). `logonAck` e `reject` atualizam o cartão; `exec` vira linha; `gap` vira linha com o intervalo no preview.

### 5.5 Tela WebSocket

Não chama `/v1/connect`. `nexusSocket.ts` faz o upgrade com `Sec-WebSocket-Protocol: nexusrouter.v1` e manda o Logon JSON da §3.3.1 (`encoding` `json`, `replay` false).

Enquanto o operador não apontar host e porta, a tela mostra que o acceptor WebSocket não está no egress (spec `nexusrouter` T-49 a T-53). Com host e porta preenchidos, conectar tenta o socket mesmo assim: o build não muda quando T-49 fechar.

O badge dessa tela é o do socket de egress. O badge da topbar continua sendo API e `/v1/ws`.

---

## 6. Estrutura do Projeto

```
utils/
├── sim/                                   # git mv de sim/  (T-01)
│   ├── README.md
│   ├── backend/                           # DropCopySimulator.slnx, props, src, tests
│   └── frontend/                          # SimPanel
└── client/
    ├── README.md
    ├── ClientProbe.slnx                  # um projeto de aplicação + um de teste
    ├── Directory.Build.props
    ├── Directory.Packages.props
    ├── src/ClientProbe/
    │   ├── ClientProbe.csproj            # npm run build → wwwroot; dev sobe o Vite
    │   ├── Program.cs
    │   ├── appsettings.json
    │   ├── Probe/
    │   │   ├── IProbeAdapter.cs
    │   │   ├── ProbeController.cs
    │   │   ├── ProbeProfile.cs
    │   │   └── ProbeCounters.cs
    │   ├── Kafka/KafkaConsumerAdapter.cs
    │   ├── Rabbit/RabbitConsumerAdapter.cs
    │   ├── Fix/
    │   │   ├── FixInitiatorAdapter.cs
    │   │   ├── FixFrameScanner.cs
    │   │   └── FixSessionWriter.cs
    │   ├── Telemetry/
    │   │   ├── TelemetryHub.cs
    │   │   └── ProbeEvent.cs
    │   ├── wwwroot/                      # saída do Vite; vazio até o build
    │   └── ClientApp/                    # Seção 5
    └── tests/ClientProbe.Tests/         # scanner, writer, controller; sem ref. ao router
```

### 6.1 Isolamento

| Ponto | Neutralização |
|-------|----------------|
| Props da raiz | `utils/client/Directory.Build.props` e `Directory.Packages.props` cortam a herança. |
| `NexusRouter.slnx` | Não lista `utils/`. |
| CI | Nenhum workflow novo e nenhum step alterado. |
| Código do core e do simulador | Sem `ProjectReference`. Scanner e writer são locais. |
| Compose | Já publica `:19092` e `:5672`. O probe não mexe no arquivo. |

O move `sim/` → `utils/sim/` mantém a barreira dentro de `utils/sim/backend/`. `dotnet test utils/sim/backend/DropCopySimulator.slnx` é a verificação. A spec do simulador (§5 do design e os trechos de localização dos requisitos) passa a citar `utils/sim/`, senão o próximo agente recria `sim/`.

### 6.2 README (`utils/client/README.md`)

1. Pré-requisitos: .NET 10 SDK, Node 20+, compose com Kafka e Rabbit.
2. `dotnet run --project utils/client/src/ClientProbe` abre `http://localhost:8100`.
3. Development: a mesma URL, Vite atrás do Kestrel.
4. Fluxo: simulador em `utils/sim/`, NexusRouter com rota para o dispatcher sob prova, probe conectado na aba correspondente.
5. Rabbit: o primeiro connect declara a exchange `fix.orders` (topic, durável).
6. WebSocket: até T-49 a tela explica a ausência do acceptor. Depois, `AllowedOrigins` inclui `http://localhost:8100` e o `clientId` aponta para o `DispatcherId`.

---

## 7. Modelo de Configuração

```
ClientProbe:
  Urls: "http://localhost:8100"
  Telemetry:
    ChannelCapacity: 1024
    SampleAbovePerSecond: 50
    RawPreviewLength: 240
    RingSize: 500
  Kafka:
    BootstrapServers: "127.0.0.1:19092"
    Topic: "fix.ordexec.raw"
    GroupId: "probe-local"
    AutoOffsetReset: "Latest"
  Rabbit:
    Host: "127.0.0.1"
    Port: 5672
    VirtualHost: "/"
    Exchange: "fix.orders"
    RoutingKey: "fix.raw"
    UsernameEnvVar: "RABBITMQ_DEFAULT_USER"
    PasswordEnvVar: "RABBITMQ_DEFAULT_PASS"
  Fix:
    Host: "127.0.0.1"
    Port: 6001
    SenderCompId: "PROBE"
    TargetCompId: "NEXUSROUTER"
    HeartBtInt: 20
  WebSocket:
    # só default de tela; a página conecta direto
    Url: ""
    Subprotocol: "nexusrouter.v1"
```

Segredos não aparecem neste arquivo. `WebSocket.Url` vazio significa "acceptor ainda não existe"; a tela usa isso no texto de bloqueio até o operador preencher host e porta.

---

## 8. Fluxo de Laboratório

1. Compose sobe Kafka e Rabbit.
2. Simulador (`utils/sim`) aceita o Logon do NexusRouter e gera `ExecutionReport`.
3. O router publica no dispatcher escolhido: tópico `fix.ordexec.raw`, exchange `fix.orders` com key `fix.raw`, ou acceptor FIX `:6001`.
4. O operador abre `http://localhost:8100`, escolhe o menu e conecta.
5. A tabela mostra o frame cru (Kafka, Rabbit, FIX) ou o `exec` JSON (WebSocket, depois de T-49).

O WebSocket de egress não passa pelo `TelemetryHub`. A página traduz o frame para a linha da tabela.

---

## 9. Decisões

| Decisão | Motivo |
|---------|--------|
| Um csproj com `ClientApp` dentro | Kafka, Rabbit e FIX não rodam no Chrome. Uma origem elimina CORS e URL base. |
| Simulador segue com dois projetos | A spec do DropCopy já fechou essa divisão. O move para `utils/sim/` não a reabre. |
| Cópia enxuta do tema, sem pacote compartilhado | Os dois utilitários continuam excluíveis sozinhos. |
| Probe declara a exchange Rabbit | O dispatcher só publica. Sem a exchange, o broker rejeita a publicação. A fila do probe é exclusiva e não é a topologia do router. |
| Sem proxy do WebSocket de egress | O valor da prova é o contrato da §3.3.1: subprotocolo, Origin, Logon JSON. |
| Testes num projeto irmão, não na aplicação | A aplicação permanece um csproj. O scanner e o writer precisam de teste sem subir o router. |
| Tela WebSocket entregue antes de T-49 | O menu fica completo. A conexão real espera o acceptor; o cliente da página não. |

---

## 10. Verificação

| Pronto quando | Como |
|---------------|------|
| Move do simulador | `dotnet test utils/sim/backend/DropCopySimulator.slnx` verde. `npm run typecheck` do SimPanel verde. `NexusRouter.slnx` não lista `utils/`. |
| Página e API na mesma origem | `dotnet run` abre `:8100`. `/v1/state` responde. O modo dev recarrega a UI sem segunda URL. |
| Kafka e Rabbit | Com compose e uma rota do router para esses dispatchers, o frame FIX cru aparece nas duas tabelas. |
| FIX | Sessão `Live` contra `fix-acceptor-01` em `:6001`, `MsgSeqNum` subindo. Disconnect envia Logout. |
| WebSocket | Antes de T-49, o menu mostra o item e a tela explica o bloqueio. Com T-49 fechada, `logonAck` e `exec` JSON entram na mesma tabela, com `AllowedOrigins` contendo `http://localhost:8100`. |
| Typecheck | `npm run typecheck` em `ClientApp` verde. Testes do scanner/writer verdes, sem referência ao NexusRouter. |
