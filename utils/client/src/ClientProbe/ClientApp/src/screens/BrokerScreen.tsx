/**
 * Static Kafka and Rabbit screens. Endpoint text matches FR-UI-10.
 */

import type { JSX } from 'react';
import ConnectionCard from '../components/ConnectionCard';
import EventTable from '../components/EventTable';
import StatsRow from '../components/StatsRow';
import styles from './Screen.module.css';

/** Broker transports that share this screen. */
export type BrokerTransport = 'kafka' | 'rabbit';

/** Endpoint lines aligned with the Host appsettings defaults. */
const ENDPOINTS: Record<BrokerTransport, string> = {
  kafka: '127.0.0.1:19092 · tópico fix.ordexec.raw · grupo probe',
  rabbit: '127.0.0.1:5672 · exchange fix.orders · key fix.raw',
};

const TITLES: Record<BrokerTransport, string> = {
  kafka: 'Kafka',
  rabbit: 'Rabbit',
};

/** Props for the broker screen. */
export interface BrokerScreenProps {
  transport: BrokerTransport;
}

/** Kafka or Rabbit: connection card, counters, empty event table. */
export function BrokerScreen({ transport }: BrokerScreenProps): JSX.Element {
  const headingId = `${transport}-screen-title`;

  return (
    <section className={styles.screen} aria-labelledby={headingId}>
      <h2 id={headingId} className={styles.title}>
        {TITLES[transport]}
      </h2>
      <ConnectionCard endpoint={ENDPOINTS[transport]} />
      <StatsRow />
      <EventTable />
    </section>
  );
}

export default BrokerScreen;
