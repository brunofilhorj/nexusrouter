# NexusRouter — Requisitos Funcionais e Não Funcionais

> **Status do Documento:** Rascunho v1.0  
> **Classificação:** Especificação de Engenharia Interna  
> **Domínio:** High-Frequency Trading (HFT) — Motor de Execução de Ordens / DropCopy Distribution Engine

---

## 1. Visão Geral do Sistema

O NexusRouter é um motor de ingestão, roteamento e distribuição de alta performance para streams de Execução de Ordens e DropCopy. Atua como camada intermediária entre as sessões de conectividade upstream com a bolsa (FIX/SBE) e os consumidores downstream (clientes FIX, tópicos Kafka, exchanges RabbitMQ). Sua restrição definidora é a **latência end-to-end ultra-baixa e determinística** em todo o hot path — do recebimento do byte até o handoff ao dispatcher.

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
| FR-EGR-02 | O sistema DEVE suportar no mínimo três implementações de dispatcher concorrentes: `KafkaMessageDispatcher`, `RabbitMQMessageDispatcher` e `FixOutboundDispatcher`. |
| FR-EGR-03 | O `FixOutboundDispatcher` DEVE manter sessões FIX outbound persistentes para clientes downstream e gerenciar seus próprios sequence numbers de forma independente dos sequence numbers da sessão inbound. |
| FR-EGR-04 | O motor de roteamento DEVE suportar **fan-out**: uma única mensagem inbound pode ser despachada concorrentemente para múltiplas instâncias registradas de `IMessageDispatcher`. O fan-out NÃO DEVE criar dependências síncronas entre dispatchers — um dispatcher lento ou com falha NÃO DEVE bloquear ou degradar a entrega para os demais. |
| FR-EGR-05 | Cada instância de dispatcher DEVE ter seu próprio buffer/channel de envio isolado. A back-pressure de um consumidor downstream DEVE ser absorvida pela fila interna daquele dispatcher e NÃO DEVE se propagar de volta ao receive loop do hot path. |
| FR-EGR-06 | O registro de dispatchers e as regras de roteamento (qual sessão roteia para qual(is) dispatcher(s)) DEVEM ser definidos em configuração e carregados na inicialização. Mutação dinâmica de rotas em tempo de execução é desejável, mas está fora do escopo da v1. |
| FR-EGR-07 | Cada dispatcher DEVE implementar um ciclo de vida `StartAsync` / `StopAsync` alinhado ao padrão `IHostedService` do .NET para inicialização limpa e shutdown gracioso. |
| FR-EGR-08 | O `KafkaMessageDispatcher` DEVE publicar em tópicos Kafka configuráveis usando o producer `Confluent.Kafka` em modo fire-and-forget (acks=0 para latência mínima) OU com política de confirmação configurável. A estratégia de tópico e chave de partição DEVE ser configurável por instância de dispatcher. |
| FR-EGR-09 | O `RabbitMQMessageDispatcher` DEVE publicar em exchanges/routing keys configuráveis usando a biblioteca oficial `RabbitMQ.Client` com um channel dedicado por dispatcher para evitar contenção de channel. |
| FR-EGR-10 | Erros de dispatcher (falha de publicação, perda de conexão) DEVEM ser capturados, registrados no log de auditoria estruturado, e NÃO DEVEM propagar exceções para o hot path. |

### 2.5 Cold Path Assíncrono — Log de Auditoria (FR-AUD)

| ID | Requisito |
|----|-----------|
| FR-AUD-01 | O sistema DEVE manter uma trilha de auditoria no cold path de todas as mensagens recebidas, decisões de roteamento e resultados de dispatchers sem bloquear o hot path. |
| FR-AUD-02 | O pipeline do log de auditoria DEVE usar `System.Threading.Channels.Channel<T>` (bounded, capaz de single-writer-multiple-reader) como mecanismo de desacoplamento entre o hot path e o audit writer. |
| FR-AUD-03 | O channel de auditoria DEVE ser configurado com uma **capacidade limitada** e uma estratégia de overflow `BoundedChannelFullMode.DropOldest` ou `DropWrite`. O hot path DEVE usar o `TryWrite` não bloqueante no channel de auditoria e NÃO DEVE aguardar ou bloquear nele. |
| FR-AUD-04 | O serviço de background audit writer DEVE consumir do channel e persistir registros em um sink configurável (log de arquivo estruturado via Serilog, ou banco de dados de série temporal) de forma assíncrona. |
| FR-AUD-05 | Eventos de ciclo de vida de sessão (FR-ING-07) TAMBÉM DEVEM ser escritos no channel de auditoria. |
| FR-AUD-06 | O registro de auditoria de uma mensagem DEVE capturar no mínimo: `SessionId`, `IngressTimestamp` (UTC ticks, timestamp de hardware se disponível), `MsgType`, `ClOrdID` (se presente), `DeliveryMode`, `DispatcherIds` alvo e `EgressTimestamp`. |
| FR-AUD-07 | As gravações no log de auditoria DEVEM sobreviver a um shutdown gracioso: o sistema DEVE drenar o channel de auditoria completamente antes de o processo encerrar. |

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
| NFR-REL-04 | O sistema DEVE implementar uma sequência de shutdown limpa: (1) parar de aceitar novas conexões, (2) enviar FIX Logout para sessões ativas, (3) drenar channels do hot path, (4) drenar channel de auditoria, (5) descartar todos os recursos gerenciados. |

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
| NFR-SEC-02 | Credenciais (FIX Password, segredos SASL do Kafka, senhas do RabbitMQ) NÃO DEVEM ser armazenadas em texto simples em arquivos de configuração. A integração com o .NET Secret Manager ou injeção por variável de ambiente DEVE ser suportada. |
| NFR-SEC-03 | Todos os endpoints externos (brokers Kafka, nós RabbitMQ, hosts FIX downstream) DEVEM ser configuráveis. Nenhum endpoint DEVE ser hardcoded. |

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
- **Sem Persistência de Estado no Hot Path:** Não há RDBMS no caminho crítico. O estado de sequence number pode ser persistido de forma assíncrona em um store leve (arquivo/Redis) para recuperação de crash, mas leituras ocorrem apenas na inicialização da sessão.
- **Single-Process, Multi-Threaded:** O NexusRouter roda como um único processo de SO. Scale-out horizontal é uma preocupação futura e não deve restringir o design inicial.
- **Injeção de Dependência:** O sistema usa `Microsoft.Extensions.DependencyInjection` e o Generic Host (`IHostedService`) como container de runtime.
