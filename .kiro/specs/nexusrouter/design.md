# NexusRouter — Documento de Design de Arquitetura

> **Status do Documento:** Rascunho v1.0  
> **Classificação:** Especificação de Engenharia Interna  
> **Domínio:** High-Frequency Trading (HFT) — Motor de Execução de Ordens / DropCopy Distribution Engine

---

## 1. Filosofia Arquitetural

O NexusRouter é projetado em torno de uma única restrição fundamental: **o hot path deve ter latência determinística sub-50µs do recebimento do byte até o handoff ao dispatcher**. Cada decisão arquitetural é avaliada primeiro contra essa restrição. O design separa responsabilidades em quatro tiers com **regras de acoplamento estritamente assimétricas**: tiers inferiores (Ingress, Core Router) não podem bloquear sincronamente em tiers superiores (Egress, Audit). A informação flui em uma única direção — de entrada para o router, de saída para os dispatchers — sem que back-pressure atravesse um limite de tier de volta para o hot path.

---

## 2. Arquitetura Conceitual de Alto Nível

```
┌─────────────────────────────────────────────────────────────────────────────────────┐
│                                   NexusRouter Process                                │
│                                                                                     │
│  ┌──────────────────────────────────────────────────────────────────────────────┐   │
│  │                          INGRESS TIER                                        │   │
│  │                                                                              │   │
│  │   ┌─────────────────┐   ┌─────────────────┐   ┌─────────────────┐          │   │
│  │   │ Session A       │   │ Session B       │   │ Session N       │          │   │
│  │   │ (FIX 4.4)       │   │ (FIX 5.0/FIXT) │   │ (FIX 4.2)       │          │   │
│  │   │                 │   │                 │   │                 │          │   │
│  │   │ TcpClient       │   │ TcpClient       │   │ TcpClient       │          │   │
│  │   │ + PipeReader    │   │ + PipeReader    │   │ + PipeReader    │          │   │
│  │   │ + SessionFSM    │   │ + SessionFSM    │   │ + SessionFSM    │          │   │
│  │   │ + FrameDecoder  │   │ + FrameDecoder  │   │ + FrameDecoder  │          │   │
│  │   └────────┬────────┘   └────────┬────────┘   └────────┬────────┘          │   │
│  │            │                     │                     │                   │   │
│  └────────────┼─────────────────────┼─────────────────────┼───────────────────┘   │
│               │  IngressFrame (ref struct, rented buffer)  │                       │
│               └─────────────────────┬───────────────────── ┘                       │
│                                     │                                               │
│  ┌──────────────────────────────────▼───────────────────────────────────────────┐   │
│  │                          CORE ROUTER / HOT PATH                              │   │
│  │                                                                              │   │
│  │   ┌─────────────────────────────────────────────────────────────────────┐   │   │
│  │   │  RouterEngine                                                       │   │   │
│  │   │                                                                     │   │   │
│  │   │   IngressFrame                                                      │   │   │
│  │   │       │                                                             │   │   │
│  │   │       ▼                                                             │   │   │
│  │   │   ┌──────────────────────────────────────────┐                     │   │   │
│  │   │   │  RouteResolver                           │                     │   │   │
│  │   │   │  (immutable routing table, lock-free     │                     │   │   │
│  │   │   │   dictionary lookup by SessionId)        │                     │   │   │
│  │   │   └──────────────────────┬───────────────────┘                     │   │   │
│  │   │                          │ RouteContext                             │   │   │
│  │   │                          ▼                                         │   │   │
│  │   │   ┌──────────────────────────────────────────┐                     │   │   │
│  │   │   │  DeliveryMode Switch                     │                     │   │   │
│  │   │   │  ┌──────────────┐  ┌───────────────────┐ │                     │   │   │
│  │   │   │  │ PassThrough  │  │  Parse            │ │                     │   │   │
│  │   │   │  │ (SeqNum +    │  │  (FixTokenizer →  │ │                     │   │   │
│  │   │   │  │  Checksum    │  │   FixMessage      │ │                     │   │   │
│  │   │   │  │  rewrite on  │  │   value type)     │ │                     │   │   │
│  │   │   │  │  Span<byte>) │  │                   │ │                     │   │   │
│  │   │   │  └──────┬───────┘  └─────────┬─────────┘ │                     │   │   │
│  │   │   └─────────┼─────────────────── ┼────────────┘                     │   │   │
│  │   │             └──────────┬──────────┘                                 │   │   │
│  │   │                        │ DispatchPayload                            │   │   │
│  │   │                        ▼                                            │   │   │
│  │   │   ┌──────────────────────────────────────────┐                     │   │   │
│  │   │   │  FanOut Engine                           │                     │   │   │
│  │   │   │  (TryWrite to each dispatcher's          │                     │   │   │
│  │   │   │   bounded Channel — non-blocking)        │                     │   │   │
│  │   │   └──────────────────────────────────────────┘                     │   │   │
│  │   └─────────────────────────────────────────────────────────────────────┘   │   │
│  │                                                                              │   │
│  │   ┌─────────────────────────────────────────────────────────────────────┐   │   │
│  │   │  Audit Channel (TryWrite — non-blocking)                            │   │   │
│  │   └─────────────────────────────────────────────────────────────────────┘   │   │
│  └──────────────────────────────────────────────────────────────────────────┘   │
│                                     │                                           │
│  ┌──────────────────────────────────▼───────────────────────────────────────┐   │
│  │                          EGRESS TIER                                     │   │
│  │                                                                          │   │
│  │  ┌────────────────────┐  ┌──────────────────────┐  ┌──────────────────┐ │   │
│  │  │ KafkaMessage       │  │ RabbitMQ             │  │ FixOutbound      │ │   │
│  │  │ Dispatcher         │  │ MessageDispatcher    │  │ Dispatcher       │ │   │
│  │  │                    │  │                      │  │                  │ │   │
│  │  │ Channel<Payload>   │  │ Channel<Payload>     │  │ Channel<Payload> │ │   │
│  │  │ ↓                  │  │ ↓                    │  │ ↓                │ │   │
│  │  │ Confluent.Kafka    │  │ RabbitMQ.Client      │  │ TcpListener      │ │   │
│  │  │ IProducer          │  │ IChannel             │  │ N sessões FIX    │ │   │
│  │  └────────────────────┘  └──────────────────────┘  └──────────────────┘ │   │
│  │  WebSocketMessageDispatcher: acceptor, N sessões, projeção por Logon     │   │
│  └──────────────────────────────────────────────────────────────────────────┘   │
│                                                                                  │
│  ┌───────────────────────────────────────────────────────────────────────────┐  │
│  │                          COLD PATH / AUDIT TIER                           │  │
│  │                                                                           │  │
│  │  Channel<AuditRecord> (bounded) → AuditWriterService                      │  │
│  │       ├─► Serilog Sink (arquivo / console — metadados operacionais)       │  │
│  │       └─► PostgresAuditSink (Npgsql COPY → TimescaleDB hypertable)        │  │
│  └───────────────────────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────────────────────┘
```

---

## 3. Detalhamento dos Tiers

### 3.1 Ingress Tier

Cada sessão upstream é representada por uma instância de `FixSession`, que é um `IHostedService` rodando em sua própria thread dedicada (via `Task.Factory.StartNew` com `TaskCreationOptions.LongRunning` para fixá-la fora do thread-pool).

**Leitura do Socket:** `System.IO.Pipelines` é usado exclusivamente. Um `PipeWriter` é alimentado pelo loop `Socket.ReceiveAsync` usando buffers pré-alocados de `MemoryPool<byte>.Shared`. O `PipeReader` na outra ponta enquadra as mensagens. Isso elimina as cópias duplas que ocorreriam com uma abordagem ingênua de buffer `byte[]`.

**Decodificação de Frames:** Uma interface plugável `ISessionFrameDecoder` trata o framing específico do protocolo (ex.: framing delimitado por FIX BeginString + BodyLength, ou SBE com prefixo SOFH). Ela retorna um `IngressFrame` — um `ref struct` contendo um slice `ReadOnlySequence<byte>` no buffer do pipe, mais metadados (SessionId, timestamp de ingresso).

**Session FSM:** O `FixSessionStateMachine` gerencia os estados do ciclo de vida da sessão: `Disconnected → Connecting → LogonSent → Active → LogoutSent → Disconnected`. Apenas mensagens administrativas de nível de sessão (Heartbeat, TestRequest, ResendRequest, Logout) são processadas dentro da FSM. Mensagens de aplicação são passadas imediatamente para o router sem parsing adicional no modo `PassThrough`.

**Reconexão:** Cada `FixSession` possui um objeto de valor `ExponentialBackoffReconnectPolicy` contendo `BaseDelay`, `MaxDelay` e `JitterFactor`. Na desconexão, a sessão re-entra no estado `Connecting` após o delay calculado.

---

### 3.2 Core Router / Hot Path

O `RouterEngine` é o coração do sistema. É stateless do ponto de vista de roteamento — todo o estado de roteamento é imutável após a inicialização e reside em um `FrozenDictionary<SessionId, RouteContext>` (usando `System.Collections.Frozen`, disponível desde o .NET 8 e otimizado no .NET 10) para leituras lock-free O(1) com comportamento de cache otimizado.

**Lookup de Roteamento:** A cada chegada de `IngressFrame`, o router resolve o `RouteContext` para o `SessionId`. Trata-se de um único lookup no dicionário, sem locks necessários graças à coleção frozen imutável.

**Despacho por DeliveryMode:**
- No modo `PassThrough`: o router invoca o `PassThroughRewriter`, que opera no `Span<byte>` bruto, encontrando e reescrevendo apenas a `tag 34` (MsgSeqNum) e a `tag 10` (CheckSum) usando um scanner de campos zero-allocation. No egress FIX, onde o seqnum da sessão de saída pode ter largura diferente da do frame de origem, `TryRewriteTo` copia o frame para o buffer da sessão gravando a tag 34 com os dígitos exatos e recalculando também a `tag 9` (BodyLength). A cópia já existia, então não há custo extra.
- No modo `Parse`: o router invoca o `FixTokenizer`, que produz um `FixMessage` — um `ref struct` que encapsula um array stack-allocated ou poolizado de entradas `FixField` (pares tag-valor como pares `int`/`ReadOnlySpan<byte>`). Nenhuma alocação de string gerenciada ocorre.

**FanOut:** Para cada dispatcher registrado no span `RouteContext.Dispatchers`, o router chama `TryWrite` no `Channel<DispatchPayload>` do dispatcher. Isso é não bloqueante por design. O channel é o caminho de entrega ao vivo.

> **Revisão de 2026-11-06 (`notes/backlog-sessao-fix.md`), aplicada em TD-04a.** A thread da sessão de ingress, que é o escritor único do stream no log de sessão (§3.4.3), grava o frame original logo após o `TryDecode`, antes do `SessionFSM`, do `PassThroughRewriter` e do fan-out. O fan-out recebe só a referência `(stream, época, seq de entrada)` no `DispatchPayload`. Com isso, channel cheio deixa de descartar: vira lag do dispatcher, que alcança lendo do log pelo cursor. Rota sem dispatcher também não descarta — a mensagem fica no log. A métrica de lag por dispatcher substitui a antiga métrica de descarte por channel cheio. A descrição acima vale como o caminho de entrega ao vivo; a durabilidade vem do log.

