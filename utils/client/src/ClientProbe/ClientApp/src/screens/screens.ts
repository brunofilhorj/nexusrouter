/** Dispatcher screen selected by the location hash. */
export type ScreenId = 'kafka' | 'rabbit' | 'fix' | 'ws';

/**
 * Maps the location hash to a screen. An empty or unknown hash opens Kafka.
 * Known hashes are `#/kafka`, `#/rabbit`, `#/fix` and `#/ws`.
 */
export function screenFromHash(hash: string): ScreenId {
  switch (hash) {
    case '#/rabbit':
      return 'rabbit';
    case '#/fix':
      return 'fix';
    case '#/ws':
      return 'ws';
    default:
      return 'kafka';
  }
}
