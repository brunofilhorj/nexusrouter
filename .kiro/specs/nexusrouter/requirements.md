# NexusRouter — Requisitos Funcionais e Não Funcionais

> **Status do Documento:** Rascunho v1.0  
> **Classificação:** Especificação de Engenharia Interna  
> **Domínio:** High-Frequency Trading (HFT) — Motor de Execução de Ordens / DropCopy Distribution Engine

---

## 1. Visão Geral do Sistema

O NexusRouter é um motor de ingestão, roteamento e distribuição de alta performance para streams de Execução de Ordens e DropCopy. Atua como camada intermediária entre as sessões de conectividade upstream com a bolsa (FIX/SBE) e os consumidores downstream (clientes FIX, clientes WebSocket, tópicos Kafka, exchanges RabbitMQ). Sua restrição definidora é a **latência end-to-end ultra-baixa e determinística** em todo o hot path — do recebimento do byte até o handoff ao dispatcher.

---

## 2. Requisitos Funcionais

### 2.1 Ingestão Multi-Sessão (FR-ING)

| ID | Requisito |
|----|-----------|
| FR-ING-01 | O sistema DEVE gerenciar múltiplas sessões FIX/SBE inbound independentes de forma concorrente. Cada sessão opera em isolamento: sua state machine, rastreamento de sequence number e ciclo de vida de reconexão são completamente encapsulados e não compartilham estado mutável com sessões irmãs. |
| FR-ING-02 | Cada sessão DEVE implementar o protocolo de nível de sessão FIX: Logon (MsgType=A), Logout (MsgType=5), Heartbeat (MsgType=0), TestRequest (MsgType=1) e ResendRequest (MsgType=2). |
| FR-ING-03 | O sistema DEVE suportar intervalos de Heartbeat configuráveis por sessão (HeartBtInt). Uma sessão que perder N heartbeats consecutivos (configurável) DEVE iniciar um TestRequest antes de declarar a sessão como inativa e desconectar. |
| FR-ING-04 | Cada sessão DEVE implementar reconexão automática com **exponential backoff**. Os parâmetros de delay base, delay máximo e jitter DEVEM ser configuráveis de forma independente por sessão. |
| FR-ING-05 | O sistema DEVE ser **agnóstico ao protocolo na camada de transporte**: as sessões são identificadas por um `SessionId` abstrato e seu framing a nível de wire (FIX 4.x SOFH, FIX 5.0/FIXT, SBE com wrapper FIX) é tratado por implementações plugáveis de `ISessionFrameDecoder`. |
| FR-ING-06 | A configuração de sessão (host, porta, sender/target comp IDs, credenciais, configurações TLS, intervalo de heartbeat, política de reconexão) DEVE ser carregada a partir de uma fonte de configuração estruturada (ex.: `appsettings.json` ou um `sessions.json` dedicado) sem necessidade de recompilação. |
| FR-ING-07 | O sistema DEVE emitir eventos estruturados de ciclo de vida de sessão (Connected, Logon, Logout, Disconnected, Reconnecting) que podem ser consumidos pelo monitoramento e pelo log de auditoria sem acoplamento ao hot path. |

### 2.2 Hot Path de Ultra-Baixa Latência (FR-HLP)

