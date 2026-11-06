/**
 * Four counters for a dispatcher screen. Sampled and dropped are shown only
 * when they are greater than zero.
 */

import type { JSX } from 'react';
import styles from './StatsRow.module.css';

/** Props for the counter row. Omitted counters render as zero or an em dash. */
export interface StatsRowProps {
  received?: number;
  perSecond?: number;
  lastEvent?: string;
  parseFailures?: number;
  sampled?: number;
  dropped?: number;
}

/** Counter definition rendered as one cell. */
interface StatCell {
  label: string;
  value: string;
}

/** Four primary counters, plus sampled and dropped when they are non-zero. */
export function StatsRow({
  received = 0,
  perSecond = 0,
  lastEvent = '—',
  parseFailures = 0,
  sampled = 0,
  dropped = 0,
}: StatsRowProps): JSX.Element {
  const cells: StatCell[] = [
    { label: 'Recebidas', value: String(received) },
    { label: 'msg/s', value: String(perSecond) },
    { label: 'Último evento', value: lastEvent },
    { label: 'Falhas de parse', value: String(parseFailures) },
  ];

  if (sampled > 0) {
    cells.push({ label: 'Amostradas', value: String(sampled) });
  }
  if (dropped > 0) {
    cells.push({ label: 'Descartadas', value: String(dropped) });
  }

  return (
    <ul className={styles.row} aria-label="Contadores">
      {cells.map((cell) => (
        <li key={cell.label} className={styles.item}>
          <p className={styles.label}>{cell.label}</p>
          <p className={styles.value}>{cell.value}</p>
        </li>
      ))}
    </ul>
  );
}

export default StatsRow;
