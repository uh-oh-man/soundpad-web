import { useState } from 'react';
import { controller } from '../controller';
import { candidateSummary } from '../network/diagnostics';
import type { ConnectionInfo } from '../network/session';

const seconds = (ms: number) => `${(ms / 1000).toFixed(1)} s`;
const VERDICT: Record<string, string> = {
  ok: 'Direct connections should work from this network.',
  'udp-blocked': 'No STUN server answered: UDP is blocked, or an extension blocks WebRTC.',
  'symmetric-nat': 'This network likely uses symmetric NAT, which direct connections cannot cross.',
  unknown: 'No public address found; the reason is unclear.',
};

/**
 * The technical side of the connection (2.37), folded away under "Connection details" in Settings for
 * host and guest: how it is connected, the relay's health, what the network check found, and timings.
 */
export function ConnectionDetails({ info, host }: { info?: ConnectionInfo; host: boolean }) {
  const [checking, setChecking] = useState(false);
  if (!info) return null;
  const relays = info.relay?.relays ?? [];
  const ready = relays.filter((r) => r.state === 'ready').length;
  return (
    <details className="connection-details">
      <summary>Connection details</summary>
      <dl>
        {!host && (
          <>
            <dt>Connection</dt>
            <dd>
              {info.transport === 'direct'
                ? 'Direct (WebRTC)'
                : info.transport === 'relay'
                  ? 'Relayed, slower (public Nostr relays)'
                  : info.reconnecting
                    ? 'Reconnecting'
                    : 'Not connected'}
            </dd>
          </>
        )}
        <dt>WebRTC</dt>
        <dd>{info.webrtc ? 'Available' : 'Missing or blocked in this browser'}</dd>
        {info.diagnosis && (
          <>
            <dt>Likely cause</dt>
            <dd>{info.diagnosis.text}</dd>
          </>
        )}
        <dt>Relay</dt>
        <dd>
          {info.relay
            ? `${info.relay.state}: ${ready} of ${relays.length} relays answered${info.relay.rttMs !== undefined ? `, round trip ${Math.round(info.relay.rttMs)} ms` : ''}`
            : 'Not in use'}
        </dd>
        {relays.map((relay) => (
          <dd key={relay.url} className="relay-health">
            {relay.url.replace(/^wss:\/\//, '')}: {relay.state}
            {relay.latencyMs !== undefined ? `, ${Math.round(relay.latencyMs)} ms` : ''}
            {relay.error ? ` (${relay.error})` : ''}
          </dd>
        ))}
        <dt>Network check</dt>
        <dd>
          {info.ice
            ? `Candidates: ${candidateSummary(info.ice.candidates)}; ${info.ice.errors.length} STUN error${info.ice.errors.length === 1 ? '' : 's'}${info.ice.errors.some((e) => e.errorCode === 701) ? ' (701: server unreachable)' : ''}; ${seconds(info.ice.durationMs)}${info.ice.complete ? '' : ', stopped at the time limit'}. ${VERDICT[info.iceVerdict ?? 'unknown']}`
            : 'Not run yet'}
        </dd>
        {info.webrtc && (
          <dd>
            <button
              aria-disabled={checking || undefined}
              onClick={() => {
                if (checking) return;
                setChecking(true);
                void controller.checkNetwork().finally(() => setChecking(false));
              }}
            >
              {checking ? 'Checking…' : 'Check network'}
            </button>
          </dd>
        )}
        <dt>Timings</dt>
        <dd>
          {info.connectedAt
            ? `Connected ${seconds(info.connectedAt - info.since)} after it started looking`
            : `Looking for ${seconds(Date.now() - info.since)}`}
          {info.relay
            ? `; relay opened ${seconds(info.relay.startedAt - info.startedAt)} after load`
            : ''}
        </dd>
      </dl>
    </details>
  );
}
