/**
 * Polls `GET /v1/state` once per second and feeds the store. A failed poll
 * marks the API as down and keeps the last snapshot on screen.
 */
import { getState } from '../api/client';
import { setAdapters, setApiDown } from './store';

const POLL_INTERVAL_MS = 1000;

/** Fetches the state once and stores it. Never throws. */
export async function refreshState(): Promise<void> {
  try {
    const snapshot = await getState();
    setAdapters(snapshot.adapters);
  } catch {
    setApiDown();
  }
}

/** Starts the poll. Returns the function that stops it. */
export function startStatePolling(): () => void {
  void refreshState();
  const timer = window.setInterval(() => {
    void refreshState();
  }, POLL_INTERVAL_MS);
  return () => {
    window.clearInterval(timer);
  };
}
