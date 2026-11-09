# Notas — Log de sessão em arquivo (seqnum, reenvio e cursor por dispatcher)

> **Status (2026-11-08):** as decisões da tabela abaixo foram aplicadas em `requirements.md`,
> `design.md` (§3.4.3 a §3.4.5, §4.8, §5, §7 a §10) e `tasks.md` (TD-04 a TD-08, Fase 9, T-50 a T-53).
> Esta nota fica como registro da discussão. Em caso de divergência, vale a spec.
> Mudanças feitas na revisão de 2026-11-08: índice por cliente de 16 bytes (não 8) com `kind`; frame
> original gravado pela sessão de ingress antes do rewriter; seq de saída salta e nunca regride após
> queda; API HTTP de replay dos brokers removida; falha do log não para o roteamento.
> Origem: conversa de 2026-11-06 sobre `seqnum-rethink.md` e `replay-rethink.md`.
> Base: leitura de `SequenceNumberStore`, `FanOutEngine`, `init.sql` e das duas notas.
> **Não lido:** `FixOutboundDispatcher.TryEnqueue`, `FixSession` por inteiro e a política de
> `RawFixRetention`. O que depende deles está marcado como a confirmar.

## Decisões já tomadas

| # | Decisão |
|---|---|
| 1 | Log de entrada (o que o counterpart/B3 manda) e log de saída por cliente entram no mesmo escopo. |
| 2 | Entrega aos brokers (Kafka, RabbitMQ) é **ao menos uma vez**. O consumidor deduplica por um header de identidade. |
| 3 | Cliente que reconecta recebe a partir do seqnum que ele informar. Não existe reenvio automático do dia inteiro. |
| 4 | O arquivo é o log de que a sessão depende. O Timescale fica para auditoria e histórico. |
| 5 | O log grava todos os frames, administrativos incluídos. No reenvio, administrativas viram gap-fill (como o QuickFIX). |
| 6 | Retenção parametrizável, padrão de 5 dias de pregão. |
| 7 | O filtro de roteamento por tag sai do backlog. |
| 8 | `CursorAckMode` configurável por dispatcher de broker (`OnBrokerAck` ou `OnEnqueue`). |
| 9 | Volume de referência: 500 mil mensagens por dia de pregão. |

## 1. Problema

1. O fan-out descarta a mensagem quando a rota não tem dispatcher ou quando `TryEnqueue` recusa
   (channel cheio e, a confirmar, sessão FIX sem cliente conectado). Para um cliente FIX, o
   descarte vira um buraco de seqnum que ele não consegue pedir de volta.
2. O initiator não persiste seqnum de saída nem valida o de entrada. O acceptor persiste só a saída.
3. Não há store de mensagens, então `ResendRequest` só responde com gap-fill.
4. A auditoria é assíncrona, com modo de overflow. Não serve como fonte de recuperação.

## 2. Ideia central

Gravar a mensagem num log **antes** do fan-out. O dispatcher deixa de ser um destino que pode estar
ausente e passa a ser um consumidor com cursor, como no Kafka.

- Channel cheio vira lag do consumidor, não perda.
- Cliente offline fica atrás no log e alcança ao reconectar.
- O hot path não espera consumidor lento.

O fan-out em memória continua como caminho rápido. Se o consumidor está em dia, recebe pelo channel.
Se o cursor dele não bate com a mensagem que chegou, ele lê do log até alcançar.

## 3. Modelo

### 3.1 Dois tipos de log

| Log | Conteúdo | Chave |
|---|---|---|
| Entrada (por sessão de ingress) | Frames recebidos do counterpart, como chegaram | `(sessão, época, seq de entrada)` |
| Saída (por cliente) | Índice que aponta para o payload do log de entrada | `(cliente, época, seq do cliente)` |

O payload fica gravado uma vez. Cada cliente tem só o mapeamento do seu seqnum para a posição no
log de entrada. Isso resolve a duplicação do item 1 do `replay-rethink.md`.

Para o initiator (nós mandando para a B3), o log de saída guarda os frames que enviamos, se a B3
exigir reenvio real (depende do TD-03).

### 3.2 Época

Um reset (`141=Y` ou virada do pregão) começa uma nova época. O seqnum 5 de antes e o de depois
são mensagens diferentes. A chave inclui a época. Arquivos da época anterior ficam arquivados, não
apagados, até a retenção.

### 3.3 Formato dos segmentos

