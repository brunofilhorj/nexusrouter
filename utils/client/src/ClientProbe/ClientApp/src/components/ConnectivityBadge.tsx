/**
 * Topbar connectivity for the page origin: the HTTP API and `/v1/ws`.
 * Both channels share the page origin. Live status arrives in T-06; until
 * then both stay unknown (neutral), with a text label beside the dot.
 */

import type { JSX } from 'react';
import styles from './ConnectivityBadge.module.css';

/** Badge for API and `/v1/ws` on the same origin. */
export function ConnectivityBadge(): JSX.Element {
  return (
    <section className={styles.badge} aria-labelledby="connectivity-badge-heading">
      <h2 id="connectivity-badge-heading" className={styles.heading}>
        Conectividade
      </h2>
      <ul className={styles.list}>
        <li className={styles.item} data-channel="api" data-status="unknown">
          <span className={styles.dot} aria-hidden="true" />
          <span className={styles.label}>API</span>
        </li>
        <li className={styles.item} data-channel="ws" data-status="unknown">
          <span className={styles.dot} aria-hidden="true" />
          <span className={styles.label}>/v1/ws</span>
        </li>
      </ul>
    </section>
  );
}

export default ConnectivityBadge;