---

### 3.3 Egress Tier

Todos os dispatchers compartilham o mesmo padrão operacional:

1. Uma `Task` de background (iniciada em `StartAsync`) faz loop em `ChannelReader<DispatchPayload>.ReadAllAsync`.
2. O reader retira payloads da fila e invoca a lógica de envio específica do protocolo.
3. Erros são capturados, registrados no logger estruturado, e não são relançados de volta ao consume loop.
4. Em `StopAsync`, o writer do `Channel` do dispatcher é completado e o consume loop drena os itens restantes antes de retornar.

**`FixOutboundDispatcher`:** Acceptor. Abre um `TcpListener` no endereço configurado e aceita N clientes (OMS, risco ou outro consumidor FIX). Não disca para esses clientes. Cada Logon vira uma sessão com `PipeWriter`, fila e `MsgSeqNum` próprios (estado e índice em arquivo por `DispatcherId` + SenderCompID do cliente, no log de sessão, §3.4.6). O envio para uma sessão só enfileira; a lentidão de um socket não segura as outras. Em `PassThrough`, cada sessão recebe uma cópia do frame com a tag 34 e a tag 10 reescritas para o sequence number dela (FR-PTH-02: o restante do frame permanece). Em `Parse`, `FixMessageSerializer` grava o SenderCompID do acceptor e o TargetCompID do cliente. Sem sessão logada, o payload não é descartado: fica no log de sessão (§3.4.3) e, para cliente conhecido, o seq e a entrada do índice continuam sendo reservados (§3.4.4). O cliente alcança pelo seqnum quando reconecta. Um ResendRequest é atendido pelo índice daquele cliente no log: mensagem de aplicação com `PossDupFlag=Y` e `OrigSendingTime`, administrativa em `SequenceReset-GapFill` agrupado (FR-RPL-03).

**`KafkaMessageDispatcher` e `RabbitMQMessageDispatcher`:** conectam no broker configurado e publicam. O NexusRouter é cliente desses recursos.

**`KafkaMessageDispatcher`:** Encapsula um `Confluent.Kafka.IProducer<Null, byte[]>`. Payloads são publicados em um tópico configurado. A estratégia de chave de partição (ex.: por `ClOrdID`, `SenderCompID` ou round-robin) é uma `IKafkaPartitionStrategy` configurável injetada na construção.

O producer é **agnóstico ao provedor**. `ConnectAsync` monta um `ProducerConfig` a partir de `KafkaDispatcherOptions` e não ramifica em “AWS vs local”. O mesmo código cobre:

| Ambiente | `SecurityProtocol` | `SaslMechanism` | Bootstrap típico |
|----------|--------------------|-----------------|------------------|
| Docker Compose / Kafka auto-gerenciado PLAINTEXT | `Plaintext` | `None` | `kafka:9092` (rede Compose) ou `localhost:19092` (host) |
| Kafka auto-gerenciado com TLS | `Ssl` | `None` | brokers internos `:9093` |
| Kafka auto-gerenciado / Confluent Cloud SASL | `SaslSsl` | `Plain` ou `ScramSha512` | bootstrap do cluster |
| Amazon MSK SASL/SCRAM | `SaslSsl` | `ScramSha512` | `b-#.xxx.kafka.region.amazonaws.com:9096` |
| Amazon MSK TLS mútuo | `Ssl` | `None` | bootstrap `:9094` |
| Amazon MSK IAM (opt-in) | `SaslSsl` | `AwsMskIam` | bootstrap `:9098` |

`AwsMskIam` é o único caso que carrega um callback OAUTHBEARER (pacote AWS MSK Auth). Não entra no DI a menos que o mechanism esteja selecionado. Credenciais SASL nunca ficam no POCO — só nomes de variáveis de ambiente.

**`RabbitMQMessageDispatcher`:** Encapsula um `IModel` (cliente clássico) ou `IChannel` (futuro cliente async v7+). Cada instância de dispatcher mantém um channel dedicado para evitar contenção. O flag mandatory e as confirmações de publisher são configuráveis — desabilitados por padrão para latência mínima.

#### 3.3.1 `WebSocketMessageDispatcher`

Acceptor para aplicação web ou nativa. O NexusRouter escuta; o cliente abre o WebSocket. O hot path não muda: o router continua fazendo `TryEnqueue` do `DispatchPayload` inteiro. JSON, binário e filtro de campos existem só no consume loop deste dispatcher, e só quando há sessão logada.

O que este acceptor aproveita do `FixOutboundDispatcher`, e o que deixa de lado:

| Do acceptor FIX | No WebSocket |
|-----------------|--------------|
| Escuta, aceita N clientes, não disca | Igual. `TcpListener` mais upgrade HTTP/1.1. Subprotocolo obrigatório `nexusrouter.v1`. |
| Fila de envio por sessão; socket lento não segura os outros | Igual. Channel bounded por `WebSocketAcceptedSession`. Channel cheio descarta só aquela sessão e emite métrica. |
| Sem sessão logada, o payload fica no log de sessão (§3.4.3) | Igual. Sem sessão logada não há tokenização, mas a mensagem fica no log e é recuperável quando a sessão volta. |
| Logon é o primeiro frame | Igual. Não congela retenção: o log grava tudo, não há escolha diária. |
| Seq próprio, retomado no próximo Logon | Igual. O estado por consumidor do log de sessão (§3.4.3) guarda o `seq` por `ClientId`. O número vai no envelope, não na tag 34. |
| ResendRequest lê do índice do cliente | Pedido `replay` lê do índice daquele `ClientId` no log. `seq` sem registro no intervalo vira frame `gap`. |
| Heartbeat e queda por silêncio | Ping de protocolo no intervalo configurado. Pong ausente pelo limite configurado encerra a sessão. `{ "type": "heartbeat" }` recebe eco. |
| TLS opcional no socket aceito | TLS de servidor opcional, reusando o wrap de servidor já usado no acceptor FIX. Autenticação do cliente é o token do Logon. mTLS não é o caminho do browser. |
| Cópia do frame FIX com tag 34 e checksum reescritos | Não se aplica. O cliente não consome wire FIX. |
| Uma forma única de payload para todo mundo | Cada Logon escolhe encoding e campos. Duas sessões no mesmo dispatcher podem receber projeções diferentes da mesma ordem. |

**Upgrade.** `ConnectAsync` abre `TcpListener` em `ListenAddress`/`ListenPort`. Com TLS ligado, o socket aceito passa pelo wrap de servidor antes do HTTP. O request exige `Upgrade: websocket` e `Sec-WebSocket-Protocol: nexusrouter.v1`. Se o request traz `Origin`, o valor tem de estar em `AllowedOrigins`; lista vazia recusa qualquer `Origin` e aceita só cliente sem esse header (aplicação nativa). Falha de upgrade fecha o TCP com resposta HTTP e não cria sessão. O WebSocket é `WebSocket.CreateFromStream` sobre o stream já aceito — sem Kestrel e sem ASP.NET no projeto de egress.

**Logon.** Primeiro frame de dados, dentro de `LogonTimeoutSeconds`, texto JSON, no máximo `MaxControlFrameBytes`:

```json
{
  "type": "logon",
  "clientId": "6f1e0b3a-9c2d-4a7e-8b15-2d4c0a9e7f31",
  "token": "<valor da variável de ambiente>",
  "replay": false,
  "encoding": "json",
  "fields": [11, 55, 54, 38, 44, 39]
}
```

`clientId` tem de estar no cadastro apontando para este `DispatcherId`. `token` é comparado em tempo constante com o valor de `TokenEnvVar`; o valor não entra em log nem na URL. `encoding` é `json` ou `binary`. `fields` omitido ou vazio pede todas as propriedades de aplicação. Lista presente pede só essas tags, na ordem dada, com no máximo `MaxFields` (64). Duplicata sai. As tags de sessão 8, 9, 10, 34, 35, 49, 52 e 56 saem da lista: não são propriedade da ordem. Tag desconhecida no filtro é ignorada. `replay` já não liga retenção (o log de sessão grava tudo, §3.4.3); quando `true`, pede que o servidor reemita automaticamente, ao reconectar, o intervalo entre o último `seq` entregue e o fim do log. `replay` ausente vale `false` (o cliente recupera sob demanda com o pedido `replay`).

Sucesso devolve texto:

```json
{
  "type": "logonAck",
  "replay": false,
  "encoding": "json",
  "fields": [11, 55, 54, 38, 44, 39],
  "nextSeq": 1
}
```

`fields` ausente no ack significa projeção completa. `replay` no ack ecoa se o reenvio automático ao reconectar está ligado para esta conexão. Falha devolve `{ "type": "reject", "reason": "unauthorized|origin|protocol|logon" }` e fecha. Um segundo Logon do mesmo `ClientId` derruba o socket anterior e continua o `seq`. Até o ack, a sessão não recebe execução.

**Projeção.** Com ao menos uma sessão logada, o consume loop tokeniza o body uma vez: usa o `FixMessage` quando o ingresso veio em `Parse`; no `PassThrough`, tokeniza o frame cru aqui. Para cada sessão, monta o envelope e os campos selecionados e faz `TryWrite` na fila daquela sessão. A escrita no socket é outra task. O loop do dispatcher não espera o browser.

Envelope, sempre presente, fora do filtro: `type=exec`, `seq` da sessão, `msgType` (tag 35) e `ts` (timestamp de ingresso). Valores de campo continuam a string ASCII do FIX, sem conversão numérica, nos dois encodings. Sem filtro, a ordem dos campos é a ordem do wire. Frame maior que `MaxOutboundFrameBytes` é descartado só naquela sessão.

JSON, frame de texto:

```json
{
  "type": "exec",
  "seq": 42,
  "msgType": "8",
  "ts": 638000000000000000,
  "fields": { "11": "ORD1", "55": "PETR4", "54": "1", "38": "100" }
}
```

Binário, frame binário, little-endian, os mesmos campos:

```
u8  type            1 = exec, 2 = gap, 3 = heartbeat
u64 seq
u64 ts
u8  msgTypeLen
    msgType bytes
u16 fieldCount
    repetido: u16 tag, u16 valueLen, value bytes
```

`json` é o formato que a página consome direto. `binary` é o mesmo conteúdo em menos bytes, para cliente que lê `ArrayBuffer`. O filtro encolhe os dois: uma tela que só mostra símbolo, lado, quantidade e status não recebe o body inteiro.

**Liveness e entrada.** O servidor manda ping no `HeartbeatIntervalSeconds` e encerra após `MissedHeartbeatLimit` pongs ausentes. O cliente só pode enviar `logon` (uma vez), `heartbeat` e `replay`. Outro frame encerra a sessão. v1 é drop copy: o WebSocket não aceita ordem nova.

