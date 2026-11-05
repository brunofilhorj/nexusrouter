/**
 * Feed ao vivo das últimas mensagens amostradas (FR-UI-07).
 *
 * Exibe, em forma de tabela, as `MessageSample` mantidas no ring buffer do store
 * (Seção 4.3 do design), com a amostra mais recente no topo. Cada linha mostra
 * `MsgType`, `ClOrdID`, `OrderID`, `ExecType`, `OrdStatus`, `Side`, o instrumento
 * e o `BusinessSeqNum`. Não faz nenhuma chamada própria: apenas lê o estado
 * empurrado pela telemetria via store.
 */

import { useAppSelector } from '../state/useAppStore';
import type { ConnectivityStatus } from '../state/store';
import type { MessageSample } from '../api/types';

/** Número máximo de amostras exibidas simultaneamente no feed. */
const VISIBLE_SAMPLES = 50;

/** Placeholder exibido quando um campo opcional da amostra está ausente. */
const EMPTY = '—';

/** Formata um valor de campo opcional, tolerando null/undefined/vazio. */
function field(value: string | null | undefined): string {
  return value && value.length > 0 ? value : EMPTY;
}

/** Formata o BusinessSeqNum opcional para exibição. */
function businessSeqNum(value: number | null | undefined): string {
  return typeof value === 'number' ? value.toString() : EMPTY;
}

/** Formata um instante ISO 8601 (UTC) como horário local; tolera valores inválidos. */
function formatTime(iso: string): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) {
    return iso;
  }
  return date.toLocaleTimeString();
}

/**
 * Deriva uma chave estável para a linha. O `tsUtc` combinado ao `BusinessSeqNum`
 * e ao índice distingue amostras próximas sem depender de um id de servidor.
 */
function rowKey(sample: MessageSample, index: number): string {
  return `${sample.tsUtc}:${sample.businessSeqNum ?? ''}:${index}`;
}

/** Tabela do fluxo de mensagens amostradas. */
export function MessageFeed(): React.JSX.Element {
  const samples = useAppSelector<readonly MessageSample[]>((s) => s.samples);
  const connectivity = useAppSelector<ConnectivityStatus>(
    (s) => s.connectivity,
  );

  // Mais recente no topo, limitado às últimas N amostras visíveis.
  const visible = samples.slice(-VISIBLE_SAMPLES).reverse();

  return (
    <section className="message-feed" aria-labelledby="message-feed-heading">
      <h2 id="message-feed-heading">Feed de mensagens</h2>
      {visible.length === 0 ? (
        <p className="message-feed__empty">
          {connectivity === 'open'
            ? 'Aguardando mensagens…'
            : 'Telemetria offline.'}
        </p>
      ) : (
        <table className="message-feed__table">
          <thead>
            <tr>
              <th scope="col">Horário</th>
              <th scope="col">MsgType</th>
              <th scope="col">ClOrdID</th>
              <th scope="col">OrderID</th>
              <th scope="col">ExecType</th>
              <th scope="col">OrdStatus</th>
              <th scope="col">Side</th>
              <th scope="col">Instrumento</th>
              <th scope="col">BusinessSeqNum</th>
            </tr>
          </thead>
          <tbody>
            {visible.map((sample, index) => (
              <tr key={rowKey(sample, index)} className="message-feed__row">
                <td className="message-feed__time">{formatTime(sample.tsUtc)}</td>
                <td data-field="msgType">{field(sample.msgType)}</td>
                <td data-field="clOrdId">{field(sample.clOrdId)}</td>
                <td data-field="orderId">{field(sample.orderId)}</td>
                <td data-field="execType">{field(sample.execType)}</td>
                <td data-field="ordStatus">{field(sample.ordStatus)}</td>
                <td data-field="side">{field(sample.side)}</td>
                <td data-field="instrument">{field(sample.instrument)}</td>
                <td data-field="businessSeqNum">
                  {businessSeqNum(sample.businessSeqNum)}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}

export default MessageFeed;