| ID | Requisito |
|----|-----------|
| FR-HLP-01 | O hot path — definido como o caminho de execução desde o recebimento de bytes brutos no buffer do socket até o handoff ao `Dispatch()` do dispatcher — NÃO DEVE realizar **nenhuma operação de I/O síncrona** (sem gravações em disco, sem chamadas a banco de dados, sem chamadas de rede bloqueantes). |
| FR-HLP-02 | O hot path NÃO DEVE realizar **nenhuma alocação heap** no processamento de mensagens em regime estacionário após a fase inicial de warm-up. Todos os buffers, objetos poolizados e memória de trabalho DEVEM ser pré-alocados ou obtidos de pools de objetos/memória lock-free. |
| FR-HLP-03 | O sistema DEVE usar `System.IO.Pipelines` (`PipeReader`/`PipeWriter`) para todas as leituras de socket inbound, a fim de eliminar cópias desnecessárias de buffer e possibilitar parsing zero-copy onde aplicável. |
| FR-HLP-04 | O hot path NÃO DEVE adquirir nenhuma instrução `lock` contendida ou primitiva de sincronização bloqueante. Toda coordenação de estado compartilhado no hot path DEVE usar estruturas lock-free (`Interlocked`, `System.Collections.Concurrent` ou `System.Threading.Channels` em modo não bloqueante). |
| FR-HLP-05 | A geração e atribuição de sequence numbers (para re-framing outbound no modo Pass-Through) DEVE ser realizada usando `Interlocked.Increment` em um contador `long` — um contador por sessão outbound. |
| FR-HLP-06 | A memória para buffers de frame inbound DEVE ser obtida exclusivamente de `System.Buffers.ArrayPool<byte>` ou `MemoryPool<byte>`. Buffers alugados DEVEM ser devolvidos ao pool imediatamente após o handoff ao dispatcher ou a conclusão do fan-out. |
| FR-HLP-07 | O sistema DEVE atingir uma **latência p99 no hot path de ≤ 50 µs** (recebimento no socket até handoff ao dispatcher) sob carga sustentada de 50.000 mensagens/segundo por sessão em hardware de referência (servidor x86-64 moderno, sem pressão de GC). |

### 2.3 Raw Pass-Through — Modo de Bypass do Parser (FR-PTH)

| ID | Requisito |
|----|-----------|
| FR-PTH-01 | Cada sessão DEVE suportar uma propriedade `DeliveryMode` configurável. Quando definida como `PassThrough`, o sistema DEVE encaminhar o buffer de bytes inbound bruto diretamente para os dispatchers FIX outbound downstream **sem realizar o parsing dos campos do body FIX**. |
| FR-PTH-02 | No modo `PassThrough`, o sistema DEVE reescrever exatamente dois campos FIX in-place no buffer antes de encaminhar: `MsgSeqNum (tag 34)` e `CheckSum (tag 10)`. Nenhum outro campo DEVE ser modificado. |
| FR-PTH-03 | A reescrita in-place de `MsgSeqNum` e `CheckSum` DEVE operar diretamente no `Span<byte>` alugado sem alocar uma string intermediária ou objeto gerenciado. |
| FR-PTH-04 | Quando `DeliveryMode` for definido como `Parse`, o sistema DEVE realizar a tokenização completa de campos FIX e popular um value type estruturado `FixMessage` antes de fazer handoff para os dispatchers. |
| FR-PTH-05 | `DeliveryMode` DEVE ser recarregável em hot reload por sessão sem necessidade de reinicialização do serviço (o evento de reload de configuração se propaga para o routing context da sessão em tempo de execução). |

### 2.4 Egress Polimórfico — `IMessageDispatcher` (FR-EGR)

