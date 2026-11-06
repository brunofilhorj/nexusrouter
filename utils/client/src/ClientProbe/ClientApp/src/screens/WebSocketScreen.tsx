/**
 * Static WebSocket screen. The egress acceptor is not part of this probe;
 * the card says so until the operator points at a host (T-05) and the
 * NexusRouter acceptor exists (nexusrouter T-49).
 */

import type { JSX } from 'react';
import ConnectionCard from '../components/ConnectionCard';
import EventTable from '../components/EventTable';
import StatsRow from '../components/StatsRow';
import styles from './Screen.module.css';

/** Shown while no egress host and port are configured. */
const EGRESS_NOTE = 'O acceptor WebSocket não está no egress.';

/** WebSocket screen: no default endpoint, egress note, empty event table. */
export function WebSocketScreen(): JSX.Element {
  return (
    <section className={styles.screen} aria-labelledby="ws-screen-title">
      <h2 id="ws-screen-title" className={styles.title}>
        WebSocket
      </h2>
      <ConnectionCard endpoint="—" note={EGRESS_NOTE} />
      <StatsRow />
      <EventTable />
    </section>
  );
}

export default WebSocketScreen;
