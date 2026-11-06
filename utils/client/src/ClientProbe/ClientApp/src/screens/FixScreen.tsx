/**
 * Static FIX screen. Endpoint text matches FR-UI-10.
 */

import type { JSX } from 'react';
import ConnectionCard from '../components/ConnectionCard';
import EventTable from '../components/EventTable';
import StatsRow from '../components/StatsRow';
import styles from './Screen.module.css';

/** FIX initiator defaults from the Host appsettings. */
const FIX_ENDPOINT = '127.0.0.1:6001 · Target NEXUSROUTER';

/** FIX screen: connection card, counters, empty event table. */
export function FixScreen(): JSX.Element {
  return (
    <section className={styles.screen} aria-labelledby="fix-screen-title">
      <h2 id="fix-screen-title" className={styles.title}>
        FIX
      </h2>
      <ConnectionCard endpoint={FIX_ENDPOINT} />
      <StatsRow />
      <EventTable />
    </section>
  );
}

export default FixScreen;
