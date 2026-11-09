# Notas — Repensar a retransmissão (replay / FR-RPL)

> **Status:** rascunho de rediscussão. Não é decisão tomada.
> Não altera `requirements.md`, `design.md` nem `tasks.md`. Serve de insumo para
> revisar a Seção 2.4.1 (FR-RPL) dos requisitos e a Seção 3.4.3 do design antes
> de implementar as tasks T-44 a T-48 e T-52 (todas ainda abertas).

## Contexto

O desenho atual de retransmissão (FR-RPL) guarda, por cliente que pediu retenção no
dia, os bytes que aquele cliente recebeu, na hypertable `client_replay_messages` com
chave `(client_id, dia UTC, replay_seq)`. O `replay_seq` é o sequence number outbound
daquele cliente (MsgSeqNum da sessão FIX, `seq` do envelope no WebSocket, contador
próprio no broker). O cadastro `Clients` (`ClientId` + `DispatcherId` + `FixSenderCompId`
ou `TokenEnvVar`) existe só como options validadas no Host; nada consome a lista ainda.

Três questões em aberto apontam para tensões de eficiência e de simetria entre os
tipos de dispatcher. Registradas abaixo com o que o design atual diz e as direções a
explorar.

---

## 1. Duplicação de gravação com N clientes em retenção

**Observação.** Com o modelo atual, se 2 ou mais clientes do mesmo dispatcher pedirem
retenção, a mesma mensagem lógica é gravada 2 ou mais vezes (uma linha por cliente).

**O que o design diz (confirma a observação).** FR-RPL-03/06 e Seção 3.4.3: grava-se
"o byte que aquele cliente ia receber", com `replay_seq` próprio de cada cliente. A
cópia por cliente é intencional, porque o conteúdo **não é idêntico** entre clientes:

- FIX: o frame carrega o MsgSeqNum (tag 34) da sessão daquele cliente.
- WebSocket: o frame já vem projetado com o encoding e o filtro de campos daquela conexão.
- Broker: `replay_seq` sintético por `ClientId`, a partir do opt-in.

Logo, N clientes opted-in = N cópias. 10 clientes = 10x a mesma mensagem.

**Direções a explorar.**
- **Normalizar o payload.** Guardar o payload canônico uma vez e, por cliente, só o
  mapeamento `(client_id, replay_seq) -> ref do payload + o que varia (o seq)`. Troca
  espaço de escrita por um join na leitura do replay. Precisa resolver o caso em que o
  conteúdo realmente difere (projeção WebSocket, reescrita de tag 34 no FIX).
- **Avaliar o volume real.** Medir quantos clientes por dispatcher costumam optar por
  retenção. Se for tipicamente 1, a duplicação é teórica e não justifica normalizar.
- **Separar "o que foi enviado" de "o seq de cada um".** O MsgSeqNum pode ser derivado
  na leitura em vez de gravado por linha, se o store guardar a âncora por sessão.

**Impacto se mudar:** afeta FR-RPL-03/06, o schema de `client_replay_messages`
(`init.sql`) e as tasks T-45/T-46/T-47.

---

## 2. Filtro de roteamento por tag de negócio (distinto da projeção de campos)

> **Decisão de 2026-11-06 (`notes/session-log-rethink.md`):** fora do backlog. Não há task
> para este item. Mantido abaixo só como registro histórico.

**Observação.** Filtrar por tag **não** no nível de payload. Dois eixos ortogonais:

- **Projeção de payload** (o WebSocket já faz com `fields`): a mensagem vai, porém
  enxuta — menos bytes na ponta. Está OK, é um recurso do WebSocket.
- **Filtro de roteamento** (o que falta): decide **se** a mensagem vai para aquele
  cliente, com base em valores de tag — ClOrdID (11), Symbol (55), ou a identificação
  da sessão/origem FIX (SenderCompId 49 de origem, ou ClearingAccount 440).

**O que o design diz.** Hoje o roteamento é só por sessão de ingresso
(`Sessions[].Routes`). Não há filtro por conteúdo por consumidor. A projeção existe só
no WebSocket (FR-EGR-12, design 3.3.1); FIX e broker entregam a mensagem inteira.

**Direções a explorar.**
- **Segundo nível de roteamento por predicado.** Um filtro por consumidor na forma
  `(tag, valor(es)) -> inclui/exclui`, avaliado antes de enfileirar para cada cliente.
- **Tokenização barata.** No modo PassThrough o frame passa cru; filtrar por 11/55/440
  exige ler só essas tags (não parse completo) e deve ficar **fora do hot path**, no
  consume loop do dispatcher, como já é a projeção do WebSocket.
- **Confirmar as tags no dicionário B3.** Validar em `docs/FIX44Entrypoint.xml` se 440
  (ClearingAccount) é a tag certa para segregar por participante/conta. 440 é campo B3
  real — diferente da 9001, que é convenção inventada pelo NexusRouter.
- **Onde declarar o filtro.** Candidato: no cadastro `Clients` (por cliente) ou no
  Logon (FIX via tag convencionada, WebSocket via campo do Logon, broker via API).

**Impacto se mudar:** novo conceito de "filtro de roteamento por consumidor"; afeta
`ClientOptions`, o fan-out/consume loop de cada dispatcher e, possivelmente, o formato
de Logon. Conversa diretamente com o item 3 (a tokenização das tags alimenta a
routing key/headers do broker).

