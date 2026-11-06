/**
 * Connection card for one dispatcher screen: endpoint, labelled state,
 * last error, and connect / disconnect. A button is enabled only when its
 * callback is given and the state allows it; a card without callbacks (the
 * WebSocket screen) keeps both disabled.
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
  /** True while a connect or disconnect request is in flight. */
  busy?: boolean;
  /** Connect action. Without it the Connect button stays disabled. */
  onConnect?: () => void;
  /** Disconnect action. Without it the Disconnect button stays disabled. */
  onDisconnect?: () => void;
}

/** Connection summary with connect and disconnect actions. */
export function ConnectionCard({
  endpoint,
  state = 'disconnected',
  stateLabel = 'Desconectado',
  lastError = null,
  note,
  busy = false,
  onConnect,
  onDisconnect,
}: ConnectionCardProps): JSX.Element {
  const headingId = useId();
  const canConnect = onConnect !== undefined && !busy && (state === 'disconnected' || state === 'faulted');
  const canDisconnect = onDisconnect !== undefined && !busy && (state === 'connecting' || state === 'live' || state === 'faulted');

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
        <button type="button" disabled={!canConnect} onClick={onConnect}>
          Conectar
        </button>
        <button type="button" disabled={!canDisconnect} onClick={onDisconnect}>
          Desconectar
        </button>
      </div>
    </section>
  );
}

export default ConnectionCard;