| ID | Requisito |
|----|-----------|
| FR-EGR-01 | O sistema DEVE definir uma interface `IMessageDispatcher` como a única abstração de egress. Todos os mecanismos de entrega downstream DEVEM implementar esta interface. |
| FR-EGR-02 | O sistema DEVE suportar no mínimo quatro implementações de dispatcher concorrentes: `KafkaMessageDispatcher`, `RabbitMQMessageDispatcher`, `FixOutboundDispatcher` e `WebSocketMessageDispatcher`. |
| FR-EGR-03 | O `FixOutboundDispatcher` DEVE atuar como acceptor FIX: escuta um endereço e uma porta configurados e aceita N clientes. Cada conexão é uma sessão FIX independente, com fila de envio e MsgSeqNum outbound próprios, persistidos e independentes da sessão inbound e das demais sessões aceitas. Um cliente lento ou desconectado NÃO DEVE bloquear a entrega às outras sessões desse dispatcher. Sem sessão logada, a mensagem não é descartada: fica no log de sessão (FR-RPL-02) e o cliente que já conectou antes a recupera pelo seqnum quando reconecta. `KafkaMessageDispatcher` e `RabbitMQMessageDispatcher` DEVEM conectar ao broker configurado e publicar; eles não aceitam conexão de cliente. |
| FR-EGR-04 | O motor de roteamento DEVE suportar **fan-out**: uma única mensagem inbound pode ser despachada concorrentemente para múltiplas instâncias registradas de `IMessageDispatcher`. O fan-out NÃO DEVE criar dependências síncronas entre dispatchers — um dispatcher lento ou com falha NÃO DEVE bloquear ou degradar a entrega para os demais. |
| FR-EGR-05 | Cada instância de dispatcher DEVE ter seu próprio buffer/channel de envio isolado. A back-pressure de um consumidor downstream DEVE ser absorvida pela fila interna daquele dispatcher e NÃO DEVE se propagar de volta ao receive loop do hot path. |
| FR-EGR-06 | O registro de dispatchers e as regras de roteamento (qual sessão roteia para qual(is) dispatcher(s)) DEVEM ser definidos em configuração e carregados na inicialização. Mutação dinâmica de rotas em tempo de execução é desejável, mas está fora do escopo da v1. |
| FR-EGR-07 | Cada dispatcher DEVE implementar um ciclo de vida `StartAsync` / `StopAsync` alinhado ao padrão `IHostedService` do .NET para inicialização limpa e shutdown gracioso. |
| FR-EGR-08 | O `KafkaMessageDispatcher` DEVE publicar em tópicos Kafka configuráveis usando o producer `Confluent.Kafka` em modo fire-and-forget (acks=0 para latência mínima) OU com política de confirmação configurável. A estratégia de tópico e chave de partição DEVE ser configurável por instância de dispatcher. O mesmo dispatcher DEVE conectar tanto a Kafka auto-gerenciado (Docker, VM, cluster Apache/Redpanda/Confluent Platform) quanto a Kafka gerenciado na AWS (Amazon MSK), sem código específico de provedor no hot path — apenas `ProducerConfig` derivado de options. |
| FR-EGR-11 | Cada instância Kafka DEVE expor, via configuração, no mínimo: `BootstrapServers`, `Topic`, `Acks`, `ClientId`, `SecurityProtocol` (`Plaintext`, `Ssl`, `SaslPlaintext`, `SaslSsl`), `SaslMechanism` (`None`, `Plain`, `ScramSha256`, `ScramSha512`, `OAuthBearer`, `AwsMskIam`), nomes de variáveis de ambiente para `SaslUsername`/`SaslPassword`, e opções TLS do producer (`SslCaLocation`, `EnableSslCertificateVerification`). Valores default DEVEM permitir Kafka local PLAINTEXT (`localhost:9092` / hostname Compose). Amazon MSK SASL/SCRAM DEVE ser alcançável com `SaslSsl` + `ScramSha512` e bootstrap `:9096`. Auth IAM do MSK (`AwsMskIam`) é opcional e DEVE ser isolada atrás de um callback/pacote carregado apenas quando o mechanism estiver selecionado. |
| FR-EGR-09 | O `RabbitMQMessageDispatcher` DEVE publicar em exchanges/routing keys configuráveis usando a biblioteca oficial `RabbitMQ.Client` com um channel dedicado por dispatcher para evitar contenção de channel. |
| FR-EGR-10 | Erros de dispatcher (falha de publicação, perda de conexão) DEVEM ser capturados, registrados no log de auditoria estruturado, e NÃO DEVEM propagar exceções para o hot path. |
| FR-EGR-12 | O `WebSocketMessageDispatcher` DEVE atuar como acceptor: escuta um endereço e uma porta configurados e aceita N clientes (aplicação web ou nativa). Cada conexão logada é uma sessão independente, com fila de envio e `seq` próprios. Um cliente lento ou desconectado NÃO DEVE bloquear a entrega às outras sessões desse dispatcher. Sem sessão logada, a mensagem não é descartada: fica no log de sessão (FR-RPL-02) e o cliente cadastrado a recupera pelo pedido `replay`. O primeiro frame de aplicação DEVE ser um Logon JSON que fixa, para aquela conexão, `encoding` (`json` ou `binary`) e o conjunto de campos (`fields` omitido = todas as propriedades de aplicação da ordem; lista de tags FIX = só essas tags, na ordem pedida). A projeção para JSON ou binário acontece no consume loop do dispatcher, nunca no hot path. O token de autenticação viaja só no Logon, nunca na URL. TLS de servidor (wss) é opcional. Um upgrade com header `Origin` só completa se a origem estiver em `AllowedOrigins`. |

