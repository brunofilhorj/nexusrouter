/**
 * Panel store: one visible screen and one event buffer per transport.
 * Switching screens never clears the other buffers (FR-UI-08).
 */
import { useSyncExternalStore } from 'react';
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
}

let state: StoreState = {
  screen: 'kafka',
  buffers: { kafka: [], rabbit: [], fix: [], ws: [] },
  telemetryOpen: false,
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

/** React binding: the current snapshot. */
export function useStore(): StoreState {
  return useSyncExternalStore(subscribe, getState);
}
