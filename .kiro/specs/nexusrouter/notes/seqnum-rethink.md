# Notas — Repensar o tratamento de MsgSeqNum (sessões FIX, PassThrough e persistência)

> **Status:** rascunho de rediscussão. Não é decisão tomada.
> Não altera `requirements.md`, `design.md` nem `tasks.md`. Serve de insumo para decidir
> o escopo antes de abrir tasks. Origem: teste manual do client-probe contra o acceptor
> `fix-acceptor-01` (dispatcher FIX outbound), em 2026-11-06.

## Contexto

Com o client-probe conectado na sessão `PROBE` do acceptor `fix-acceptor-01`, o router
passou a descartar todas as mensagens com este aviso:

```
FixOutboundDispatcher: Sessão PROBE do dispatcher fix-acceptor-01 não recebeu a mensagem.
System.InvalidOperationException: O MsgSeqNum outbound não coube no campo 34.
   at FixAcceptedSession.Prepare(...)      FixAcceptedSession.cs:312
   at FixAcceptedSession.TryEnqueue(...)   FixAcceptedSession.cs:239
   at FixOutboundDispatcher.SendAsync(...) FixOutboundDispatcher.cs:146
```

A investigação mostrou dois assuntos distintos que convém separar:

1. **Formato:** o `PassThroughRewriter` não consegue gravar um MsgSeqNum com mais dígitos
   do que o campo 34 do frame de origem. Esse é o defeito que derrubou o envio.
2. **Sincronia:** quem persiste seqnum, de que lado, e o que fazer quando sender e target
   divergem. Hoje só existe persistência de saída no acceptor.

Os dois se misturaram na conversa, mas o primeiro acontece mesmo com seqnums perfeitamente
sincronizados.

---

## 1. Causa raiz do erro: a reescrita só cabe na largura do campo original

**Observação.** O arquivo `data/seqnum/fix-acceptor-01_PROBE.seq` continha `37582`
(cinco dígitos). Os frames do simulador chegam com a tag 34 de quatro dígitos
(ex.: `34=9982`). O número novo não cabe, `PassThroughRewriter.Rewrite` devolve `false` e
`FixAcceptedSession.Prepare` lança a exceção. O contador só sobe e é persistido, então
reiniciar não resolve: toda mensagem nova falha.

**Não depende de seqnum dessincronizado.** O mesmo acontece no primeiro envio de uma
sessão nova se o frame de origem tiver `34=1` e a sessão estiver em `12`. O teste
`Rewrite_WhenSequenceNeedsMoreDigits_ReturnsFalseWithoutChangingFrame` documenta o
comportamento como esperado. O desenho assumiu que o número de origem e o da sessão de
saída teriam sempre a mesma ordem de grandeza.

**Padding com espaços não é FIX válido.** Quando o número novo é mais curto, o rewriter
preenche à esquerda (`34=  12`). Nenhum dos PDFs da B3 menciona essa prática. Um
counterpart estrito pode rejeitar o campo.

**O que o design e o steering dizem.**
- `design.md` §3 (modo PassThrough) e a tabela de custos: o rewriter reescreve só as tags
  34 e 10, in-place, zero-allocation.
- `b3-guidelines.md` §15 passo 1: "mesmo número de bytes ou preencher com espaços".
- `b3-guidelines.md` §15 passo 5: se o novo SeqNum tiver mais dígitos, `BodyLength` (tag 9)
  também deve ser atualizado, e o rewriter deve "reservar espaço de preenchimento". O código
  não faz nenhuma das duas coisas.

**Direções a explorar.**
- **Reescrever com mudança de tamanho.** Novo método que copia o frame de origem para o
  destino, grava o 34 com os dígitos exatos, recalcula `9` e `10`. Elimina o padding e o
  `false` por largura. Custo: uma cópia a mais por mensagem no caminho FIX outbound.
- **Manter o in-place só quando a largura bate** e usar a cópia como caminho de exceção.
  Preserva o hot path atual quando o número cabe.
- **Rever o contrato do `RouterEngine`.** `RewriteInboundSequence` regrava o 34 inbound
  sobre ele mesmo só para recalcular o checksum; o dispatcher FIX aplica o seqnum da
  sessão depois. Avaliar se essa primeira reescrita ainda faz sentido.
- **Folga no buffer alugado.** `FixAcceptedSession.TryEnqueue` aluga exatamente
  `RawBuffer.Length` no PassThrough. Com mudança de tamanho precisa de alguns bytes a mais
  (até 20 dígitos).

