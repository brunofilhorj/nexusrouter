/**
 * Panel store: one visible screen and one event buffer per transport.
 * Switching screens never clears the other buffers (FR-UI-08).
 */
import { useSyncExternalStore } from 'react';
import type { AdapterState, ProbeTransport } from '../api/client';
import type { ProbeEventRow } from '../components/EventTable';
import type { ScreenId } from '../screens/screens';

/** Capacity of each transport buffer. */
export const BUFFER_CAPACITY = 500;

/** Transport ids; they match the screen ids. */
export type TransportId = ScreenId;

/** Buffers keyed by transport id. Newest row first. */
export type EventBuffers = Record<TransportId, readonly ProbeEventRow[]>;

/** Immutable store snapshot. */
export interface StoreState {
  /** The single visible screen. */
  screen: ScreenId;
  buffers: EventBuffers;
  /** True while the `/v1/ws` socket is open. */
  telemetryOpen: boolean;
  /** Last `GET /v1/state` snapshot per transport. Empty until the first answer. */
  adapters: Partial<Record<ProbeTransport, AdapterState>>;
  /** True when the last `/v1/state` poll succeeded; null before the first poll. */
  apiUp: boolean | null;
}

let state: StoreState = {
  screen: 'kafka',
  buffers: { kafka: [], rabbit: [], fix: [], ws: [] },
  telemetryOpen: false,
  adapters: {},
  apiUp: null,
};

const listeners = new Set<() => void>();

/** Replaces the snapshot and notifies subscribers. */
function update(next: StoreState): void {
  state = next;
  listeners.forEach((listener) => {
    listener();
  });
}

/** Subscribes to changes. Returns the unsubscribe function. */
export function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

/** Current snapshot. */
export function getState(): StoreState {
  return state;
}

/** Makes one screen visible. Other buffers are untouched. */
export function setScreen(screen: ScreenId): void {
  if (state.screen !== screen) {
    update({ ...state, screen });
  }
}

/** Adds a row to the transport buffer, newest first, capped at 500. */
export function pushEvent(transport: TransportId, row: ProbeEventRow): void {
  const next = [row, ...state.buffers[transport]].slice(0, BUFFER_CAPACITY);
  update({ ...state, buffers: { ...state.buffers, [transport]: next } });
}

/** Empties one transport buffer. */
export function clearBuffer(transport: TransportId): void {
  update({ ...state, buffers: { ...state.buffers, [transport]: [] } });
}

/** Records whether the telemetry socket is open. */
export function setTelemetryOpen(telemetryOpen: boolean): void {
  if (state.telemetryOpen !== telemetryOpen) {
    update({ ...state, telemetryOpen });
  }
}

/** Stores a successful `/v1/state` answer and marks the API as up. */
export function setAdapters(list: readonly AdapterState[]): void {
  const adapters: Partial<Record<ProbeTransport, AdapterState>> = {};
  for (const adapter of list) {
    adapters[adapter.transport] = adapter;
  }
  update({ ...state, adapters, apiUp: true });
}

/** Marks the API as unreachable after a failed poll. Keeps the last snapshot. */
export function setApiDown(): void {
  if (state.apiUp !== false) {
    update({ ...state, apiUp: false });
  }
}

/** React binding: the current snapshot. */
export function useStore(): StoreState {
  return useSyncExternalStore(subscribe, getState);
}