**Replay.** Mesmo log de sessão do FIX (§3.4.3). Não há opt-in: o log grava tudo. O índice daquele `ClientId` mapeia o `seq` do envelope para o registro no log de entrada. O pedido `replay` lê desse índice e projeta no consume loop com o encoding e o filtro de campos da conexão atual. Um reconnect com outro `encoding` ou outro `fields` muda a projeção do tráfego recuperado, porque o que fica guardado é o frame de entrada, não o já projetado.

Pedido, texto JSON: `{ "type": "replay", "fromSeq": 10, "toSeq": 20 }`. `toSeq` 0 significa até o último `seq` enviado. A resposta segue a ordem do `seq`, misturando `exec` retido e `gap`. `fromSeq` e `toSeq` do pedido são inclusivos. No `gap`, `fromSeq` é inclusivo e `toSeq` é exclusivo (o próximo `seq` que existe ou o fim do intervalo):

```json
{ "type": "gap", "fromSeq": 4, "toSeq": 6 }
```

`seq` sem registro no intervalo (fora da época atual, ou antes do início do log retido) vira um frame `gap` só naquele trecho.

---

### 3.4 Cold Path / Audit Tier

Um `AuditWriterService` (implementando `IHostedService`) possui um `Channel<AuditRecord>` com capacidade limitada (configurável, ex.: 65.536 itens). Drena o channel via `ReadAllAsync` e grava registros em lotes nos sinks registrados (`IAuditSink`). O tamanho do lote e o intervalo de flush são configuráveis.

O hot path apenas chama `auditChannel.Writer.TryWrite(record)` — não bloqueante. A cópia do payload FIX para `AuditRecord.RawFix` (quando `PersistMode=Full` ou amostra em `Sampled`) ocorre **depois** do fan-out, ainda na thread da sessão, mas apenas um `ArrayPool.Rent` + memcpy + `TryWrite`. Sem SQL, sem socket de banco, sem `await`. Um registro descartado por channel cheio é rastreado via `System.Diagnostics.Metrics` e nunca pode afetar o roteamento.

**Sinks v1:**

1. **`SerilogAuditSink`** — metadados estruturados (ciclo de vida, drops, erros de dispatcher). Não é o store de mensagens.
2. **`PostgresAuditSink`** — persistência canônica das mensagens FIX recebidas via `Npgsql` `COPY` binário (ou batch insert) na hypertable `ingress_fix_messages`.

#### 3.4.1 Por que PostgreSQL + TimescaleDB

Antes desta revisão o cold path só previa Serilog em arquivo (ou um “banco de série temporal” sem produto). Isso **não salvava as mensagens FIX** de forma consultável.

O padrão de dados é append-only, indexado por tempo (`IngressTimestamp`), com consultas do tipo “sessão S no intervalo T”, retenção por idade e ingestão em lote. PostgreSQL puro com uma tabela heap cresce sem bound, o VACUUM compete com o ingest e particionamento manual por dia vira operação. **TimescaleDB** (extensão no mesmo PostgreSQL) resolve isso com:

- **Hypertables** particionadas por `ingress_ts` (chunks diários no default).
- **Compression** nativa de chunks frios (política, ex.: 7 dias).
- **Retention** declarativa (ex.: 90 dias) sem job externo de `DELETE`.
- **Índices** `(session_id, ingress_ts)` e `(cl_ord_id, ingress_ts)` por chunk.

Timescale **é necessário** para este store — não como produto separado, e sim como extensão obrigatória da instância PostgreSQL. A imagem Docker `timescale/timescaledb` *é* PostgreSQL 16 com a extensão. Em AWS o equivalente é RDS PostgreSQL + Timescale (onde licenciado) ou Timescale Cloud / EC2 self-managed com a mesma extensão. O cliente .NET é só `Npgsql`; não há cliente Timescale específico.

**Orçamento de ingestão:** 50k msg/s × ~300 B ≈ 15 MB/s; 200k msg/s ≈ 60 MB/s de payload. Um único Timescale com `COPY` em lote de 1–5k linhas sustenta o laboratório e certificação. Em produção no teto de throughput, `PersistMode=MetadataOnly` ou `Sampled` evita transformar o banco no gargalo; o Kafka continua sendo o bus de alto volume. Persistência nunca é entrega — é auditoria/consulta.

#### 3.4.2 Schema (`ingress_fix_messages`)

```
ingress_ts        timestamptz NOT NULL
session_id        text        NOT NULL
msg_type          text
cl_ord_id         text
msg_seq_num       bigint
delivery_mode     text        NOT NULL
dispatcher_ids    text[]
routing_sequence  bigint
raw_fix           bytea       -- NULL quando MetadataOnly
was_dropped       boolean     NOT NULL DEFAULT false
drop_reason       text
```

Hypertable em `ingress_ts`, `chunk_time_interval = 1 day`. Script de bootstrap em `docker/postgres/init.sql`.

#### 3.4.3 Retransmissão sobre o log de sessão em arquivo

> **Revisão de 2026-11-06 (`notes/backlog-sessao-fix.md`).** A versão anterior desta seção guardava o replay por cliente na hypertable `client_replay_messages`, com opt-in diário. Ela foi substituída por um log de sessão em arquivo que grava **todas** as mensagens antes do fan-out. A hypertable não será criada; o PostgreSQL + TimescaleDB fica só com a auditoria (`ingress_fix_messages`).

**Por que arquivo, e não a hypertable:** a recuperação de sessão é o caminho de que a entrega ao vivo depende. A gravação em banco é assíncrona e, se virasse síncrona, acrescentaria milissegundos e uma dependência externa ao caminho que menos pode ter as duas. O log em arquivo é append sequencial no page cache, na casa dos microssegundos, e sobrevive a crash de processo sem o banco de pé. O Timescale continua servindo a auditoria e o histórico de longo prazo, onde a latência não importa.

**Formato.** Segmentos append-only por stream de ingress, rotacionados por dia de pregão e por época (um reset de seqnum ou a virada de pregão começa uma época nova). Cada segmento tem:

- `.log` — registros `[len][seq][ts][crc][payload]`, com o frame como entrou.
- `.index` denso — como o seqnum FIX é contíguo, a entrada `seq - base` guarda o offset do registro. Busca O(1), 8 bytes por mensagem (cerca de 4 MB por dia a 500 mil mensagens; ver `notes/backlog-sessao-fix.md` §1). Não há busca binária nem varredura.

O segmento tem tamanho fixo (`SegmentSizeMB`) e é pré-alocado. Se encher antes da virada do dia, abre outro segmento do mesmo dia e época. O próximo segmento é pré-alocado e tocado (page-in) por uma task de background, para que a troca no hot path seja só a troca de um ponteiro. Se a task não terminou a tempo, a troca é síncrona e conta em `sessionlog_sync_rotation_total`.

Um escritor único por stream grava sem lock, em arquivo mapeado em memória. Na abertura, o CRC do último registro valida o fim do arquivo e um registro cortado por crash é descartado.

**O frame gravado é o original.** O log guarda o frame como o counterpart enviou, antes do `PassThroughRewriter` (que reescreve o frame in-place). Por isso o append acontece na sessão de ingress logo após o `TryDecode`, e não dentro do fan-out. A sessão extrai o `MsgSeqNum` (tag 34) do frame decodificado para ser o `seq de entrada`.

**O payload fica gravado uma vez.** Cada cliente tem só um índice `seq do cliente -> posição no log de entrada` (arquivo `.cidx`, uma entrada de 16 bytes por seqnum: `kind u8`, `flags u8`, `stream u16`, `segment u32`, `offset u64`; cerca de 8 MB por cliente por dia a 500 mil mensagens). `kind` é `1` (aplicação, aponta para o log de entrada), `2` (administrativa gerada para este cliente: Heartbeat, TestRequest, Logon etc.; sem payload, vira gap-fill) ou `0` (slot ainda não publicado). N clientes não geram N cópias do payload (resolve `notes/backlog-sessao-fix.md` §1). O estado por consumidor (próximo seqnum de saída, próximo seqnum de entrada esperado, época e cursor por stream) fica num arquivo pequeno, com flush por mensagem ou por intervalo (padrão de 5 s), configurável.

**O fan-out recebe a referência do log.** Channel do dispatcher cheio e rota sem dispatcher deixam de descartar: viram lag recuperável pelo cursor. A métrica de lag por dispatcher substitui a de descarte por channel cheio.

**Cliente FIX.** A chave do cliente é `DispatcherId` + `SenderCompID` (tag 49 do Logon). Não há cadastro nem UUID: o `.cidx` e o estado do cliente são criados no primeiro Logon aceito (§3.4.6). Não há opt-in: o log grava tudo. No ResendRequest, o servidor lê o índice daquele cliente entre `BeginSeqNo` e `EndSeqNo` e aplica a regra de reenvio: mensagem de aplicação com `PossDupFlag=Y` (43) e `OrigSendingTime` (122); mensagem administrativa como `SequenceReset-GapFill` agrupado. Respeita `EndSeqNo` e o limite de 10.000 por pedido (`PossMissingApplMsg=Y` acima disso).

**Cliente de broker.** Kafka e RabbitMQ não identificam o assinante na entrega. A entrega é ao menos uma vez: cada mensagem leva um header de identidade `<sessão de origem>:<época>:<seq de entrada>` (record header no Kafka, header de mensagem no RabbitMQ) e o consumidor deduplica por ele. O cursor do dispatcher avança conforme `CursorAckMode`: `OnBrokerAck` (padrão, após a confirmação do broker) ou `OnEnqueue` (no envio). `Acks=0` no Kafka só combina com `OnEnqueue`. No restart, o dispatcher retoma do cursor e reentrega do log. Não há API HTTP de replay: o cursor persistido cobre a recuperação (decisão de 2026-11-08, T-47). Leitura sob demanda por assinante, se surgir, entra como task nova, lendo do log.

**Cliente WebSocket.** O cadastro liga o UUID ao dispatcher e a `TokenEnvVar`. A recuperação é o pedido `replay` na própria sessão, lendo o índice daquele `ClientId` no log. O que fica guardado é o frame que entrou no log de entrada; a projeção (encoding e filtro de campos da conexão) acontece no consume loop, nunca no hot path. `seq` sem registro no intervalo vira um frame `gap` só nesse trecho.

#### 3.4.4 Escritores, visibilidade e transição entre channel e log

**Quem escreve cada estrutura.** Cada estrutura tem um dono. Onde há mais de um escritor, eles gravam em posições distintas.

| Estrutura | Escritor | Leitores | Sincronização |
|---|---|---|---|
| `.log` e `.index` de um stream de ingress | Thread da sessão de ingress (a que executa `Route()`). Um escritor por stream. | Tasks de consumo (catch-up) e sessões de reenvio | Sem lock. Grava registro, grava entrada de índice e só então publica `committed` com `Volatile.Write`. O leitor lê até o `committed` (`Volatile.Read`). |
| `.cidx`, entrada de aplicação | Task de consumo do dispatcher (`SendAsync` tem um consumidor só) | Sessão do cliente (reenvio) | O `seq` do cliente vem de `Interlocked.Increment` no contador do cliente e define o slot `seq - base`. |
| `.cidx`, entrada administrativa | Task da sessão aceita (`FixAcceptedSession`: Heartbeat, TestRequest, Logon, GapFill) | Idem | Mesmo contador atômico, slots distintos. |
| Estado por consumidor | Task flusher do `SessionLogManager` | Abertura | Os donos publicam valores `long` com `Volatile.Write`; só o flusher escreve no arquivo. |
| Cursor de broker | Task de consumo do dispatcher de broker | Flusher | Marca-d'água contígua (ver abaixo). |