### 2.4.1 Retransmissão por cliente, sobre o log de sessão (FR-RPL)

> **Revisão de 2026-11-06 (`notes/backlog-sessao-fix.md`).** A retransmissão deixou de ser opt-in diário gravado em hypertable. Um log de sessão em arquivo (append-only, por dia de pregão e por época) grava **todas** as mensagens antes do fan-out, então toda sessão tem o que reenviar e não há mais escolha diária. A retransmissão passa a ler desse log e do índice por cliente. A hypertable `client_replay_messages` sai; o PostgreSQL + TimescaleDB fica só com a auditoria. O filtro de roteamento por tag de negócio fica fora do escopo.

A retransmissão não depende do tipo de dispatcher. Cada consumidor é identificado por `ClientId` (UUID) e tem um índice próprio sobre o log de sessão. A entrega ao vivo continua pelo channel do dispatcher; a recuperação lê do log.

| ID | Requisito |
|----|-----------|
| FR-RPL-01 | Cada cliente de saída DEVE ter arquivos próprios no log de sessão (índice `.cidx` e `state.json`), identificados por uma chave estável. Cliente FIX: `DispatcherId` + `SenderCompID` (tag 49 do Logon), sem cadastro prévio; os arquivos são criados no primeiro Logon aceito e o `SenderCompID` DEVE ser validado (`[A-Za-z0-9_-]`, 1 a 64 caracteres) antes de virar nome de pasta. O número de `SenderCompID` distintos por dispatcher DEVE ser limitado (`MaxClients`). Cliente WebSocket: cadastro com `ClientId` UUID, `DispatcherId` e `TokenEnvVar` (o token autentica). Um cliente que nunca conectou não tem arquivos nem recebe mensagens retroativas. |
| FR-RPL-10 | No Logon e no `ResendRequest`, o acceptor DEVE reconciliar o seq que o cliente espera (`N`, lido da tag `789` ou do `BeginSeqNo`) com o seu próximo seq de saída (`M`): `N < M` reenvia `N..M-1` (ou envia gap-fill com `SkipToCurrent`); `N == M` segue; `N > M` (seq inexistente no router) leva a `Logout` com `Text`, sem adotar o `N` do cliente. Um `ResendRequest` com `BeginSeqNo >= M` recebe o mesmo tratamento. |
| FR-RPL-11 | O acceptor DEVE oferecer três modos de conexão: começar do zero (`141=Y`, se permitido), retomar de onde parou e retomar do ponto atual do router (`SkipToCurrent`); e DEVE recusar com `Logout` o cliente que espera um seq inexistente. A tag `789` é opcional e acordada bilateralmente. As opções `OnReconnectGap`, `AllowResetSeqNumFlag`, `AcceptNextExpectedMsgSeqNum`, `RequireNextExpectedMsgSeqNum` e `LogoutTimeoutSeconds` DEVEM ser configuráveis por dispatcher e por `SenderCompID`. Ao recusar, o router DEVE enviar `Logout`, aguardar o `Logout` de confirmação até o timeout e só então fechar. |
| FR-RPL-12 | O acceptor DEVE tratar o `34` recebido do cliente com política mista: maior que o esperado é aceito com aviso e métrica; menor sem `PossDupFlag` leva a `Logout` ("MsgSeqNum too low"); menor com `PossDupFlag=Y` é ignorado. O `ResendRequest` do cliente DEVE ser processado sempre. |
| FR-RPL-13 | O NexusRouter como initiator DEVE seguir o comportamento padrão do QuickFIX para seqnum e recuperação de sessão, com as exceções exigidas pela especificação da B3 registradas no `b3-guidelines.md` (TD-03). |
| FR-RPL-02 | O sistema DEVE gravar toda mensagem num log de sessão append-only antes do fan-out, administrativas incluídas, no estilo do `MessageStore` do QuickFIX. O log é rotacionado por dia de pregão e por época (reset de seqnum ou virada de pregão). Não há opt-in de retenção: como o log grava tudo, toda sessão pode ser retransmitida. |
| FR-RPL-03 | No ResendRequest FIX (tags 7 e 16), o servidor DEVE reenviar pelo índice daquele `ClientId`: mensagem de aplicação com `PossDupFlag=Y` (43) e `OrigSendingTime` (122); mensagem administrativa como `SequenceReset-GapFill` agrupado. DEVE respeitar `EndSeqNo` e o limite de 10.000 mensagens por pedido (`PossMissingApplMsg=Y` acima disso). A retransmissão é só daquele `ClientId`. |
| FR-RPL-04 | Dispatcher de broker (Kafka, RabbitMQ) não identifica o assinante na entrega. A entrega DEVE ser ao menos uma vez: cada mensagem leva um header de identidade `<sessão de origem>:<época>:<seq de entrada>`, e o cursor do dispatcher avança conforme o `CursorAckMode` configurado. O consumidor deduplica por esse header. A recuperação se dá pelo cursor persistido: no restart o dispatcher retoma do cursor e reentrega do log. |
| FR-RPL-05 | A gravação no log DEVE ser feita por um escritor único por stream de ingress (a thread da sessão), sem lock e sem I/O síncrono de disco no caminho normal: o append é cópia em memória mapeada, e o flush fica em task própria. O frame gravado é o original, antes do `PassThroughRewriter`. Channel do dispatcher cheio NÃO DEVE descartar mensagem: vira lag recuperável pelo cursor. Rota sem dispatcher também NÃO DEVE descartar: a mensagem fica no log. A métrica de lag por dispatcher substitui a de descarte por channel cheio. Falha de escrita no log (disco cheio, erro de I/O) NÃO DEVE parar o roteamento: a entrega ao vivo continua, o log fica em estado degradado com métrica e alerta, e a recuperação do trecho perdido não é garantida. |
| FR-RPL-06 | O estado por consumidor (próximo seqnum de saída, próximo seqnum de entrada esperado, época e cursor) DEVE ser persistido em arquivo. O flush por mensagem ou por intervalo é configurável, com o intervalo atual (5 s) como padrão. O log valida o CRC do último registro na abertura e descarta registro cortado por crash. A retenção do log é parametrizável, com padrão de 5 dias de pregão. Após queda sem shutdown limpo, o seqnum de saída NUNCA DEVE regredir: a abertura retoma de `max(estado persistido, último slot publicado no índice) + UncleanRestartSeqMargin`. Os números pulados são respondidos com gap-fill se o cliente os pedir. |
| FR-RPL-09 | O append no log faz parte do hot path e DEVE respeitar NFR-PERF-01. Meta de projeto: contribuição do append de p99 ≤ 3 µs, sem rotação de segmento síncrona no caminho normal (o próximo segmento é pré-alocado em background). A meta é critério de aceite do benchmark do TD-04, medido na carga de NFR-PERF-01 (50.000 mensagens/segundo por sessão) e não só no volume de referência diário. |
| FR-RPL-07 | `CursorAckMode` por dispatcher de broker: `OnBrokerAck` (padrão; avança o cursor na confirmação do broker, garante ao menos uma vez) ou `OnEnqueue` (avança no envio, no máximo uma vez). A validação de options DEVE recusar `Acks=0` no Kafka combinado com `OnBrokerAck`. Shutdown gracioso DEVE drenar os consumidores antes de encerrar. |
| FR-RPL-08 | O cliente WebSocket recupera pelo pedido `replay` na própria sessão, lendo do índice daquele `ClientId` no log. O frame guardado é o que entrou no log de entrada; a projeção (encoding e filtro de campos) continua acontecendo no consume loop, nunca no hot path. `seq` sem registro no intervalo vira um frame `gap` só nesse trecho. A retransmissão é só daquele `ClientId`. |

