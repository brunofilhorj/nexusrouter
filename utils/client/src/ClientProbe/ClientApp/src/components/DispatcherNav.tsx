/**
 * Side menu for the four dispatcher screens. Navigation is a hash link,
 * without react-router, so the screen survives a refresh.
 */

import type { JSX } from 'react';
import type { ScreenId } from '../screens/screens';
import styles from './DispatcherNav.module.css';

/** Menu entries in display order. */
const ITEMS: readonly { id: ScreenId; href: string; label: string }[] = [
  { id: 'kafka', href: '#/kafka', label: 'Kafka' },
  { id: 'rabbit', href: '#/rabbit', label: 'Rabbit' },
  { id: 'fix', href: '#/fix', label: 'FIX' },
  { id: 'ws', href: '#/ws', label: 'WebSocket' },
];

/** Props for the dispatcher menu. */
export interface DispatcherNavProps {
  /** Screen currently shown. An empty hash still counts as Kafka. */
  current: ScreenId;
}

/** Lateral menu: Kafka, Rabbit, FIX, WebSocket. */
export function DispatcherNav({ current }: DispatcherNavProps): JSX.Element {
  return (
    <nav className={styles.nav} aria-label="Dispatchers">
      {ITEMS.map((item) => (
        <a
          key={item.id}
          className={styles.link}
          href={item.href}
          aria-current={item.id === current ? 'page' : undefined}
        >
          {item.label}
        </a>
      ))}
    </nav>
  );
}

export default DispatcherNav;
