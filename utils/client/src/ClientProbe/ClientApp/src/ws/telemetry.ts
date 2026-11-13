/**
 * Telemetry socket to `/v1/ws` on the page origin, with exponential backoff.
 * Without events the badge only reflects open or closed (store.telemetryOpen).
 */
import type { ProbeEventRow } from '../components/EventTable';
import { pushEvent, setTelemetryOpen, type TransportId } from '../state/store';

const INITIAL_DELAY_MS = 500;
const MAX_DELAY_MS = 10_000;

/** Wire shape of a telemetry event (design §3.6). */
interface WireEvent {
  transport?: unknown;
  receivedAt?: unknown;
  msgType?: unknown;
  fields?: unknown;
  rawPreview?: unknown;
  direction?: unknown;
}

let rowSeq = 0;

function isTransport(value: unknown): value is TransportId {
  return value === 'kafka' || value === 'rabbit' || value === 'fix' || value === 'ws';
}

function field(fields: unknown, tag: string): string {
  if (typeof fields === 'object' && fields !== null) {
    const value = (fields as Record<string, unknown>)[tag];
    if (typeof value === 'string') {
      return value;
    }
  }
  return '';
}

/** Translates a wire event to a table row, or null for other message kinds. */
function toRow(data: unknown): { transport: TransportId; row: ProbeEventRow } | null {
  if (typeof data !== 'object' || data === null) {
    return null;
  }
  const event = data as WireEvent;
  if (!isTransport(event.transport) || typeof event.rawPreview !== 'string') {
    return null;
  }
  rowSeq += 1;
  return {
    transport: event.transport,
    row: {
      id: `${event.transport}-${rowSeq}`,
      time: typeof event.receivedAt === 'string' ? event.receivedAt : '',
      transport: event.transport,
      msgType: typeof event.msgType === 'string' ? event.msgType : '',
      direction: event.direction === 'out' ? 'out' : 'in',
      clOrdId: field(event.fields, '11'),
      symbol: field(event.fields, '55'),
      ordStatus: field(event.fields, '39'),
      raw: event.rawPreview,
    },
  };
}

/** Absolute ws(s) URL for `/v1/ws`, derived from the page origin. */
function telemetryUrl(): string {
  const scheme = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
  return `${scheme}//${window.location.host}/v1/ws`;
}

/**
 * Opens `/v1/ws` and reconnects with backoff until the returned function runs.
 * Counter snapshots are ignored here; only events enter the buffers.
 */
export function startTelemetry(): () => void {
  let socket: WebSocket | null = null;
  let timer: number | undefined;
  let delay = INITIAL_DELAY_MS;
  let stopped = false;

  const open = (): void => {
    const ws = new WebSocket(telemetryUrl());
    socket = ws;
    ws.onopen = () => {
      delay = INITIAL_DELAY_MS;
      setTelemetryOpen(true);
    };
    ws.onmessage = (message: MessageEvent<unknown>) => {
      if (typeof message.data !== 'string') {
        return;
      }
      try {
        const parsed = toRow(JSON.parse(message.data));
        if (parsed) {
          pushEvent(parsed.transport, parsed.row);
        }
      } catch {
        // Not JSON: ignore the frame.
      }
    };
    ws.onclose = () => {
      setTelemetryOpen(false);
      socket = null;
      if (!stopped) {
        timer = window.setTimeout(open, delay);
        delay = Math.min(delay * 2, MAX_DELAY_MS);
      }
    };
  };

  open();

  return () => {
    stopped = true;
    window.clearTimeout(timer);
    socket?.close();
  };
}