### 2.5 Cold Path Assíncrono — Log de Auditoria (FR-AUD)

| ID | Requisito |
|----|-----------|
| FR-AUD-01 | O sistema DEVE manter uma trilha de auditoria no cold path de todas as mensagens recebidas, decisões de roteamento e resultados de dispatchers sem bloquear o hot path. |
| FR-AUD-02 | O pipeline do log de auditoria DEVE usar `System.Threading.Channels.Channel<T>` (bounded, capaz de single-writer-multiple-reader) como mecanismo de desacoplamento entre o hot path e o audit writer. |
| FR-AUD-03 | O channel de auditoria DEVE ser configurado com uma **capacidade limitada** e uma estratégia de overflow `BoundedChannelFullMode.DropOldest` ou `DropWrite`. O hot path DEVE usar o `TryWrite` não bloqueante no channel de auditoria e NÃO DEVE aguardar ou bloquear nele. |
| FR-AUD-04 | O serviço de background audit writer DEVE consumir do channel e persistir registros de forma assíncrona em um ou mais sinks configuráveis. Sinks em escopo da v1: (a) log de arquivo estruturado via Serilog; (b) PostgreSQL com extensão TimescaleDB (persistência canônica das mensagens FIX recebidas). |
| FR-AUD-05 | Eventos de ciclo de vida de sessão (FR-ING-07) TAMBÉM DEVEM ser escritos no channel de auditoria. |
| FR-AUD-06 | O registro de auditoria de uma mensagem DEVE capturar no mínimo: `SessionId`, `IngressTimestamp` e `EgressTimestamp` (ticks monotônicos de `Stopwatch`, publicados pelo sink Serilog em microssegundos), `MsgType`, `ClOrdID` (se presente), `MsgSeqNum` (se presente), `DeliveryMode`, `DispatcherIds` alvo e o payload FIX bruto (`RawFix`, cópia alugada no cold path — nunca I/O no hot path). O sink PostgreSQL registra o timestamp UTC do writer em `ingress_ts`. |
| FR-AUD-07 | As gravações no log de auditoria DEVEM sobreviver a um shutdown gracioso: o sistema DEVE drenar o channel de auditoria completamente antes de o processo encerrar. |
| FR-AUD-08 | O sistema DEVE persistir as mensagens FIX recebidas de forma **assíncrona** no PostgreSQL (hypertable TimescaleDB). O hot path NÃO DEVE abrir conexões, executar SQL nem alocar strings de SQL. A cópia do payload para o `AuditRecord` ocorre após o fan-out, via `TryWrite` no channel de auditoria (ou um channel dedicado de persistência com a mesma semântica não bloqueante). |
| FR-AUD-09 | A escrita no PostgreSQL DEVE ser em lote (`COPY` binário ou `INSERT` em batch via `Npgsql`). Tamanho do lote e intervalo de flush DEVEM ser configuráveis. Falha de persistência DEVE ser registrada no Serilog e NÃO DEVE afetar o hot path nem o egress. |
| FR-AUD-10 | Persistência do payload bruto DEVE ser configurável por sessão: `Full` (bytes FIX completos), `MetadataOnly` (sem `RawFix`) ou `Sampled` (fração configurável). Default de desenvolvimento: `Full`. Default sugerido de produção em throughput extremo: `MetadataOnly` ou `Sampled` (ver design — orçamento de ingestão). |