Segmentos append-only por dia de pregão e época, no estilo Kafka:

- `.log`: registros `[len][seq][ts][crc][payload]`.
- `.index` denso: o seqnum FIX é contíguo, então a entrada de índice `seq - base` guarda o offset
  do registro. Busca O(1), 8 bytes por mensagem (cerca de 8 MB por milhão).
  Índice esparso só compensa se esse custo importar.
- Escritor único por stream de ingress, sem lock. Arquivo mapeado em memória.
- Na abertura, o `crc` do último registro valida o fim do arquivo. Registro cortado por crash é descartado.

### 3.4 Estado por consumidor

Arquivo pequeno por dispatcher e por cliente:

| Campo | Uso |
|---|---|
| Próximo seqnum de saída | Substitui o `.seq` atual |
| Próximo seqnum de entrada esperado | Validação do `34` (TD-04, TD-07) |
| Época | Chave do log |
| Cursor | Último offset confirmado, para brokers |

O flush por mensagem ou por intervalo é a opção do TD-09. Mapeamento em memória sobrevive a crash do
processo. Queda do sistema operacional pode perder o que não foi sincronizado.

## 4. Entrega por tipo de dispatcher

| Tipo | Como recupera | Garantia |
|---|---|---|
| FIX (acceptor) | `ResendRequest` ou Logon com seqnum esperado. Lê do índice do cliente a partir do seqnum informado. Reenvia com `43=Y` e `122`. Gap-fill só para administrativas. | Seqnum e `PossDupFlag` identificam duplicata. |
| Kafka, RabbitMQ | Cursor persistido. No restart, retoma do cursor. | Ao menos uma vez. Pode repetir a última mensagem. |
| WebSocket | Pedido `replay` com o seqnum (T-52). | Igual ao FIX. |

### 4.1 Header de idempotência (brokers)

Cada mensagem enviada ao broker leva um header com identidade estável:

`<sessão de origem>:<época>:<seq de entrada>`

O consumidor guarda o último valor visto por sessão e descarta repetidas. Como o seqnum é monotônico
dentro da época, basta comparar com o último, sem guardar um conjunto.

**A confirmar:** formato exato do header e se os dois brokers aceitam o mesmo nome. Kafka usa record
headers. RabbitMQ usa headers de mensagem.

## 5. Custos e riscos

| Item | Risco | Mitigação |
|---|---|---|
| Append no hot path | Latência. | Benchmark antes de aceitar. Escritor único, cópia em memória mapeada. Já previsto no TD-09. |
| Reenvio com `43` e `122` | Muda o tamanho do frame. O `PassThroughRewriter` in-place não cobre. | Depende do item 1 de `seqnum-rethink.md` (rewriter com mudança de tamanho). Entra como pré-requisito. |
| Corrupção e rotação | Código novo e crítico. | Crc por registro, validação na abertura, testes de crash com escrita cortada. |
| Disco | Cliente offline não faz o log crescer, mas o dia cresce com o volume. | Retenção por dia, padrão de 5 dias, parametrizável. Estimativa na seção 8.3. |
| Fsync | Custo alto por mensagem. | Opção do operador, padrão por intervalo. |
| Dependência do TD-03 | Se a B3 não exigir reenvio real, o log de saída do initiator cai. | TD-03 vem primeiro. |

## 6. Timescale

Não sai do projeto. Continua com a auditoria (`ingress_fix_messages`) e o histórico de longo prazo.
Deixa de ser candidato a fonte de recuperação de sessão por três motivos: a gravação é assíncrona,
a sessão FIX passaria a depender do banco, e inserir por mensagem custa ms.

## 7. Impacto nas tasks (proposta, não aplicada)

| Task | Proposta |
|---|---|
| TD-03 | Mantém e vem primeiro. Responde se a B3 exige reenvio real. |
| TD-04, TD-08, TD-09 | Viram uma peça: abstração do log/store de sessão com implementação em arquivo. Reordenar. |
| TD-05, TD-06 | Sem mudança de escopo. Seguem depois do TD-04. |
| TD-07 | Usa a mesma abstração para validar o seqnum de entrada do cliente. |
| T-44 | Cadastro de cliente e retenção continua útil. A escolha diária de retenção pode deixar de existir se o log for sempre gravado. A decidir. |
| T-45 | Substituída: a hypertable `client_replay_messages` dá lugar ao log em arquivo e ao índice por cliente. |
| T-46 | Passa a ler do índice do cliente. Reenvio real em vez de GapFill único. |
| T-47 | A API HTTP de recuperação para brokers perde a razão de ser se o cursor resolver. Rever. |
| T-48, T-52 | Ajustar aos novos testes e ao pedido `replay` por seqnum. |