**Publicação de slot do índice por cliente.** Dois escritores (consumo e sessão) podem terminar fora de ordem. O escritor grava `stream`, `segment` e `offset` e por último `kind`, com `Volatile.Write`. O leitor que encontra `kind = 0` para e trata o slot como "ainda não disponível". Nunca interpreta como buraco. Só um slot que o contador já passou e continua em `0` depois de `SlotPublishTimeoutMs` vira gap.

**Cliente FIX conhecido, sem sessão logada.** Cliente "conhecido" é todo `SenderCompID` que já teve um Logon aceito naquele dispatcher e, por isso, tem `.cidx` e estado em disco. A task de consumo do dispatcher reserva o `seq` e grava a entrada de aplicação no índice de cada cliente conhecido, mesmo sem sessão logada, e só envia se a sessão existe. Assim o `seq` do cliente continua contíguo enquanto ele está offline, e o Logon seguinte revela o buraco, que o cliente pede de volta. Um `SenderCompID` que nunca conectou não tem índice e não recebe nada retroativo: o primeiro Logon dele começa em `seq` 1. Ao reiniciar, o `SessionLogManager` reabre os clientes conhecidos varrendo `clients/{DispatcherId}/`.

**Transição channel ↔ log.** Cada `DispatchPayload` carrega a referência `(stream, época, seq de entrada)`. A task de consumo guarda `expected[stream]`, o próximo seq de entrada que ela ainda não entregou:

| Situação | Ação |
|---|---|
| `seq == expected` | Entrega e faz `expected++`. |
| `seq > expected` | Buraco no channel (`TryEnqueue` recusou ou o consumidor ficou parado). Entra em catch-up: lê do log do stream o intervalo `[expected, seq)`, entrega em ordem e depois entrega o payload atual. |
| `seq < expected` | O catch-up já entregou esta mensagem. Libera o buffer sem entregar. |
| `TryEnqueue` recusa (channel cheio) | Libera o buffer, conta lag e segue. A mensagem está no log. |
| Sem tráfego depois de uma recusa | A task compara `expected[stream]` com o `committed` do stream a cada `CatchUpIdleMs` e entra em catch-up se houver atraso. O mesmo vale no restart. |
| Época nova no payload | A task drena a época anterior do log até o `committed` dela e só depois passa a esperar a época nova. |

O catch-up roda na task de consumo, nunca na thread da sessão. Lê só até o `committed`. A ordem dentro de um stream é preservada. Entre streams a ordem é a de chegada à task, como já é hoje.

**Cursor de broker.** O cursor de `OnBrokerAck` é uma marca-d'água contígua: o dispatcher guarda os envios em voo por `(stream, seq)` e o cursor só avança até o maior seq tal que todos os anteriores foram confirmados. Confirmação fora de ordem fica pendente. `OnEnqueue` avança o cursor quando entrega à biblioteca do cliente. O cursor persistido nunca fica adiante do que foi entregue, então um restart reentrega e a entrega é ao menos uma vez.

**Reenvio FIX.** Roda na task da sessão aceita, fora do hot path, e usa o mesmo `Prepare` do envio normal, com `PossDupFlag`. Em `PassThrough`, o `PassThroughRewriter` (com crescimento, TD-02) reescreve a tag 34 para o seq do cliente, insere `43=Y` e `122` (o `52` original), atualiza o `52` e recalcula o `10`. Em `Parse`, o `FixMessageSerializer` reserializa a partir do frame lido do log. O reenvio e o tráfego ao vivo escrevem na mesma fila de saída da sessão, que tem um único escritor de socket. Mensagem nova com seq maior pode sair entre as reenviadas, o que o FIX permite.

**Falha de escrita.** Erro no append (disco cheio, I/O) devolve `AppendResult.Failed`. O roteamento segue, a entrega ao vivo não para, o stream entra em estado degradado com métrica e alerta, e o trecho sem gravação sai como `gap` num pedido de reenvio.

#### 3.4.5 Recuperação após queda e política de seqnum

O `SessionLogManager` grava o marcador `clean` no shutdown gracioso, depois do flush final, e o remove ao abrir. Na abertura:

| Caso | Ação |
|---|---|
| Marcador presente | Usa o estado como está. |
| Marcador ausente (queda do processo ou do SO) | (1) Cada stream valida o CRC do último registro, descarta o registro cortado e recalcula o `committed`. (2) Cada índice por cliente descarta slots depois do último registro válido. (3) O seq de saída de cada consumidor retoma de `max(seq persistido, último slot publicado no .cidx + 1) + UncleanRestartSeqMargin`. (4) O cursor de broker retoma do valor persistido, que pode estar atrás: reentrega, nunca perde. (5) O seq de entrada esperado retoma do valor persistido. Se estiver atrás, o counterpart reenvia com `PossDupFlag` e a validação de entrada (TD-06, TD-07) trata a duplicata. |

O princípio é que o seq de saída pode saltar, mas nunca voltar. Saltar é permitido pelo FIX: o cliente pede o intervalo, recebe gap-fill e segue. Voltar repetiria seqnum já entregue. Com `FlushMode: PerMessage` a margem vale `0`. O initiator para a B3 usa a mesma regra de leitura; se a B3 aceita salto de seqnum no `Logon` é uma pergunta aberta do TD-03 e do TD-05.

#### 3.4.6 Layout de arquivos e reconciliação de seq

**Dois domínios isolados.** O ingress (B3 → router) grava o log de entrada e o controle do stream. Os clientes de saída têm os próprios arquivos. A escrita dos clientes é assíncrona em relação ao hot path (§3.4.4).

```
{SessionLog.Directory}/
├── clean                                   # marcador de shutdown limpo (arquivo vazio)
├── streams/
│   └── {StreamId}/                         # StreamId = SessionId da sessão de ingress
│       ├── state.json                      # controle com a B3: próximo seq enviado, próximo esperado, época
│       └── {yyyyMMdd}-e{epoch:D3}/         # dia de pregão + época
│           ├── 00000000.log                # segmento 0: registros [len][seq][ts][crc][payload]
│           ├── 00000000.index              # índice denso do segmento: seq - base → offset
│           ├── 00000001.log                # só se o segmento encher antes da virada do dia
│           └── 00000001.index
├── clients/
│   └── {DispatcherId}/
│       └── {ClientKey}/                    # FIX: SenderCompID validado. WebSocket: ClientId (UUID)
│           ├── state.json                  # próximo seq de saída, próximo seq de entrada esperado, época
│           └── {yyyyMMdd}-e{epoch:D3}.cidx # índice do cliente no dia e na época, slots de 16 bytes
└── brokers/
    └── {DispatcherId}/
        └── cursor.json                     # cursor por (stream, época), CursorAckMode
```

| Item | Regra |
|---|---|
| `state.json` | JSON legível, gravado pelo flusher (§3.4.4), por escrita em arquivo temporário + rename atômico. Campos: `nextOut`, `nextInExpected`, `epoch`, `updatedAt`. |
| `.log`, `.index`, `.cidx` | Binários, mapeados em memória, tamanho fixo pré-alocado (`SegmentSizeMB`, `IndexSlotsPerSegment`). |
| `clean` | Criado no shutdown limpo depois do flush final, removido na abertura (§3.4.5). |
| Retenção | Apagar pastas `{yyyyMMdd}-e{epoch}` e `.cidx` mais antigos que `RetentionTradingDays`. `state.json` e `cursor.json` não são apagados. |

**Nome de pasta vem de entrada de rede.** `SenderCompID` não vai direto ao caminho. A validação aceita só `[A-Za-z0-9_-]`, de 1 a 64 caracteres. Qualquer outro valor recusa o Logon. Caminhos são montados com `Path.Combine` e confirmados dentro de `SessionLog.Directory` após `Path.GetFullPath`. Valor com `..`, separador, caractere de controle ou nome reservado do Windows (`CON`, `NUL` etc.) é recusado.

**Criação de arquivos só depois de autenticar.** O `.cidx` e o `state.json` de um cliente FIX só são criados após o Logon ser aceito (credenciais do TD-05 verificadas). Como o acceptor hoje só valida o `TargetCompID`, o TD-05 é pré-requisito do TD-07 e deste layout em produção. `FixOutboundDispatcherOptions.MaxClients` (padrão 64) limita quantos `SenderCompID` distintos um dispatcher cria por instância. Passado o limite, o Logon é recusado com `Logout` e métrica `sessionlog_clients_rejected_total`.

**Reconciliação de seq no Logon e no ResendRequest.** `N` é o seq que o cliente diz esperar; `M` é o `nextOut` do servidor. O servidor sabe o que gravou no socket, não o que o cliente recebeu, por isso quem declara a falta é o cliente.

| Situação | Ação |
|---|---|
| `N == M` | Em dia. Segue o fluxo normal. |
| `N < M` | Cliente atrasado. Reenvia `N..M-1` pelo `.cidx` (aplicação com `43=Y` e `122`; administrativa em gap-fill). |
| `N > M` | O cliente espera um seq que o router nunca numerou (seq inexistente). Conforme o dicionário FIX (processamento da `789`): `Logout` com `Text` e fecha. O router **não** adota o `N` do cliente. Registra `WARN` e `acceptor_client_ahead_total`. Isso pode acontecer após queda do router; a margem `UncleanRestartSeqMargin` (§3.4.5) existe para evitar. O cliente se recupera com `141=Y` (modo 1). |
| Slot ausente ou `kind = 0` | Espera até `SlotPublishTimeoutMs`; depois vira gap-fill. |
| Intervalo acima de 10.000 | Atende em lotes, com `PossMissingApplMsg=Y`. |

**Como o router conhece `N`.** O `34` do Logon do cliente é o seq de saída dele, não o que ele espera receber. O acceptor lê `N` da tag `789` (NextExpectedMsgSeqNum, FIX 4.4) do Logon, ou do `BeginSeqNo` de um `ResendRequest`. A `789` é opcional e depende de acordo bilateral (dicionário FIX 4.4, Logon e "Logon Message NextExpectedMsgSeqNum processing"). Sem `789`, o acceptor responde o Logon com o seu `M` e o cliente pede o que falta por `ResendRequest`. Um `ResendRequest` com `BeginSeqNo >= M` também é "seq inexistente" e leva a `Logout`.

**Dois "à frente" diferentes.** O `34` que o cliente **envia** acima do esperado é tolerado (política mista, §3.4.7). O seq que o cliente **espera receber** (`789` ou `BeginSeqNo`) acima do que o router numerou é recusado com `Logout`.

#### 3.4.7 Política de sessão do acceptor (clientes do router)

As regras entre o router e seus clientes são nossas e configuráveis. O initiator para a B3 segue outro regime (último parágrafo).

**Modos de conexão.** Três modos de conexão e uma recusa. O cliente escolhe pela forma do Logon. O operador escolhe o comportamento do servidor por configuração.