---

## 3. Requisitos Não Funcionais

### 3.1 Performance (NFR-PERF)

| ID | Requisito |
|----|-----------|
| NFR-PERF-01 | Latência end-to-end no hot path (recebimento no socket → handoff ao dispatcher): **p50 ≤ 10 µs, p99 ≤ 50 µs, p999 ≤ 200 µs** sob 50k msg/s por sessão. |
| NFR-PERF-02 | O sistema DEVE sustentar **≥ 200.000 mensagens/segundo** de throughput agregado entre todas as sessões em uma máquina de referência com 4 cores, sem perda de mensagens no hot path (overflow de fila do dispatcher é uma preocupação de back-pressure separada). |
| NFR-PERF-03 | Coletas de GC Gen0 NÃO DEVEM ocorrer mais de uma vez por minuto sob carga em regime estacionário. Coletas Gen2 NÃO DEVEM ocorrer durante operação normal. Verificado com `dotnet-counters` e testes de alocação do BenchmarkDotNet. |
| NFR-PERF-04 | O sistema DEVE pré-alocar toda a memória de trabalho do hot path na inicialização (fase de warm-up). Os buffers de pipe do `PipeReader`, segmentos de `ArrayPool` e itens do channel do dispatcher DEVEM ser pré-aquecidos antes de a primeira sessão conectar. |