## 8. Perguntas que estavam em aberto, agora decididas

| # | Pergunta | Decisão |
|---|---|---|
| 1 | O log grava todos os frames ou só os de aplicação? | **Todos**, administrativos incluídos. Ver 8.1. |
| 2 | Retenção do arquivo | **Parametrizável. Padrão de 5 dias de pregão.** |
| 3 | Filtro de roteamento por cliente (`replay-rethink.md` §2) | **Fora do backlog.** O índice por cliente aponta para todas as mensagens da rota. |
| 4 | Cursor com `Acks=0` | **Configurável por dispatcher.** Ver 8.2. |
| 5 | Volume para dimensionar | **500 mil mensagens por dia de pregão.** Ver 8.3. |

### 8.1 Todos os frames, como o QuickFIX

O QuickFIX (conhecimento da lib, não consultado na documentação) grava no `MessageStore` toda
mensagem enviada, administrativa ou não, porque cada uma consome um seqnum. Na hora do reenvio ele
decide: mensagem de aplicação é reenviada com `PossDupFlag=Y` e `OrigSendingTime`. Mensagem
administrativa (Heartbeat, TestRequest, Logon, entre outras) é substituída por `SequenceReset-GapFill`,
juntando as vizinhas em um único gap-fill. Ele não guarda as mensagens recebidas, só o próximo seqnum
esperado.

Aqui o log de entrada também grava os frames recebidos, o que o QuickFIX não faz. A regra de reenvio
fica a mesma do QuickFIX:

- Aplicação: reenvia com `43=Y` e `122`.
- Administrativa: gap-fill agrupado.
- Qualquer reenvio de saída respeita o `EndSeqNo` e o limite de 10.000 mensagens por pedido da B3
  (`PossMissingApplMsg=Y` quando passa disso).

Tipos administrativos a confirmar no dicionário `docs/FIX44Entrypoint.xml`.

### 8.2 Cursor e `Acks`

O cursor avança quando o broker confirma. Nova opção por dispatcher de broker, com nome a definir
(`CursorAckMode`), em dois valores:

| Valor | Quando o cursor avança | Garantia |
|---|---|---|
| `OnBrokerAck` | Depois da confirmação do broker (`Acks=1` ou `all` no Kafka, publisher confirms no RabbitMQ) | Ao menos uma vez |
| `OnEnqueue` | Logo depois de entregar ao cliente da lib, sem esperar o broker | No máximo uma vez. Uma falha entre o envio e o broker perde a mensagem |

`Acks=0` no Kafka só combina com `OnEnqueue`, porque não existe confirmação para esperar. A validação
de options deve recusar `Acks=0` com `OnBrokerAck`. O padrão sugerido é `OnBrokerAck`, que é o que
sustenta a decisão de "ao menos uma vez".

### 8.3 Dimensionamento (500 mil mensagens por dia)

O tamanho médio do frame é uma suposição minha, de 300 a 500 bytes para ExecutionReport e semelhantes.
Precisa ser medido com tráfego real antes de fixar valores.

| Item | Conta | Resultado |
|---|---|---|
| Log de um stream por dia | 500 mil × 300 a 500 B | cerca de 150 a 250 MB |
| Índice denso por dia | 500 mil × 8 B | cerca de 4 MB |
| Índice por cliente | 500 mil × 8 B por cliente | cerca de 4 MB por cliente |
| Retenção padrão | 5 dias | cerca de 0,75 a 1,25 GB por stream, mais 20 MB de índice por stream |

O índice denso de 8 bytes por mensagem é pequeno a esse volume, então fica a decisão de usar índice
denso em vez de esparso. Os 500 mil por dia vêm a ~6 mensagens por segundo na média, mas o pico do
pregão é o que importa para o benchmark do append. O valor de pico não foi informado.

## Ligações

- `notes/seqnum-rethink.md` §1 (rewriter), §4 (cenários), §7 (lacunas de sessão).
- `notes/replay-rethink.md` §1 (duplicação), §2 (filtro).
- Código: `src/NexusRouter.Egress/Fix/SequenceNumberStore.cs`, `src/NexusRouter.Router/FanOutEngine.cs`,
  `docker/postgres/init.sql`.