| # | Modo | O cliente pede | O router faz |
|---|---|---|---|
| 1 | Começar do zero | Logon com `141=Y` e `34=1` | Se `AllowResetSeqNumFlag`: abre nova época do cliente (novo `.cidx`), `nextOut = 1`, `nextInExpected = 2`, responde com `141=Y`. As mensagens da época anterior ficam arquivadas e não são mais reenviáveis. Se não permitido: `Logout`. |
| 2 | Retomar de onde parou | Logon normal. Com `789 = N`, a recuperação é automática | Com `789`: o Logon de resposta traz a `789` do router (próximo `34` que ele espera do cliente), o router reenvia `N..M-1` pelo `.cidx` com `PossDupFlag=Y`, depois um `SequenceReset-GapFill` sobre o seq usado pelo Logon, e segue com mensagens novas. Sem `789`: responde o Logon com o seq `M` e atende o `ResendRequest` do cliente. Nenhum dos lados gera `ResendRequest` a partir do `34` do Logon (dicionário FIX). |
| 3 | Retomar do ponto atual do router | Logon normal | Com `OnReconnectGap = SkipToCurrent`: responde o Logon e envia `SequenceReset-GapFill` com `34 = N` (início do intervalo) e `NewSeqNo` no seq corrente do router, sem reenviar aplicação. As mensagens continuam no `.cidx`. A ordem exata em relação ao seq do Logon de resposta é detalhe do TD-07, validado contra o QuickFIX. |
| 4 | Cliente espera um seq que não existe (`N > M`) | `789 = N` ou `ResendRequest` com `BeginSeqNo >= M` | `Logout` com `Text` e fecha, sem adotar o `N` do cliente. Dicionário FIX: `789` acima do próximo seq a atribuir leva a `Logout`. |

**Logout.** Ao recusar, o router envia `Logout` com `Text`, espera o `Logout` de confirmação por `LogoutTimeoutSeconds` e só então fecha o socket. Depois de enviar o `Logout`, só responde a `ResendRequest` (dicionário FIX, Logout). Isso dá ao cliente a chance de pedir gap-fill antes de cair.

**`SequenceReset-Reset` não é usado.** O dicionário o restringe a recuperação de desastre e só permite aumentar o seq. O acceptor usa só o modo `GapFill`.

**Política do `34` recebido do cliente (seq de entrada): mista.**

| Chegou | Ação |
|---|---|
| `34 > nextInExpected` | Tolerante: aceita, avança `nextInExpected` para `34 + 1`, registra `WARN` e a métrica `acceptor_in_seq_gap_total`. |
| `34 < nextInExpected` com `PossDupFlag=Y` | Ignora a duplicata. |
| `34 < nextInExpected` sem `PossDupFlag` | Estrita: `Logout` com `Text = "MsgSeqNum too low"`, espera a confirmação por `LogoutTimeoutSeconds` e fecha. |
| Logon com `141=Y` | Redefine `nextInExpected` (modo 1). |
| `ResendRequest` do cliente | Mesmo no caso `34 >` acima, o `ResendRequest` é sempre processado, porque ele é a recuperação que o cliente pediu. |

**Opções (`FixOutboundDispatcher`).** Valem para o dispatcher e podem ser sobrescritas por `SenderCompID` em `ClientOverrides`:

| Opção | Valores | Padrão |
|---|---|---|
| `Recovery.OnReconnectGap` | `WaitForResendRequest`, `SkipToCurrent` | `WaitForResendRequest` |
| `Recovery.AllowResetSeqNumFlag` | `true`, `false` | `true` |
| `Recovery.AcceptNextExpectedMsgSeqNum` | `true`, `false` | `true` (usa a `789` quando o cliente a envia) |
| `Recovery.RequireNextExpectedMsgSeqNum` | `true`, `false` | `false` (`true` recusa Logon sem `789`) |
| `Recovery.LogoutTimeoutSeconds` | inteiro | `5` |

O router nunca adota um seq maior declarado pelo cliente, então o `.cidx` denso não cresce por pedido do cliente.

**Initiator (NexusRouter como cliente da B3 ou de outra fonte).** Segue o comportamento padrão do QuickFIX: persiste os dois seqs, envia o Logon com o seq esperado, `ResendRequest` quando o `34` recebido é maior, `Logout` quando é menor sem `PossDupFlag`, e `141=Y` só quando configurado ou quando o dia vira. Onde a spec da B3 diverge do QuickFIX (resultado do TD-03), vale a B3. Não há política tolerante nem `SkipToCurrent` no initiator.

---

## 4. Principais Abstrações e Interfaces

### 4.1 `IMessageDispatcher`

```
IMessageDispatcher
├── Propriedades
│   ├── DispatcherId : string          // Nome único, usado em config de roteamento e registros de auditoria
│   └── IsHealthy    : bool            // Flag de saúde em tempo real, legível sem lock
├── Métodos
│   ├── StartAsync(CancellationToken) : ValueTask
│   ├── StopAsync(CancellationToken)  : ValueTask
│   └── TryEnqueue(DispatchPayload)   : bool    // Enqueue não bloqueante; retorna false se o channel estiver cheio
└── Eventos
    └── OnDispatchError : event        // Disparado em falha de publicação (consumido pelo audit writer)
```

**Justificativa do design:** `TryEnqueue` retorna `bool` em vez de `ValueTask<bool>` para manter o ponto de chamada no hot path estritamente síncrono e livre de alocações. O I/O async real para o broker downstream ocorre dentro do consume loop de background privado do dispatcher, completamente desacoplado do hot path.

### 4.2 `ISessionFrameDecoder`

```
ISessionFrameDecoder
└── TryDecode(ref ReadOnlySequence<byte> buffer, out IngressFrame frame) : bool
    // Tenta decodificar um frame completo do buffer.
    // Avança o buffer além dos bytes consumidos em caso de sucesso.
    // Retorna false se o buffer ainda não contiver um frame completo.
    // Zero allocation: IngressFrame é um ref struct contendo um slice do buffer do pipe.
```

### 4.3 `IngressFrame` (ref struct)

```
IngressFrame (ref struct)
├── SessionId        : SessionId           // Value type — sem alocação heap
├── RawBuffer        : ReadOnlySequence<byte>  // Slice no buffer do PipeReader
├── IngressTimestamp : long                // Stopwatch.GetTimestamp ticks; Serilog publica µs
└── FrameLength      : int
```

### 4.4 `DispatchPayload`

```
DispatchPayload
├── SessionId        : SessionId
├── DeliveryMode     : DeliveryMode        // PassThrough | Parse
├── RawBuffer        : IMemoryOwner<byte>  // Alugado do ArrayPool; dispatcher responsável pelo dispose
├── ParsedMessage    : FixMessage?         // Não nulo apenas quando DeliveryMode == Parse
├── IngressTimestamp : long                // Stopwatch.GetTimestamp ticks; Serilog publica µs
├── EgressTimestamp  : long                // Stopwatch.GetTimestamp ticks; Serilog publica µs
└── RoutingSequence  : long               // Sequência interna para ordenação/dedup
```

**Regra de ownership do buffer:** O router aluga um buffer de `ArrayPool<byte>`, copia o segmento do pipe alugado nele e o encapsula em um `IMemoryOwner<byte>` (usando um struct leve `ArrayPoolMemoryOwner<byte>`). O ownership é transferido para o `DispatchPayload`. O consume loop de cada dispatcher é responsável por chamar `Dispose()` no `IMemoryOwner` após a entrega. Em cenários de fan-out, o buffer é contado por referência usando `Interlocked` — todos os dispatchers recebem o mesmo buffer alugado; o último a chamar `Dispose` o devolve ao pool.

### 4.5 `FixMessage` (ref struct)

```
FixMessage (ref struct)
├── MsgType          : ReadOnlySpan<byte>
├── Fields           : ReadOnlySpan<FixField>   // Stack-allocated, backed by stackalloc ou array poolizado
└── TryGetField(int tag, out ReadOnlySpan<byte> value) : bool
```

### 4.6 `RouteContext`

```
RouteContext (readonly record struct)
├── SessionId        : SessionId
├── DeliveryMode     : DeliveryMode
└── Dispatchers      : ImmutableArray<IMessageDispatcher>
```

Armazenado em um `FrozenDictionary<SessionId, RouteContext>` no `RouterEngine`. O dicionário é construído uma única vez na inicialização e nunca é mutado durante a operação normal.

### 4.7 `IAuditSink`

```
IAuditSink
├── Name : string
├── StartAsync(CancellationToken) : ValueTask
├── WriteBatchAsync(ReadOnlyMemory<AuditRecord> batch, CancellationToken) : ValueTask
└── StopAsync(CancellationToken) : ValueTask
```

`AuditWriterService` não conhece Serilog nem Npgsql — apenas `IReadOnlyList<IAuditSink>`. Falha em um sink não aborta os demais.

### 4.8 Log de sessão: abstrações e classes

As interfaces que o hot path e os dispatchers usam ficam em `NexusRouter.Domain`. As implementações ficam no novo projeto `NexusRouter.SessionLog`. O Router e o Egress não referenciam o projeto de implementação.

```
ISessionLogStream (Domain)                       -- um por sessão de ingress; escritor único
├── StreamId       : ushort
├── Epoch          : int
├── Committed      : long                        -- Volatile.Read; último seq de entrada legível
├── Append(ReadOnlySpan<byte> frame, long inSeq, long ingressTs) : AppendResult
│       AppendResult = { Ok(LogRef) | Failed }   -- nunca lança; zero alloc
└── BeginEpoch(int epoch, long baseSeq) : void   -- reset 141=Y ou virada de pregão

LogRef (readonly record struct, Domain)          -- vai no DispatchPayload
├── Stream : ushort   ├── Epoch : int   └── InSeq : long

ISessionLogReader (Domain)                       -- leitores: catch-up, reenvio
├── TryRead(ushort stream, int epoch, long inSeq, IBufferWriter<byte> dest) : bool
└── GetCommitted(ushort stream, int epoch) : long

IClientIndex (Domain)                            -- um por cliente conhecido (.cidx)
├── ClientKey      : string                      -- FIX: DispatcherId + SenderCompID; WebSocket: ClientId (UUID)
├── NextOut        : long                        -- Volatile.Read
├── ReserveSeq()   : long                        -- Interlocked.Increment
├── PublishApp(long seq, LogRef ref) : void      -- grava campos e por último kind=1
├── PublishAdmin(long seq) : void                -- kind=2
└── TryGet(long seq, out IndexEntry entry) : IndexReadResult   -- Published | NotYet | Missing

IConsumerState (Domain)                          -- um por dispatcher e por cliente
├── NextOut / NextInExpected / Epoch : long      -- Volatile
├── GetExpected(ushort stream) / SetExpected(ushort stream, long inSeq)
└── Cursor(ushort stream) : long                 -- brokers
```