**Impacto se mudar:** `PassThroughRewriter` e seus testes, `FixAcceptedSession.Prepare`
e `TryEnqueue`, `RouterEngine.RewriteInboundSequence`, benchmarks de PassThrough, e o
texto de `design.md` §3 e de `b3-guidelines.md` §15.

---

## 2. O que a B3 documenta sobre seqnum (PDFs em `docs/`)

> Levantamento por busca de texto nos três PDFs convertidos. Não foi uma leitura integral.

| Assunto | O que consta | Fonte |
|---|---|---|
| Reset diário | Ao fim da sessão o gateway derruba a conexão e o seqnum volta a 1. Todo início de sessão espera 1. Não há Logout no encerramento. | Messaging Guidelines §3.4 |
| Drop Copy | Reset do seqnum de terça a quinta às 23:00 BRT, com todos os clientes desconectados. De domingo para segunda o número **não** é resetado. | Messaging Guidelines (Drop Copy, End of Day) |
| `ResetSeqNumFlag` (141) | Opcional no Logon. Indica que os dois lados da sessão devem zerar o seqnum. | Message Specs §2.7 |
| `NextExpectedMsgSeqNum` (789) | **Removido do Logon na versão 1.5.1** da spec. | Message Specs, histórico de revisões |
| `ResendRequest` (35=2) | Limite de 10.000 mensagens por pedido. Acima disso, só o limite é reenviado e o `SequenceReset` final traz `PossMissingApplMsg` (35033)=`Y`. Um pedido por vez. | Messaging Guidelines §14.6 e Drop Copy |
| Reconexão e COD | Reconectando sem reset, o cliente recebe os ExecutionReports dos cancelamentos por COD. | Messaging Guidelines §3.6 |
| Roteiro de certificação (Drop Copy) | Cenário A4 pede Resend Request depois de desconectar da bolsa e conferir o primeiro e o último SeqNum recebidos. | Roteiro de Certificação |

**O que não encontrei nos PDFs.**
- A regra do steering `b3-guidelines.md` §13: `Logout` com "MsgSeqNum too low" leva a
  resetar e reconectar com `141=Y`. Essa linha é do steering, não aparece nos PDFs.
- Qualquer orientação para seqnum de sender e target divergentes além do que está acima.
- Qualquer menção a preencher a tag 34 com espaços.

**Divergência a corrigir no steering.** `b3-guidelines.md` §1 lista o 789 como enviado no
Logon ao continuar sessão. O PDF removeu o campo na v1.5.1. Pela regra do próprio
steering, o PDF prevalece.

---

## 3. O que o router persiste hoje

| Papel | Seqnum de saída | Seqnum de entrada |
|---|---|---|
| **Acceptor** (`FixOutboundDispatcher`, serve o PROBE) | Persiste em `<SequenceStoreDirectory>/<DispatcherId>_<SenderCompID>.seq`. Flush a cada 5 s e no stop. Zera quando o Logon do cliente traz `141=Y`. | **Não valida** o `34` das mensagens do cliente e não guarda o esperado. |
| **Initiator** (`FixSession` do ingress, conecta na B3 ou no simulador) | `_nextOutboundSequence` só em memória. Nada é gravado. Nunca é zerado, nem na reconexão dentro do mesmo processo. Num restart volta a 1. | Não identifiquei controle de seqnum recebido. |

Outros detalhes verificados:
- O Logon do initiator (`FixMessageBuilder.BuildLogon`) só envia `98` e `108`. Não envia
  `141`.
- O acceptor responde a `ResendRequest` apenas com `SequenceReset` de gap-fill até o próximo
  número (`TryEnqueueGapFill`). Não reenvia mensagens. O `FixSessionStateMachine.HandleResendRequest`
  do ingress é um stub que devolve `35=4` sem `GapFillFlag`, intervalo nem checksum.
- Um crash entre dois flushes de 5 s pode repetir seqnums já enviados (documentado em
  `SequenceNumberStore`).
- O store não conhece data de pregão. Nada zera o contador no dia seguinte, ao contrário do
  que a B3 faz.
- Não li o `FixSession` do ingress por inteiro. A conclusão sobre seqnum de entrada vem de
  busca por termos.

