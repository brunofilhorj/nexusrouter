/**
 * Topbar connectivity for the page origin: the HTTP API and `/v1/ws`.
 * Both channels share the page origin. The API dot follows the `/v1/state`
 * poll (neutral until the first answer); the socket dot follows `/v1/ws`.
 * A text label always sits beside the dot.
 */

import type { JSX } from 'react';
import { useStore } from '../state/store';
import styles from './ConnectivityBadge.module.css';

/** Badge for API and `/v1/ws` on the same origin. */
export function ConnectivityBadge(): JSX.Element {
  const { telemetryOpen, apiUp } = useStore();
  const apiStatus = apiUp === null ? 'unknown' : apiUp ? 'up' : 'down';
  return (
    <section className={styles.badge} aria-labelledby="connectivity-badge-heading">
      <h2 id="connectivity-badge-heading" className={styles.heading}>
        Conectividade
      </h2>
      <ul className={styles.list}>
        <li className={styles.item} data-channel="api" data-status={apiStatus}>
          <span className={styles.dot} aria-hidden="true" />
          <span className={styles.label}>
            API {apiStatus === 'up' ? 'ok' : apiStatus === 'down' ? 'fora do ar' : '…'}
          </span>
        </li>
        <li className={styles.item} data-channel="ws" data-status={telemetryOpen ? 'up' : 'down'}>
          <span className={styles.dot} aria-hidden="true" />
          <span className={styles.label}>/v1/ws {telemetryOpen ? 'aberto' : 'fechado'}</span>
        </li>
      </ul>
    </section>
  );
}

export default ConnectivityBadge;