| Classe | Projeto | Responsabilidade |
|---|---|---|
| `SessionLogManager` (`IHostedService`) | SessionLog | Dono do diretório. Abre e recupera (§3.4.5), cria streams, índices e estados, roda a task flusher, a task de pré-alocação/rotação e a retenção por dia de pregão. Grava o marcador `clean`. |
| `SessionLogStream` | SessionLog | Implementa `ISessionLogStream`. Mantém o segmento corrente e o próximo, pré-alocado. |
| `LogSegment` (internal) | SessionLog | Arquivo mapeado em memória. Layout do registro `[len][seq][ts][crc][payload]` e CRC. |
| `ClientIndex` (`.cidx`) | SessionLog | Implementa `IClientIndex`. Slots de 16 bytes, publicação por `kind`. |
| `ConsumerStateStore` | SessionLog | Implementa `IConsumerState`. Arquivo pequeno, gravado pelo flusher. |
| `StreamCatchUp` | Egress | Tabela `expected[stream]`, regras da transição channel↔log, timer ocioso. Usado pelo `DispatcherBase`. |
| `BrokerCursorTracker` | Egress | Marca-d'água contígua dos envios em voo para `OnBrokerAck`. |
| `ResendPlanner` | Egress/Fix | Dado `[BeginSeqNo, EndSeqNo]`, lê o `IClientIndex` e devolve a sequência de ações: reenviar aplicação (`LogRef`), gap-fill agrupado (administrativas e buracos), `NotYet` (espera), com o limite de 10.000 mensagens por pedido. |
| `SessionLogOptions` | Host | Seção `SessionLog` da §6. |

Alterações no código existente: `DispatchPayload` ganha `LogRef`; `FixSession` chama `ISessionLogStream.Append`; `DispatcherBase` ganha `StreamCatchUp` e a métrica de lag; `FanOutEngine` deixa de descartar por rota vazia ou channel cheio; `FixAcceptedSession` e `WebSocketAcceptedSession` trocam o `SequenceNumberStore` por `IClientIndex` + `IConsumerState`; os dispatchers Kafka e RabbitMQ ganham o header de idempotência, o `BrokerCursorTracker` e `CursorAckMode`. A lista completa por arquivo está na §7.

---

## 5. Modelo de Concorrência

### 5.1 Topologia de Threads

| Componente | Modelo de Thread |
|------------|-----------------|
| `FixSession` (por sessão) | Thread dedicada de longa duração (`TaskCreationOptions.LongRunning`). Uma thread por sessão para o loop de leitura do Pipe. |
| `RouterEngine.Route()` | Chamado na thread dedicada da sessão — sem despacho cross-thread para o hot path em si. |
| Consumidores de `IMessageDispatcher` | Cada dispatcher tem sua própria `Task` de background no thread-pool, consumindo do seu `Channel`. |
| `AuditWriterService` | Única `Task` de background no thread-pool. |
| `ISessionLogStream.Append` | Na própria thread da sessão de ingress, logo após o `TryDecode`. Um escritor por stream. Cópia em memória mapeada, sem I/O de disco síncrono. |
| `SessionLogManager` (flusher, pré-alocação/rotação, retenção) | Uma `Task` de background para o flush, outra para pré-alocar o próximo segmento e rotacionar, e a retenção roda no startup e na virada do dia. |
| Catch-up do dispatcher | Na task de consumo do próprio dispatcher. Nunca na thread da sessão. |
| Reenvio de `ResendRequest` | Na task da sessão aceita (`FixAcceptedSession`). |

Essa topologia significa que o **hot path nunca sofre context-switch**: uma mensagem chega na thread da sessão, é roteada na mesma thread (lookup no FrozenDictionary é somente leitura), sofre `TryWrite` nos channels dos dispatchers (que são internamente filas concorrentes lock-free), e o controle retorna imediatamente ao loop de leitura do pipe. A thread da sessão possui o pipeline desde o recebimento no socket até a conclusão do fan-out.

### 5.2 Garantias Lock-Free

| Recurso Compartilhado | Mecanismo de Sincronização |
|----------------------|---------------------------|
| Tabela de roteamento (`FrozenDictionary`) | Imutável após inicialização — sem necessidade de sincronização |
| Escritas no channel do dispatcher (`TryWrite`) | `System.Threading.Channels` — implementação MPSC lock-free |
| Escritas no channel de auditoria (`TryWrite`) | `System.Threading.Channels` — implementação MPSC lock-free |
| Contadores de sequence number outbound | `Interlocked.Increment` em `long` (`IClientIndex.ReserveSeq`). O estado persistido vem do `IConsumerState`, que substitui o `SequenceNumberStore`. |
| Append no log de sessão | Escritor único por stream (a thread da sessão). Sem lock. Publica o `committed` com `Volatile.Write` depois de gravar registro e índice. |
| Slots do índice por cliente | Dois escritores em slots distintos (consumo e sessão). O `kind` é gravado por último com `Volatile.Write`; `kind = 0` significa não publicado. |
| Estado por consumidor e cursor | Donos publicam com `Volatile.Write`; só o flusher grava em arquivo. |
| Transições de estado de sessão | `Interlocked.CompareExchange` no valor do enum de estado |
| Flag de saúde do dispatcher | `volatile bool` — single writer (thread do dispatcher), múltiplos readers |
| Contagem de referências do buffer (fan-out) | `Interlocked.Decrement` — último decremento para 0 dispara `ArrayPool.Return` |

Nenhum `lock`, `Monitor`, `Mutex`, `SemaphoreSlim` ou `ReaderWriterLockSlim` aparece no hot path. Essas construções são permitidas apenas na inicialização do cold path e em operações administrativas.

### 5.2.1 Fluxo de concorrência do log de sessão

```
Thread da sessão de ingress (escritor do stream)         Task de consumo do dispatcher            Task da sessão aceita
        │                                                        │                                       │
 TryDecode ──► Append(frame, inSeq)                              │                                       │
        │        grava registro + índice                         │                                       │
        │        Volatile.Write(committed)                       │                                       │
        ├─► SessionFSM / Route / rewriter                        │                                       │
        └─► TryWrite(payload com LogRef) ───────────────►  expected[stream] x LogRef.InSeq              │
                                                          ├─ igual: entrega                              │
                                                          ├─ maior: catch-up (ISessionLogReader,         │
                                                          │          lê até committed) e entrega         │
                                                          └─ menor: libera                               │
                                                                 │                                       │
                                                       ReserveSeq + PublishApp(seq, LogRef)              │
                                                                 └─ se há sessão: TryWrite na fila ──►  escreve no socket
                                                                                                         │
                                                          ResendRequest ──► ResendPlanner lê IClientIndex
                                                                            reenvia pela mesma fila de saída
                                                          Heartbeat/Gap-fill ──► ReserveSeq + PublishAdmin
```

Invariantes:

1. Cada stream de ingress tem um único escritor. Nenhum outro código chama `Append` do mesmo stream.
2. Um leitor nunca lê além do `committed` do stream nem de um slot com `kind = 0`.
3. O seq de saída de um cliente nunca é reutilizado nem regride, nem após queda (§3.4.5).
4. O flush e a rotação acontecem em tasks próprias. O caminho normal do hot path não espera disco.
5. Falha do log não derruba o roteamento (§3.4.4, Falha de escrita).
6. O shutdown drena as tasks de consumo, faz o flush final e só então grava o marcador `clean`.

### 5.3 Considerações sobre o Modelo de Memória

- Todas as estruturas de dados do hot path são dimensionadas para caber nas linhas de cache L1/L2 onde possível. `RouteContext` é um struct compacto, e a referência `ImmutableArray<IMessageDispatcher>` é um único deref de ponteiro.
- O array de `FixField` em `FixMessage` usa `stackalloc` para mensagens com ≤ 64 campos (o caso comum); recorre a array poolizado para mensagens maiores.
- `DispatchPayload` é uma classe (alocada no heap) para permitir compartilhamento seguro entre channels. O objetivo de zero-allocation refere-se à lógica de processamento de mensagens, não ao envelope do payload em si — o objeto de payload é obtido de um `ObjectPool<DispatchPayload>` e resetado/reciclado.

---

## 6. Modelo de Configuração

