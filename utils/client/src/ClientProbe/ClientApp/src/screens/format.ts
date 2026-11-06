/** Formatting helpers shared by the dispatcher screens. */

/** Local time of the last received frame, or an em dash when there is none. */
export function lastEventLabel(value: string | null | undefined): string {
  if (!value) {
    return '—';
  }
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? '—' : date.toLocaleTimeString('pt-BR');
}
