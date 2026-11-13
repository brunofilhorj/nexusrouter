/**
 * Builds the `profile` body for `POST /v1/connect` from the modal settings.
 * Plain fields come from `loadConfig()`; the Rabbit password comes from the
 * in-memory secrets and is sent only in the request body (NFR-SEC-01).
 * Empty fields are omitted so the server falls back to its own defaults.
 */
import type { ProbeProfile, ProbeTransport } from '../api/client';
import { getSecrets, loadConfig } from './config';

/** Copies non-blank values; numeric strings stay strings, the server parses them. */
function compact(values: Record<string, string>): ProbeProfile {
  const profile: ProbeProfile = {};
  for (const [key, value] of Object.entries(values)) {
    const trimmed = value.trim();
    if (trimmed.length > 0) {
      profile[key] = trimmed;
    }
  }
  return profile;
}

/** Profile for one transport, taken from the current settings. */
export function buildProfile(transport: ProbeTransport): ProbeProfile {
  const config = loadConfig();
  switch (transport) {
    case 'kafka': {
      const { host, port, topic, groupId } = config.kafka;
      const hostPart = host.trim();
      const portPart = port.trim();
      return compact({
        bootstrapServers: hostPart && portPart ? `${hostPart}:${portPart}` : hostPart,
        topic,
        groupId,
      });
    }
    case 'rabbit': {
      const { host, port, exchange, routingKey, username } = config.rabbit;
      return compact({ host, port, exchange, routingKey, username, password: getSecrets().rabbitPassword });
    }
    case 'fix': {
      const { host, port, senderCompId, targetCompId, resetSeqNum, replayMode, sendNextExpected } = config.fix;
      return {
        ...compact({ host, port, senderCompId, targetCompId }),
        resetSeqNum: resetSeqNum === 'true',
        replayMode,
        sendNextExpected: sendNextExpected === 'true',
      };
    }
  }
}