### 3.2 Confiabilidade e Tolerância a Falhas (NFR-REL)

| ID | Requisito |
|----|-----------|
| NFR-REL-01 | Um panic ou exceção não tratada no pipeline de ingestão de uma sessão DEVE ser isolado via limite de supervisão e NÃO DEVE derrubar sessões irmãs ou a camada de dispatchers. |
| NFR-REL-02 | O sistema DEVE tolerar partições de rede transitórias sem perda de mensagens no lado inbound, usando semântica de reconexão + gap-fill (ResendRequest) conforme o protocolo de sessão FIX. |
| NFR-REL-03 | No evento de um channel interno de dispatcher estar cheio (back-pressure), o motor de roteamento DEVE registrar o overflow no log de auditoria e continuar o processamento sem bloquear. |
| NFR-REL-04 | O sistema DEVE implementar uma sequência de shutdown limpa: (1) parar de aceitar novas conexões, (2) enviar FIX Logout para sessões ativas, (3) drenar channels do hot path e as tasks de consumo dos dispatchers, (4) fazer o flush final do log de sessão e gravar o marcador de shutdown limpo, (5) drenar channel de auditoria, (6) descartar todos os recursos gerenciados. |

### 3.3 Observabilidade (NFR-OBS)

| ID | Requisito |
|----|-----------|
| NFR-OBS-01 | O sistema DEVE expor contadores `System.Diagnostics.Metrics`: mensagens recebidas/s, mensagens despachadas/s (por dispatcher), histograma de latência do hot path (µs), profundidade da fila do dispatcher (por dispatcher), duração de pause de GC. |
| NFR-OBS-02 | O sistema DEVE emitir logs estruturados via `Microsoft.Extensions.Logging` com `Serilog` como provedor de sink (configurável: console, arquivo rolling ou endpoint seq). |
| NFR-OBS-03 | O sistema DEVE expor um endpoint de métricas compatível com OpenTelemetry (`/metrics` em formato de scrape Prometheus) para integração com stacks de monitoramento padrão. |
| NFR-OBS-04 | Spans de `ActivitySource` DEVEM instrumentar o hot path nas fronteiras de session-receive e dispatcher-handoff para rastreamento distribuído, com taxa de sampling configurável para 0% a fim de evitar overhead em produção. |

### 3.4 Segurança (NFR-SEC)

