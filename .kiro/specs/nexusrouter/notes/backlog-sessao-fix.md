# Notas — Sessão FIX, seqnum, log de sessão e retransmissão

> **Status (2026-11-10):** consolida `seqnum-rethink.md`, `session-log-rethink.md` e
> `replay-rethink.md`, que foram removidas. O que virou decisão está em `requirements.md`
> (FR-RPL), `design.md` (§3.4.3 a §3.4.7, §4.8) e `tasks.md`. Em caso de divergência, vale a spec.
> O texto completo das notas antigas está no histórico do git.

## 1. Decisões tomadas

| # | Decisão | Onde está |
|---|---|---|
| 1 | Log de entrada e índice por cliente no mesmo escopo. O payload fica gravado uma vez. | design §3.4.3 |
| 2 | Entrega aos brokers é ao menos uma vez, com header `<sessão>:<época>:<seq de entrada>` para deduplicar. | design §3.4.3, TD-04b |
| 3 | Cliente que reconecta recebe a partir do seqnum que informar. Não há reenvio automático do dia inteiro. | design §3.4.6, §3.4.7 |
| 4 | O arquivo é o log de que a sessão depende. O Timescale fica para auditoria e histórico. | design §3.4.1 |
| 5 | O log grava todos os frames. No reenvio, administrativas viram gap-fill (como o QuickFIX). | TD-08 |
| 6 | Retenção parametrizável, padrão de 5 dias de pregão. | `SessionLog.RetentionTradingDays` |
| 7 | Filtro de roteamento por tag de negócio por consumidor: **fora do backlog**. | — |
| 8 | `CursorAckMode` por dispatcher de broker (`OnBrokerAck` ou `OnEnqueue`). | TD-04b |
| 9 | Volume de referência: 500 mil mensagens por dia de pregão. Estimativa (frame de 300 a 500 B, a medir): log de ~150 a 250 MB/dia por stream, índice denso de ~4 MB/dia, ~4 MB/dia por cliente; 5 dias ≈ 0,75 a 1,25 GB por stream. | design §9 |
| 10 | API HTTP de replay para brokers não será implementada. O cursor persistido cobre a recuperação. | T-47 |
| 11 | Rewriter com mudança de tamanho, sem padding com espaços. | TD-02 |
| 12 | Hypertable `client_replay_messages` não será criada. | T-45 (removida) |

## 2. Fatos da B3 levantados nos PDFs

Levantamento por busca de texto em `docs/`, não leitura integral.

| Assunto | O que consta |
|---|---|
| Reset diário | Ao fim da sessão o gateway derruba a conexão e o seqnum volta a 1. Não há Logout no encerramento. |
| Drop Copy | Reset de terça a quinta às 23:00 BRT, com todos os clientes desconectados. De domingo para segunda o número não é resetado. |
| `ResetSeqNumFlag` (141) | Opcional no Logon. Os dois lados zeram o seqnum. |
| `NextExpectedMsgSeqNum` (789) | Removido do Logon; o PDF registra a v1.3.1 (ver `b3-guidelines.md` §1). No acceptor do router a tag continua opcional por acordo bilateral (design §3.4.6). |
| `ResendRequest` | Limite de 10.000 mensagens por pedido; acima disso, `PossMissingApplMsg` (35033)=`Y`. Um pedido por vez. |
| Certificação | Cenário A4 do Drop Copy pede `ResendRequest` depois de desconectar e conferir o primeiro e o último SeqNum. |
| Não consta nos PDFs | A regra "Logout com MsgSeqNum too low leva a `141=Y`" (é do steering) e qualquer menção a preencher a tag 34 com espaços. |

## 3. Comparativo com o QuickFIX (camada de sessão)

Base: leitura do código em 2026-11-06 e conhecimento da lib, sem consulta à documentação.

| Lacuna | Situação |
|---|---|
| Validar o `34` recebido (alto: `ResendRequest`; baixo sem `PossDup`: `Logout`) | TD-06 (initiator), TD-07 (acceptor). Concluídos. |
| `ResendRequest` recebido gerava frame inválido | TD-01. Concluído. |
| Reenvio real com `43` e `122` | TD-08. Concluído. |
| Logon sem credenciais, COD e `141` | TD-05. Concluído. |
| `Reject` (35=3) e validação de entrada | TD-06. Concluído. |
| Seqnum do initiator só em memória, sem reset diário | Persistência no TD-04. **Reset diário: TD-12.** |
| `SequenceReset` recebido não é tratado no initiator | **TD-11.** |
| Logon recebido aceito sem conferência (`CompID`, `HeartBtInt`, eco de `141`) | Parcial no TD-05 (passo 3). |
| Acceptor sem `Logout` na recusa, sem detecção de silêncio | TD-07. Concluído. |

Pontos fortes do router: hot path de PassThrough sem alocação e sem reserialização; TLS, reconexão com backoff e
`TestRequest` sem resposta no initiator.

## 4. Ideias e perguntas em aberto

- **Segregação nos brokers (task TD-10).** RabbitMQ segrega no servidor, com topic exchange (routing key hierárquica,
  por exemplo `fix.<conta440>.<symbol>`) ou headers exchange (`x-match`). O Kafka não decide na entrega: as opções são
  record headers com filtro no consumidor, tópico por critério (por participante, não por ClOrdID) e partition key,
  que só ordena. O modelo não fica simétrico entre os dois brokers.
- **Tag 440 (ClearingAccount).** Confirmar no `docs/FIX44Entrypoint.xml` como dimensão de segregação. A 9001 é convenção
  do NexusRouter, não tag da B3.
- **Dia de pregão (TD-12).** Quem define a virada: relógio UTC, calendário B3 ou o primeiro Logon com `141=Y`.
- **Tamanho médio do frame.** A estimativa de 300 a 500 bytes é suposição e deve ser medida com tráfego real.
  O pico do pregão não foi informado e é o que importa para o benchmark do append.
- **Sync de disco.** Queda do sistema operacional pode perder o que o mapeamento em memória não sincronizou; a
  `UncleanRestartSeqMargin` cobre o seqnum de saída, não o conteúdo.
- **B3 e salto de seqnum no `Logon`.** Se a B3 aceita salto de seqnum é pergunta em aberto do design §3.4.5.
