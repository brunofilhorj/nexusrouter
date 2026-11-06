/**
 * Connection card for one dispatcher screen: endpoint, labelled state,
 * last error, and connect / disconnect. Buttons stay disabled until a live
 * adapter is wired.
 */

import { useId, type JSX } from 'react';
import styles from './ConnectionCard.module.css';

/** Connection states. Color is paired with `stateLabel`. */
export type ConnectionState = 'disconnected' | 'connecting' | 'live' | 'faulted';

/** Props for the static connection card. */
export interface ConnectionCardProps {
  /** Endpoint line shown to the operator. */
  endpoint: string;
  /** Machine state used for the color. */
  state?: ConnectionState;
  /** Text label that always accompanies the state color. */
  stateLabel?: string;
  /** Last error, or null when there is none. */
  lastError?: string | null;
  /** Optional note under the fields (WebSocket egress). */
  note?: string;
}

/** Connection summary with connect and disconnect actions. */
export function ConnectionCard({
  endpoint,
  state = 'disconnected',
  stateLabel = 'Desconectado',
  lastError = null,
  note,
}: ConnectionCardProps): JSX.Element {
  const headingId = useId();

  return (
    <section className={styles.card} aria-labelledby={headingId}>
      <h2 id={headingId} className={styles.title}>
        Conexão
      </h2>
      <dl className={styles.grid}>
        <div className={styles.row}>
          <dt>Endpoint</dt>
          <dd>{endpoint}</dd>
        </div>
        <div className={styles.row}>
          <dt>Estado</dt>
          <dd className={styles.state} data-state={state}>
            {stateLabel}
          </dd>
        </div>
        <div className={styles.row}>
          <dt>Último erro</dt>
          <dd>{lastError && lastError.length > 0 ? lastError : '—'}</dd>
        </div>
      </dl>
      {note ? <p className={styles.note}>{note}</p> : null}
      <div className={styles.actions}>
        <button type="button" disabled>
          Conectar
        </button>
        <button type="button" disabled>
          Desconectar
        </button>
      </div>
    </section>
  );
}

export default ConnectionCard;