---

## 3. Segregação nativa nos dispatchers de fila (Kafka e RabbitMQ)

**Observação.** Hoje as mensagens vão para um único tópico/fila para N consumers. Os
dois brokers têm recursos próprios de segregação que o design atual ignora ao tratar
broker como fan-out para um destino compartilhado. Explorar o que cada um oferece.

**O que o design diz.** FR-RPL-04 e Seção 3.4.3: "Kafka e RabbitMQ não identificam o
assinante". Por isso o replay de broker foi jogado para uma API HTTP paralela
(`/v1/clients/{id}/replay`) com `replay_seq` sintético — uma forma de "fingir" a
segregação por cliente que FIX e WebSocket têm nativamente (uma sessão por cliente).

### RabbitMQ — segregação no servidor

Tem roteamento no broker (exchange + binding). Em vez da routing key fixa atual
(`fix.raw`), o router carimba a mensagem e o broker entrega só o recorte certo:

- **Topic exchange** — routing key hierárquica com wildcards, ex.
  `fix.<conta440>.<symbol>`. Cada consumidor faz bind no slice que quer
  (`fix.ACCT1.#`, `fix.*.PETR4`). A segregação vive no binding; o produtor não muda
  quando entra consumidor novo. Default recomendado para 1–2 dimensões.
- **Headers exchange** — roteia por pares chave/valor nos headers com `x-match: all|any`.
  Certo quando a decisão combina atributos independentes (conta E símbolo E tipo) que
  não cabem numa string hierárquica. Mais caro, mais expressivo para filtro multi-tag.

Referências: [tipos de exchange (RabbitMQ docs)](https://www.rabbitmq.com/docs/4.1/exchanges),
[topic vs headers](https://codemia.io/knowledge-hub/path/rabbitmq_exchange_type_comparison_topic_vs_header),
[escolha do exchange pela forma do consumer set](https://acemq.com/blogs/rabbitmq-exchanges-queues-bindings-microservices/).

### Kafka — sem decisão na entrega

O broker não inspeciona o conteúdo e não há ponto de decisão na entrega: o registro é
escrito uma vez e cada consumer group lê tudo no seu offset. A segregação server-side
do RabbitMQ não existe igual. Alternativas:

- **Partition key** — mesma key cai sempre na mesma partição (ex. Symbol ou
  SenderCompId). Co-localiza e preserva ordem por chave, mas **não é filtro por
  consumidor**: o consumer group ainda lê todas as partições (salvo assignment manual).
- **Record headers + filtro no consumidor** — produtor carimba headers (`clOrdId`,
  `symbol`, `senderCompId`); o consumidor lê sem desserializar e descarta o que não
  casa. Idiomático, mas **client-side**: todos recebem os bytes, cada um filtra.
- **Tópico por critério** — tópico por conta/participante (`fix.orders.ACCT1`).
  Isolamento real e ACL por tópico, ao custo de proliferação de tópicos. Viável por
  participante (número controlado); inviável por ClOrdID.

Referências: [partition key (Confluent)](https://www.confluent.io/learn/kafka-partition-key/),
[Kafka headers para metadata/routing](https://aitorcto.substack.com/p/kafka-headers-for-clean-event-metadata),
[padrão de routing key no Kafka](https://dev.to/leobaniak/signadots-kafka-routing-key-trick-one-broker-many-parallel-tests-2jpg).

### Assimetria a assumir no design

- **RabbitMQ:** segregação **no servidor** (topic/headers exchange) → perto do modelo
  FIX/WebSocket; o broker entrega só o recorte certo, sem `replay_seq` sintético.
- **Kafka:** sem decisão na entrega → ou **client-side** (headers), ou **estrutural**
  (tópico por grupo), ou só **ordenação** (partition key). Nenhum é "filtro por
  cliente" de graça.

O modelo não fica simétrico: custo e lugar do filtro diferem por broker. A tokenização
das tags (11, 55, 440) do item 2 é o insumo comum para a routing key/headers do
RabbitMQ e para os headers/tópico do Kafka.

**Impacto se mudar:** afeta FR-RPL-04, as options dos dispatchers de broker
(`KafkaDispatcherOptions`, `RabbitMQDispatcherOptions` — hoje topic/exchange+routing key
fixos), o publish de cada dispatcher e a estratégia de replay de broker (talvez
dispensando parte da API paralela se a segregação virar nativa).

> Nota de conformidade: trechos das fontes externas foram reescritos/resumidos para
> respeitar restrições de licenciamento.

---

## Próximos passos sugeridos

1. Medir o volume real de clientes em retenção por dispatcher (decide o peso do item 1).
2. Confirmar as tags de filtro candidatas (11, 55, 49, 440) no dicionário B3
   (`docs/FIX44Entrypoint.xml`).
3. Decidir onde o filtro de roteamento é declarado (cadastro `Clients` vs Logon vs API).
4. Escolher a estratégia de broker por tipo (RabbitMQ topic/headers; Kafka
   headers/tópico/partition) e revisar se o replay paralelo ainda é necessário.
5. Revisar FR-RPL e a Seção 3.4.3 com as decisões antes de retomar T-44.
