/**
 * Kafka and Rabbit screens. The static endpoint text (FR-UI-10) shows until
 * the adapter reports its effective endpoint.
 */

import type { JSX } from 'react';
import ConnectionCard from '../components/ConnectionCard';
import EventTable from '../components/EventTable';
import StatsRow from '../components/StatsRow';
import { useAdapter } from '../state/useAdapter';
import { useStore } from '../state/store';
import { lastEventLabel } from './format';
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

/** Kafka or Rabbit: connection card, counters and the event table. */
export function BrokerScreen({ transport }: BrokerScreenProps): JSX.Element {
  const headingId = `${transport}-screen-title`;
  const view = useAdapter(transport);
  const { buffers } = useStore();
  const counters = view.adapter?.counters;
  const endpoint = view.adapter?.endpoint ? view.adapter.endpoint : ENDPOINTS[transport];

  return (
    <section className={styles.screen} aria-labelledby={headingId}>
      <h2 id={headingId} className={styles.title}>
        {TITLES[transport]}
      </h2>
      <ConnectionCard
        endpoint={endpoint}
        state={view.state}
        stateLabel={view.stateLabel}
        lastError={view.lastError}
        busy={view.busy}
        onConnect={view.onConnect}
        onDisconnect={view.onDisconnect}
      />
      <StatsRow
        received={counters?.received}
        perSecond={counters ? Math.round(counters.ratePerSecond * 10) / 10 : undefined}
        lastEvent={lastEventLabel(counters?.lastReceivedAt)}
        parseFailures={counters?.parseFailures}
        sampled={counters?.sampled}
        dropped={counters?.dropped}
      />
      <EventTable events={buffers[transport]} />
    </section>
  );
}

export default BrokerScreen;