```
NexusRouter:
  Sessions:
    - SessionId: "EXCH_A_ORDEXEC"
      Host: "fix-gateway-a.exchange.com"
      Port: 9876
      SenderCompId: "CLIENT1"
      TargetCompId: "EXCHANGE_A"
      HeartbeatIntervalSeconds: 30
      DeliveryMode: PassThrough        # ou Parse
      FrameDecoder: Fix44              # ou Fix50, Sbe
      TLS:
        Enabled: true
        CertificatePath: "/certs/client.pfx"
        CertificatePasswordEnvVar: "NEXUS_CERT_PWD"
      ReconnectPolicy:
        BaseDelayMs: 500
        MaxDelayMs: 30000
        JitterFactor: 0.2
      Routes:
        - DispatcherId: "kafka-main"
        - DispatcherId: "fix-out-client1"

  Dispatchers:
    # Local / Kafka auto-gerenciado (Docker Compose). App no host usa localhost:19092.
    - Id: "kafka-main"
      Type: Kafka
      BootstrapServers: "localhost:19092"
      Topic: "fix.ordexec.raw"
      ClientId: "nexusrouter"
      Acks: 0
      CursorAckMode: OnEnqueue          # OnBrokerAck (padrão) | OnEnqueue. Acks=0 exige OnEnqueue.
      SecurityProtocol: Plaintext
      SaslMechanism: None
      PartitionStrategy: ByClOrdId
      ChannelCapacity: 16384

    # Exemplo Amazon MSK SASL/SCRAM (produção). Secrets só via env.
    # - Id: "kafka-msk"
    #   Type: Kafka
    #   BootstrapServers: "b-1.xxx.kafka.us-east-1.amazonaws.com:9096,b-2.xxx.kafka.us-east-1.amazonaws.com:9096"
    #   Topic: "fix.ordexec.raw"
    #   ClientId: "nexusrouter"
    #   Acks: 1
    #   CursorAckMode: OnBrokerAck        # ao menos uma vez: cursor avança na confirmação
    #   SecurityProtocol: SaslSsl
    #   SaslMechanism: ScramSha512
    #   SaslUsernameEnvVar: "NEXUS_KAFKA_SASL_USERNAME"
    #   SaslPasswordEnvVar: "NEXUS_KAFKA_SASL_PASSWORD"
    #   EnableSslCertificateVerification: true
    #   PartitionStrategy: ByClOrdId
    #   ChannelCapacity: 16384

    - Id: "fix-out-client1"
      Type: FixOutbound
      ListenAddress: "0.0.0.0"
      ListenPort: 6001
      SenderCompId: "NEXUS"
      BeginString: "FIX.4.4"
      SequenceStoreDirectory: "/var/lib/nexusrouter/seq"   # TD-04: absorvido pelo estado por consumidor do SessionLog
      # AllowedSenderCompIds: ["OMS1", "RISK1"]  # omitido = qualquer SenderCompID cujo TargetCompID seja NEXUS
      MaxClients: 64                   # máximo de SenderCompID distintos com arquivos neste dispatcher (3.4.6)
      Recovery:                        # política de sessão do acceptor (3.4.7)
        OnReconnectGap: WaitForResendRequest   # WaitForResendRequest | SkipToCurrent
        AllowResetSeqNumFlag: true             # permite 141=Y (nova época do cliente)
        AcceptNextExpectedMsgSeqNum: true      # lê a 789 do Logon quando o cliente a envia
        RequireNextExpectedMsgSeqNum: false    # true recusa Logon sem a 789
        LogoutTimeoutSeconds: 5                # espera do Logout de confirmação antes de fechar
        # Cliente que espera seq inexistente (789 ou BeginSeqNo > M): sempre Logout
      # ClientOverrides:
      #   - SenderCompId: "OMS1"
      #     OnReconnectGap: SkipToCurrent
      ChannelCapacity: 8192

    - Id: "rabbitmq-dropcopy"
      Type: RabbitMQ
      Host: "rabbitmq.internal"
      VirtualHost: "/trading"
      Exchange: "dropcopy.fanout"
      RoutingKey: ""
      CursorAckMode: OnBrokerAck         # publisher confirms; cursor avança na confirmação
      ChannelCapacity: 8192

    - Id: "ws-dropcopy"
      Type: WebSocket
      ListenAddress: "0.0.0.0"
      ListenPort: 6002
      AllowedOrigins: ["https://app.example.com"]
      HeartbeatIntervalSeconds: 30
      MissedHeartbeatLimit: 2
      LogonTimeoutSeconds: 5
      MaxControlFrameBytes: 4096
      MaxFields: 64
      MaxOutboundFrameBytes: 65536
      SessionChannelCapacity: 1024   # SequenceStoreDirectory removido: o seq vem do SessionLog (3.4.6)
      ChannelCapacity: 8192
      # TLS:
      #   Enabled: true
      #   CertificatePath: "/certs/server.pfx"
      #   CertificatePasswordEnvVar: "NEXUS_WS_CERT_PWD"

  # Cadastro só para WebSocket (UUID + token) e brokers. Clientes FIX NÃO são
  # cadastrados: a chave é DispatcherId + SenderCompID, criada no primeiro Logon
  # aceito (ver 3.4.6). Entradas FIX abaixo ficam apenas por compatibilidade e
  # são ignoradas. O log grava todas as mensagens (ver 3.4.3).
  Clients:
    - ClientId: "6f1e0b3a-9c2d-4a7e-8b15-2d4c0a9e7f31"
      DispatcherId: "fix-out-client1"
      FixSenderCompId: "OMS1"
    - ClientId: "a4c8e2d1-1b6f-4e90-9a33-7c5d2e8b0146"
      DispatcherId: "kafka-main"
    - ClientId: "c1d2e3f4-a5b6-4789-8abc-def012345678"
      DispatcherId: "ws-dropcopy"
      TokenEnvVar: "NEXUS_WS_CLIENT_TOKEN"

  # Log de sessão em arquivo (ver 3.4.3). Fonte de recuperação de FIX,
  # WebSocket e brokers. Substitui a hypertable client_replay_messages.
  SessionLog:
    Directory: "/var/lib/nexusrouter/sessionlog"
    RetentionTradingDays: 5           # parametrizável; padrão 5 dias de pregão
    FlushMode: Interval               # Interval | PerMessage
    FlushIntervalMs: 5000             # usado quando FlushMode = Interval
    SegmentSizeMB: 256                # tamanho fixo, pré-alocado; valor inicial a validar no benchmark
    IndexSlotsPerSegment: 1000000     # slots do .index por segmento
    UncleanRestartSeqMargin: 10000    # salto do seq de saída após queda; 0 com FlushMode = PerMessage
    CatchUpIdleMs: 200                # intervalo do catch-up sem tráfego
    SlotPublishTimeoutMs: 1000        # slot reservado e não publicado vira gap depois disso

  Audit:
    ChannelCapacity: 65536
    OverflowMode: DropOldest
    BatchSize: 1000
    FlushIntervalMs: 100
    PersistMode: Full                 # Full | MetadataOnly | Sampled
    SampleRate: 1.0
    Sinks:
      - Type: Serilog
        FilePath: "/var/log/nexusrouter/audit-.log"
        RollingInterval: Hour
      - Type: Postgres
        Host: "localhost"
        Port: 5432
        Database: "nexusrouter"
        UsernameEnvVar: "NEXUS_PG_USER"
        PasswordEnvVar: "NEXUS_PG_PASSWORD"
        SslMode: Disable              # Disable local; Require / VerifyFull em AWS
        CommandTimeoutSeconds: 15
```

---

## 7. Estrutura do Projeto

```
NexusRouter/
├── src/
│   ├── NexusRouter.Domain/               # Value types, interfaces e enums do núcleo
│   │   ├── Abstractions/
│   │   │   ├── IMessageDispatcher.cs
│   │   │   ├── ISessionFrameDecoder.cs
│   │   │   ├── IReconnectPolicy.cs
│   │   │   ├── ISessionLogStream.cs      # novo (TD-04)
│   │   │   ├── ISessionLogReader.cs      # novo (TD-04)
│   │   │   ├── IClientIndex.cs           # novo (TD-04)
│   │   │   └── IConsumerState.cs         # novo (TD-04)
│   │   ├── Models/
│   │   │   ├── IngressFrame.cs           # ref struct
│   │   │   ├── FixMessage.cs             # ref struct
│   │   │   ├── FixField.cs               # readonly struct
│   │   │   ├── DispatchPayload.cs
│   │   │   ├── AuditRecord.cs
│   │   │   ├── LogRef.cs                 # novo (TD-04); readonly record struct
│   │   │   └── RouteContext.cs           # readonly record struct
│   │   └── Enums/
│   │       ├── DeliveryMode.cs
│   │       └── SessionState.cs
│   │
│   ├── NexusRouter.Ingress/              # Gerenciamento de sessões e decodificação de frames
│   │   ├── Sessions/
│   │   │   ├── FixSession.cs
│   │   │   ├── FixSessionStateMachine.cs
│   │   │   └── ExponentialBackoffReconnectPolicy.cs
│   │   └── Decoders/
│   │       ├── Fix44FrameDecoder.cs
│   │       ├── Fix50FrameDecoder.cs
│   │       └── SbeFrameDecoder.cs        # Stub para uso futuro
│   │
│   ├── NexusRouter.Router/               # Motor de roteamento do hot path
│   │   ├── RouterEngine.cs
│   │   ├── RouteResolver.cs
│   │   ├── PassThroughRewriter.cs
│   │   ├── FixTokenizer.cs
│   │   └── FanOutEngine.cs
│   │
│   ├── NexusRouter.Egress/               # Implementações de dispatchers
│   │   ├── Kafka/
│   │   │   ├── KafkaMessageDispatcher.cs
│   │   │   └── IKafkaPartitionStrategy.cs
│   │   ├── RabbitMQ/
│   │   │   └── RabbitMQMessageDispatcher.cs
│   │   ├── StreamCatchUp.cs              # novo (TD-04a); transição channel↔log
│   │   ├── BrokerCursorTracker.cs        # novo (TD-04b); marca-d'água contígua
│   │   ├── Fix/
│   │   │   ├── FixOutboundDispatcher.cs  # TcpListener; N sessões aceitas
│   │   │   ├── FixAcceptedSession.cs     # uma conexão, um MsgSeqNum
│   │   │   └── ResendPlanner.cs          # novo (TD-08); plano de reenvio sobre o IClientIndex
│   │   └── WebSocket/
│   │       ├── WebSocketMessageDispatcher.cs  # TcpListener; upgrade; N sessões
│   │       ├── WebSocketAcceptedSession.cs    # uma conexão, um seq, um filtro
│   │       └── WebSocketFrameEncoder.cs       # JSON ou binário, fora do hot path
│   │
│   ├── NexusRouter.Audit/                # Log de auditoria do cold path
│   │   ├── AuditWriterService.cs
│   │   ├── AuditChannelSingleton.cs
│   │   └── Sinks/
│   │       ├── IAuditSink.cs
│   │       ├── SerilogAuditSink.cs
│   │       └── PostgresAuditSink.cs      # Npgsql COPY → TimescaleDB
│   │
│   ├── NexusRouter.SessionLog/           # novo (TD-04): log de sessão em arquivo
│   │   ├── SessionLogManager.cs          # IHostedService: recuperação, flusher, rotação, retenção
│   │   ├── SessionLogStream.cs
│   │   ├── LogSegment.cs
│   │   ├── ClientIndex.cs                # .cidx
│   │   ├── ConsumerStateStore.cs
│   │   └── SessionLogOptions.cs
│   │
│   └── NexusRouter.Host/                 # Entry point, wiring de DI, configuração
│       ├── Program.cs
│       ├── appsettings.json
│       ├── Configuration/
│       │   ├── NexusRouterOptions.cs
│       │   ├── SessionOptions.cs
│       │   └── DispatcherOptions.cs
│       └── Extensions/
│           └── ServiceCollectionExtensions.cs
│
├── tests/
│   ├── NexusRouter.Domain.Tests/
│   ├── NexusRouter.Ingress.Tests/
│   ├── NexusRouter.Router.Tests/
│   ├── NexusRouter.Egress.Tests/
│   ├── NexusRouter.SessionLog.Tests/     # novo: crash, CRC, concorrência de slots, retenção
│   └── NexusRouter.Benchmarks/           # Benchmarks de hot path com BenchmarkDotNet (inclui o append do log)
│
├── docker/
│   └── postgres/
│       └── init.sql                      # CREATE EXTENSION timescaledb + hypertable
├── docker-compose.yml                    # Kafka KRaft, RabbitMQ, TimescaleDB
├── .env.example
└── NexusRouter.slnx
```

**Arquivos alterados pela revisão do log de sessão.** Os caminhos foram conferidos na árvore em 2026-11-08. `Egress/WebSocket/*` ainda não existe (Fase 10), então ali a mudança é de como criar. O `init.sql` não tem `client_replay_messages`, então não há tabela a remover.