**Correção sobre o client-probe.** O probe **não** persiste seqnum. O
`FixInitiatorAdapter` declara que o `MsgSeqNum` vale só para a sessão e nada é gravado.
Cada conexão começa em 1 e o Logon sai sem `141=Y`. Quem guarda o número da sessão `PROBE`
é o acceptor do router.

Consequência: o probe reconecta sempre em 1 enquanto o acceptor continua de onde parou
(37582). Como o acceptor não valida o seqnum de entrada, hoje isso passa despercebido. Um
counterpart que validasse (a B3, por exemplo) reagiria ao número baixo.

---

## 4. Seqnum de sender e target divergentes

**Observação.** A pergunta original foi como tratar os dois lados não baterem. Hoje o router
não detecta divergência em nenhum dos papéis: o acceptor não olha o `34` recebido e o
initiator não persiste nem confere nada.

**Cenários a cobrir.**

| Cenário | Situação atual | Direção |
|---|---|---|
| Cliente reconecta com `34` menor que o esperado (como o probe, que volta a 1) | Aceito sem verificação | Rejeitar com `Logout` explicando, ou aceitar `141=Y` explícito |
| Cliente reconecta com `34` maior que o esperado | Aceito sem verificação | Enviar `ResendRequest` para o intervalo faltante |
| Router initiator reinicia | Volta a 1 sem avisar | Persistir, ou enviar `141=Y` conscientemente |
| Pregão novo | Nenhum reset automático | Marcar o arquivo com a data de pregão e zerar no primeiro Logon do dia |
| Mensagem de reenvio (`ResendRequest` recebido) | Só gap-fill | Decidir se haverá reenvio real, o que depende do repensar de replay (`replay-rethink.md`) |

**Perguntas em aberto.**
- O acceptor deve ser tolerante (o probe é só uma prova) ou estrito como a B3?
- O probe deveria passar a persistir o seqnum dele, ou enviar `141=Y` em todo Logon?
- Quem decide o dia de pregão: relógio UTC, calendário B3, ou o primeiro Logon `141=Y`?
- A retransmissão real de FIX acompanha o replay de `client_replay_messages` ou fica fora do escopo?

---

## 5. Escopo proposto para decisão

| Item | O que muda | Tamanho | Observação |
|---|---|---|---|
| 1. Rewriter com mudança de tamanho | `PassThroughRewriter`, `FixAcceptedSession`, testes, benchmark | Pequeno a médio | Resolve o erro que originou estas notas |
| 2. Acceptor valida e persiste o seqnum de entrada | `FixAcceptedSession`, `SequenceNumberStore` (ou novo store), Logon | Médio | Precisa da política do item 4 |
| 3. Initiator persiste saída e entrada, com data de pregão e `141=Y` no primeiro Logon do dia | `FixSession`, `FixMessageBuilder.BuildLogon`, novo store | Médio a grande | Depende da definição de dia de pregão |
| 4. Steering | `b3-guidelines.md` §1 (789), §13 (origem da regra "too low"), §15 (padding e `BodyLength`) | Pequeno | Pode ir junto com o item 1 |

Nenhuma task foi aberta. Falta decidir em qual fase do `tasks.md` entram, quais IDs usam e
quais itens ficam no backlog.

---

## 6. Contorno disponível até a decisão

Com a sessão parada, apagar `src/NexusRouter.Host/data/seqnum/fix-acceptor-01_PROBE.seq`
zera o contador. A pasta `data/` está no `.gitignore` desde o commit da T-13a. O erro volta
quando o seqnum ultrapassar de novo a largura do campo 34 dos frames de origem.

---

## Ligações com outras notas e documentos

- `notes/replay-rethink.md`: o `replay_seq` de cada cliente é o MsgSeqNum de saída da
  sessão. Qualquer mudança em como o seqnum é atribuído ou persistido afeta a chave do
  replay.
- `.kiro/steering/b3-guidelines.md` §1, §4, §12, §13 e §15.
- Código: `src/NexusRouter.Router/PassThroughRewriter.cs`, `src/NexusRouter.Router/RouterEngine.cs`,
  `src/NexusRouter.Egress/Fix/{FixAcceptedSession,FixOutboundDispatcher,SequenceNumberStore}.cs`,
  `src/NexusRouter.Ingress/Sessions/{FixSession,FixMessageBuilder,FixSessionStateMachine}.cs`.
- Probe: `utils/client/src/ClientProbe/Fix/FixInitiatorAdapter.cs`.
