/**
 * Event ring for one dispatcher. Capacity is 500 and the newest row is first.
 * FIX columns use a monospace face. An empty ring shows a single placeholder.
 */

import { useId, type JSX } from 'react';
import styles from './EventTable.module.css';

/** Maximum rows kept on screen. */
const RING_CAPACITY = 500;

/** One row of the event table. */
export interface ProbeEventRow {
  /** Stable key for the row. */
  id: string;
  time: string;
  transport: string;
  msgType: string;
  /** <c>in</c> for a received frame, <c>out</c> for a frame the probe sent. */
  direction: 'in' | 'out';
  clOrdId: string;
  symbol: string;
  ordStatus: string;
  raw: string;
}

/** Props for the event table. */
export interface EventTableProps {
  /** Newest-first events. Only the first 500 are shown. */
  events?: readonly ProbeEventRow[];
}

/** Session messages other than Reject. Heartbeat and TestRequest are included if they ever arrive. */
const SESSION_MSG_TYPES = new Set(['0', '1', '2', '4', '5', 'A']);

/** Row class: reject in red, other session messages in amber, application unchanged. */
function rowClass(msgType: string): string {
  if (msgType === '3' || msgType === 'j') {
    return `${styles.row} ${styles.reject}`;
  }
  if (SESSION_MSG_TYPES.has(msgType)) {
    return `${styles.row} ${styles.session}`;
  }
  return styles.row;
}

/** Table of hora, transporte, sentido, 35=, 11=, 55=, 39= and a raw excerpt. */
export function EventTable({ events = [] }: EventTableProps): JSX.Element {
  const headingId = useId();
  const visible = events.slice(0, RING_CAPACITY);

  return (
    <section className={styles.feed} aria-labelledby={headingId}>
      <h2 id={headingId} className={styles.title}>
        Eventos
      </h2>
      <div className={styles.wrap}>
        <table className={styles.table}>
          <thead>
            <tr>
              <th scope="col">Hora</th>
              <th scope="col">Transporte</th>
              <th scope="col">Sentido</th>
              <th scope="col">35=</th>
              <th scope="col">11=</th>
              <th scope="col">55=</th>
              <th scope="col">39=</th>
              <th scope="col">Raw</th>
            </tr>
          </thead>
          <tbody>
            {visible.length === 0 ? (
              <tr>
                <td className={styles.empty} colSpan={8}>
                  Nenhum evento.
                </td>
              </tr>
            ) : (
              visible.map((event) => (
                <tr key={event.id} className={rowClass(event.msgType)}>
                  <td>{event.time}</td>
                  <td>{event.transport}</td>
                  <td>{event.direction === 'out' ? 'enviada' : 'recebida'}</td>
                  <td className={styles.mono}>{event.msgType}</td>
                  <td className={styles.mono}>{event.clOrdId}</td>
                  <td className={styles.mono}>{event.symbol}</td>
                  <td className={styles.mono}>{event.ordStatus}</td>
                  <td className={styles.mono}>{event.raw}</td>
                </tr>
              ))
            )}
          </tbody>
        </table>
      </div>
    </section>
  );
}

export default EventTable;