| Arquivo | Alteração | Task |
|---|---|---|
| `NexusRouter.slnx`, `Directory.Packages.props` | Incluir `NexusRouter.SessionLog` e seu projeto de teste | TD-04 |
| `src/NexusRouter.Domain/Models/DispatchPayload.cs` | Campo `LogRef`, copiado em `CopyPayload` do fan-out | TD-04a |
| `src/NexusRouter.Ingress/Sessions/FixSession.cs` | Chamar `Append` após o `TryDecode`, antes do `SessionFSM`; extrair a tag 34 como seq de entrada | TD-04a |
| `src/NexusRouter.Router/RouterEngine.cs` | Propagar o `LogRef` ao `DispatchPayload` | TD-04a |
| `src/NexusRouter.Router/FanOutEngine.cs` | Rota vazia e channel cheio deixam de descartar; métrica de lag no lugar da de overflow | TD-04a |
| `src/NexusRouter.Egress/DispatcherBase.cs` | `StreamCatchUp`, métrica de lag, timer `CatchUpIdleMs` | TD-04a |
| `src/NexusRouter.Egress/Fix/SequenceNumberStore.cs` | Removido. Substituído por `IClientIndex` + `IConsumerState` | TD-04 |
| `src/NexusRouter.Egress/Fix/FixOutboundDispatcher.cs` | `SendAsync` reserva seq e publica no índice de cada cliente conhecido, mesmo sem sessão logada; cria arquivos no primeiro Logon aceito e aplica `MaxClients` | TD-04a, T-44 |
| `src/NexusRouter.Egress/Fix/FixAcceptedSession.cs` | Seq e entradas administrativas pelo `IClientIndex`; `ResendRequest` pelo `ResendPlanner` | TD-04, TD-08 |
| `src/NexusRouter.Egress/Kafka/*`, `RabbitMQ/*` (dispatchers e options) | Header de idempotência, `CursorAckMode`, `BrokerCursorTracker` | TD-04b |
| `src/NexusRouter.Egress/WebSocket/*` | Seq e replay pelo `IClientIndex` | T-50, T-51, T-52 |
| `src/NexusRouter.Host/Configuration/ReplayOptions.cs`, `NexusRouterOptions.cs` | `Replay` sai; `SessionLog` (`SessionLogOptions`) entra, com validação | TD-04 |
| `src/NexusRouter.Host/Extensions/ServiceCollectionExtensions.cs`, `Program.cs` | Registrar `SessionLogManager` e a ordem de shutdown (NFR-REL-04) | TD-04 |
| `src/NexusRouter.Host/appsettings.json` | Seção `Replay` (linha 144) sai; `SessionLog` entra | TD-04 |

---

## 8. Fluxo de Dados — Sequência End-to-End

```
[Exchange TCP Socket]
        │
        │ bytes brutos (segmento TCP)
        ▼
[FixSession — PipeWriter.WriteAsync]
        │
        │ segmento do pipe (slice zero-copy)
        ▼
[ISessionFrameDecoder.TryDecode]
        │ retorna IngressFrame (ref struct, sem alloc)
        ▼
[ISessionLogStream.Append(frame original, seq de entrada)]
        │ cópia em memória mapeada; publica committed; devolve LogRef
        │ (falha: segue sem LogRef, stream degradado)
        ▼
[SessionFSM — verificação de mensagem administrativa]
        │ (Heartbeat/TestRequest tratados inline, retorna)
        │ (Mensagem de aplicação passa adiante)
        ▼
[RouterEngine.Route(IngressFrame)]
        │
        ├─► [FrozenDictionary<SessionId, RouteContext> lookup]  — O(1), sem lock
        │
        ├─► [DeliveryMode == PassThrough?]
        │       │ Sim → PassThroughRewriter.Rewrite(Span<byte>)  — in-place (largura igual), sem alloc
        │       │ Não → FixTokenizer.Tokenize(...)               — stack-alloc, sem heap alloc
        │
        ├─► [Aluga buffer do ArrayPool, copia frame, encapsula em DispatchPayload do ObjectPool]
        │
        └─► [FanOutEngine — para cada dispatcher em RouteContext.Dispatchers]
                │
                ├─► dispatcher_A.TryEnqueue(payload)  — Channel TryWrite, não bloqueante
                ├─► dispatcher_B.TryEnqueue(payload)  — Channel TryWrite, não bloqueante
                └─► dispatcher_N.TryEnqueue(payload)  — Channel TryWrite, não bloqueante
                        │
                        └─► [AuditChannel.TryWrite(auditRecord)]  — não bloqueante

--- FRONTEIRA DO HOT PATH — controle retorna ao loop do PipeReader ---

[Dispatcher Consumer Task — thread do thread-pool em background]
        │
        ├─► StreamCatchUp: compara o LogRef do payload com expected[stream]
        │       (igual: entrega · maior: lê o buraco do log e entrega · menor: libera)
        │
        ├─► Clientes conhecidos (FIX, WebSocket): ReserveSeq + PublishApp no índice do cliente,
        │       mesmo sem sessão logada; só envia se a sessão existe
        │
        ├─► KafkaMessageDispatcher: IProducer.ProduceAsync(topic, message)
        ├─► RabbitMQMessageDispatcher: IModel.BasicPublish(exchange, routingKey, body)
        ├─► FixOutboundDispatcher: enfileira uma cópia por sessão aceita, cada uma com o próprio MsgSeqNum
        └─► WebSocketMessageDispatcher: projeta por sessão (json ou binary, campos do Logon) e enfileira

[AuditWriterService Consumer Task — thread do thread-pool em background]
        ├─► SerilogAuditSink (metadados, lote async)
        └─► PostgresAuditSink.WriteBatchAsync — Npgsql COPY na hypertable
```

---

## 9. Análise do Orçamento de Latência

Contribuições estimadas dos componentes do hot path para a latência end-to-end (alvo p99: 50 µs):

| Componente | Contribuição Estimada | Observações |
|------------|----------------------|-------------|
| Recebimento no socket (`PipeWriter.WriteAsync`) | 2–8 µs | Cópia do kernel para userspace; irredutível |
| `TryDecode` (varredura de limite de frame) | 0,1–0,5 µs | Varredura sequencial em `Span<byte>` contíguo |
| Lookup no `FrozenDictionary` | < 0,1 µs | Hash probe em dicionário imutável |
| `PassThroughRewriter` (reescrita das tags 34 + 10) | 0,2–1 µs | Duas varreduras sequenciais em `Span<byte>` |
| `FixTokenizer` (parse completo, 20 campos) | 1–5 µs | Stack-allocated, sem heap |
| `ArrayPool.Rent` + cópia de buffer | 0,5–2 µs | Memcpy de mensagem FIX típica (~200 bytes) |
| `ObjectPool.Get` (DispatchPayload) | < 0,1 µs | Pool pré-aquecido na inicialização |
| `Channel.TryWrite` × N dispatchers | 0,2 µs cada | Push em fila lock-free |
| `AuditChannel.TryWrite` | 0,1 µs | Não bloqueante |
| `ISessionLogStream.Append` (registro + índice + CRC, ~200–500 bytes) | **Meta ≤ 3 µs no p99. Não medido.** | Memcpy em página já mapeada e tocada, mais CRC. Page fault e troca de segmento são a cauda: o próximo segmento é pré-alocado e tocado em background. É estimativa de projeto, não resultado |
| **Total (PassThrough, 3 dispatchers, com log)** | **~6–18 µs (estimado)** | Dentro do orçamento p99 de 50 µs se o append cumprir a meta |

**Critério de aceite do append (TD-04).** O benchmark mede o append na carga de NFR-PERF-01 (50.000 mensagens/segundo por sessão, ou seja ~15 MB/s por sessão com frames de 300 bytes), e não só no volume de referência diário de 500 mil mensagens. Reporta p50, p99 e p99,9, e a contagem de trocas síncronas de segmento. Se p99 passar de 3 µs ou o p99,9 do hot path total estourar o NFR-PERF-01, as alternativas, nesta ordem, são: pré-tocar mais páginas à frente, mudar o `FlushMode`/intervalo, e, por último, entregar o frame a uma thread escritora por um anel SPSC (acrescenta um handoff, mas tira a escrita da thread da sessão). A mudança de design só entra com o número medido.

**Disco na carga de pico.** A 200.000 mensagens/segundo agregadas (NFR-PERF-02) e 300 bytes, o log cresce ~60 MB/s, ou ~5 GB por hora. Isso é muito acima dos 150 a 250 MB por dia do volume de referência. A retenção de 5 dias precisa ser dimensionada pelo volume real do pregão, não pelo teste de carga.
| **Pressão de GC** | 0 µs (alvo) | Zero alloc em regime estacionário |

---

## 10. Principais Decisões de Design e Justificativas

| Decisão | Alternativa Considerada | Justificativa |
|---------|------------------------|---------------|
| `System.IO.Pipelines` para I/O de socket | `Socket.Receive` bruto com `byte[]` | Pipelines eliminam cópia dupla; back-pressure integrado; slicing zero-copy em `ReadOnlySequence<byte>` |
| `FrozenDictionary` para tabela de roteamento | `ConcurrentDictionary` | FrozenDictionary tem menor overhead de leitura (sem CAS no hit-path); seguro pois a tabela de roteamento é imutável em runtime |
| `ref struct` para `IngressFrame` / `FixMessage` | Objetos de mensagem baseados em classe | Garante alocação em stack; previne captura acidental em boundaries async; reforça disciplina zero-allocation em tempo de compilação |
| `TryWrite` (não bloqueante) para fan-out | `await WriteAsync(...)` | Mantém o hot path síncrono e livre de alocações; back-pressure é absorvida pelos channels dos dispatchers |
| Thread dedicada de longa duração por sessão | Tasks do thread-pool | Elimina latência de scheduling do thread-pool para o hot path; afinidade de CPU previsível (future CPU pinning possível) |
| Contagem de referências do buffer (fan-out) | Cópia de buffer por dispatcher | Evita cópias O(N) no fan-out; ciclo único de rent/return independente da contagem de dispatchers |
| `ObjectPool<DispatchPayload>` | `new DispatchPayload()` por mensagem | Elimina alocação em regime estacionário; `DispatchPayload` é uma classe (necessária para compartilhamento entre channels) mas poolizada |
| `Confluent.Kafka` + options de segurança | Cliente AWS-only ou QuickFIX-style engine | Um producer cobre Docker, Kafka auto-gerenciado e Amazon MSK; IAM é opt-in |
| TimescaleDB (extensão PostgreSQL) para FIX persistido | Só Serilog arquivo, ou PG heap sem particionar | Store consultável no cold path; chunks, compressão e retenção batem com ingestão temporal; `Npgsql` único cliente |
| Kafka PLAINTEXT no Compose + dual listener | Um listener `kafka:9092` | App no host usa `localhost:19092`; outros containers usam `kafka:9092`; evita advertised.listeners quebrado |
| WebSocket projeta no consume loop, por sessão | Filtrar campos no router, ou reenviar o frame FIX | O hot path continua entregando o payload inteiro. Cada browser pede no Logon o encoding e as tags que cabem na tela. Binário e JSON carregam os mesmos valores ASCII do FIX, sem coerção numérica |
| Replay WebSocket projeta o frame de entrada guardado no log | Guardar o frame já projetado por cliente | Uma cópia do payload para todos os clientes. A projeção (encoding e `fields` da conexão atual) acontece no consume loop quando o pedido `replay` chega. Trocar `fields` no reconnect muda também a projeção do replay |
| Log de sessão em arquivo mapeado, append síncrono na thread da sessão | Timescale como store de replay; thread escritora separada | Banco síncrono custa ms e cria dependência externa na recuperação. Thread separada acrescenta handoff. Fica como alternativa se o benchmark do TD-04 reprovar o append |
| Índice por cliente escrito pela task de consumo e pela sessão aceita, em slots distintos | Escritor único por cliente com lock ou fila | O `seq` já vem de `Interlocked`. Slots distintos não precisam de lock, e `kind` publica o slot |
| Seq de saída salta depois de queda, nunca regride | Retomar do último valor persistido | Regredir repete seqnum já entregue. Saltar é tolerado pelo FIX e vira gap-fill |
| Falha do log não para o roteamento | Parar o roteamento se o log falhar | A entrega ao vivo é o objetivo de latência. Perde-se só a garantia de recuperar o trecho, com métrica e alerta |
