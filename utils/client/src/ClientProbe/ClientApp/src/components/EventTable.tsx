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

/** Table of hora, transporte, 35=, 11=, 55=, 39= and a raw excerpt. */
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
                <td className={styles.empty} colSpan={7}>
                  Nenhum evento.
                </td>
              </tr>
            ) : (
              visible.map((event) => (
                <tr key={event.id} className={styles.row}>
                  <td>{event.time}</td>
                  <td>{event.transport}</td>
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