| ID | Requisito |
|----|-----------|
| NFR-SEC-01 | Sessões FIX/SBE outbound e inbound DEVEM suportar autenticação mútua TLS 1.2/1.3 (mTLS) opcional com caminhos de certificado configuráveis. |
| NFR-SEC-02 | Credenciais (FIX Password, SASL do Kafka, senhas do RabbitMQ, connection string / senha do PostgreSQL) NÃO DEVEM ser armazenadas em texto simples em arquivos de configuração. A integração com o .NET Secret Manager ou injeção por variável de ambiente DEVE ser suportada. |
| NFR-SEC-03 | Todos os endpoints externos (brokers Kafka, nós RabbitMQ, endereço de escuta do acceptor FIX, endereço de escuta do acceptor WebSocket, PostgreSQL) DEVEM ser configuráveis. Nenhum endpoint DEVE ser hardcoded. |
| NFR-SEC-04 | O acceptor WebSocket DEVE autenticar o Logon com um token lido da variável de ambiente nomeada no cadastro do cliente. O valor do token NÃO DEVE aparecer em configuração, URL ou log. mTLS NÃO é exigido nesse acceptor: o cliente é aplicação web e autentica pelo token; TLS de servidor permanece opcional. |

### 3.5 Manutenibilidade e Extensibilidade (NFR-MAINT)

| ID | Requisito |
|----|-----------|
| NFR-MAINT-01 | Adicionar um novo tipo de dispatcher de egress DEVE requerer apenas: (a) implementar `IMessageDispatcher`, (b) registrar a implementação no container de DI. Nenhuma alteração no motor de roteamento ou hot path é necessária. |
| NFR-MAINT-02 | Adicionar um novo formato de framing de sessão DEVE requerer apenas implementar `ISessionFrameDecoder` e registrá-lo. |
| NFR-MAINT-03 | A solução DEVE ter como alvo o **.NET 10 LTS** e ser buildável com o .NET 10 SDK sem toolchains adicionais. |
| NFR-MAINT-04 | Todas as interfaces e abstrações públicas DEVEM ter comentários de documentação XML. |

---

## 4. Restrições e Premissas

- **Runtime Alvo:** .NET 10 LTS em Linux (x86-64, kernel ≥ 5.15 com suporte a SO_REUSEPORT). Compatibilidade com Windows é desejável, mas não é uma restrição primária.
- **Escopo de Protocolo da Bolsa:** Os protocolos de nível de sessão FIX 4.2, FIX 4.4 e FIX 5.0/FIXT 1.1 estão em escopo. Encoding/decoding SBE é uma extensão futura; a arquitetura deve acomodá-la.
- **Kafka ambiente-agnóstico:** O único cliente Kafka é `Confluent.Kafka`. Amazon MSK, MSK Serverless (via bootstrap IAM, se habilitado), Kafka auto-gerenciado na AWS (EC2/EKS), Confluent Cloud, Redpanda e Kafka em Docker DEVEM ser alcançados só com options. Não há SDK AWS no caminho padrão; IAM MSK é opt-in.
- **Persistência de mensagens (cold path):** PostgreSQL 16+ com extensão **TimescaleDB** é o store canônico das mensagens FIX recebidas, para auditoria e histórico (`ingress_fix_messages`). Não há RDBMS no hot path. A retransmissão por cliente usa o log de sessão em arquivo (append-only, por dia de pregão e por época), não o banco; ver FR-RPL e `design.md` §3.4.3. Sequence numbers e cursores por consumidor ficam nesse log. Não se adiciona banco não relacional para replay.
- **Persistência no Hot Path, só no log de sessão:** Além do log de sessão em arquivo, que grava cada mensagem antes do fan-out com escritor único e sem bloquear o roteamento, não há persistência no hot path. Leituras de banco ocorrem apenas na inicialização (health/schema check); escritas de auditoria são exclusivamente no cold path, em lote.
- **Single-Process, Multi-Threaded:** O NexusRouter roda como um único processo de SO. Scale-out horizontal é uma preocupação futura e não deve restringir o design inicial.
- **Injeção de Dependência:** O sistema usa `Microsoft.Extensions.DependencyInjection` e o Generic Host (`IHostedService`) como container de runtime.
- **Ambiente local:** `docker-compose.yml` na raiz sobe Kafka (KRaft, PLAINTEXT), RabbitMQ, e PostgreSQL+TimescaleDB para desenvolvimento e testes.
