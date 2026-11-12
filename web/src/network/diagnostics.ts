/**
 * Connection diagnostics (2.37). When a guest cannot reach the host, say why in words a person can act
 * on, instead of one generic sentence. The classification is pure (tested with made-up candidate lists);
 * `probeIce` is the one part that touches the browser: a throwaway RTCPeerConnection that only gathers
 * candidates with the same STUN servers Trystero uses, and is closed again. It never connects anywhere.
 */
import type { RelayHealth, RelayLinkState } from './relay';

/** Trystero 0.26's default ICE list (`@trystero-p2p/core/dist/peer.mjs`, `defaultIceServers`). */
export const STUN_SERVERS = [
  'stun:stun.l.google.com:19302',
  'stun:stun1.l.google.com:19302',
  'stun:stun2.l.google.com:19302',
  'stun:stun.cloudflare.com:3478',
];

/** Shown to a guest whose browser has no WebRTC (2.25; kept word for word, an e2e test reads it). */
export const NO_WEBRTC =
  "WebRTC blocked. This browser can't reach the host because WebRTC is turned off or not supported. Use an up-to-date Chrome, Edge, Firefox or Safari, and check that no setting or privacy extension blocks WebRTC.";

export type Candidate = {
  type: 'host' | 'srflx' | 'prflx' | 'relay' | string;
  protocol: string;
  address: string;
  port: number;
  relatedAddress?: string;
  relatedPort?: number;
};
export type IceError = { errorCode: number; url: string; errorText?: string };
export type IceReport = {
  candidates: Candidate[];
  errors: IceError[];
  /** How long gathering took, or until it was given up. */
  durationMs: number;
  /** True when gathering finished by itself rather than at the timeout. */
  complete: boolean;
  at: number;
};
/** What the gathered candidates say about this network. */
export type IceVerdict = 'ok' | 'udp-blocked' | 'symmetric-nat' | 'unknown';

/** Reads one `candidate:` line (RFC 8839). Undefined for anything that is not one. */
export function parseCandidate(line: string): Candidate | undefined {
  const parts = line.replace(/^a=/, '').trim().split(/\s+/);
  if (!/^candidate:/.test(parts[0] ?? '') || parts.length < 8 || parts[6] !== 'typ')
    return undefined;
  const port = Number(parts[5]);
  if (!Number.isInteger(port)) return undefined;
  const candidate: Candidate = {
    protocol: parts[2].toLowerCase(),
    address: parts[4],
    port,
    type: parts[7],
  };
  for (let i = 8; i + 1 < parts.length; i += 2) {
    if (parts[i] === 'raddr') candidate.relatedAddress = parts[i + 1];
    if (parts[i] === 'rport') candidate.relatedPort = Number(parts[i + 1]);
  }
  return candidate;
}

/**
 * Judges a gathered list. No server-reflexive (srflx) candidate while every STUN server failed with
 * 701 ("could not be reached") means UDP to the internet is blocked, or a VPN add-on or privacy
 * extension blocks WebRTC (NordVPN's extension does by default). Several srflx ports behind one base
 * mean the network gives each destination its own port (symmetric NAT), which hole punching cannot
 * get through against most routers. Browsers hide the base behind mDNS or 0.0.0.0, so this is a
 * likelihood, not a proof, and the copy says "likely".
 */
export function judgeIce(report: Pick<IceReport, 'candidates' | 'errors'>): IceVerdict {
  const srflx = report.candidates.filter((c) => c.type === 'srflx' || c.type === 'prflx');
  if (!srflx.length) {
    if (report.errors.some((e) => e.errorCode === 701) || !report.candidates.length)
      return 'udp-blocked';
    return 'unknown';
  }
  const ports = new Map<string, Set<number>>();
  for (const c of srflx) {
    const base = `${c.protocol} ${c.address} ${c.relatedAddress ?? ''}:${c.relatedPort ?? ''}`;
    const seen = ports.get(base) ?? new Set<number>();
    seen.add(c.port);
    ports.set(base, seen);
  }
  return [...ports.values()].some((p) => p.size > 1) ? 'symmetric-nat' : 'ok';
}

/** Counts candidate types for the details panel, e.g. "host 2, srflx 1". */
export function candidateSummary(candidates: Candidate[]) {
  const counts = new Map<string, number>();
  for (const c of candidates) counts.set(c.type, (counts.get(c.type) ?? 0) + 1);
  return counts.size ? [...counts].map(([type, n]) => `${type} ${n}`).join(', ') : 'none';
}

/**
 * Gathers candidates on a throwaway peer connection (2.37). Resolves within `timeoutMs` even if
 * gathering never completes, and closes the connection either way. Undefined without WebRTC.
 */
export async function probeIce(
  iceServers: RTCIceServer[] = STUN_SERVERS.map((urls) => ({ urls })),
  timeoutMs = 6000,
): Promise<IceReport | undefined> {
  if (typeof RTCPeerConnection === 'undefined') return undefined;
  const started = Date.now();
  const candidates: Candidate[] = [];
  const errors: IceError[] = [];
  let pc: RTCPeerConnection;
  try {
    pc = new RTCPeerConnection({ iceServers });
  } catch {
    return { candidates, errors, durationMs: 0, complete: false, at: started };
  }
  try {
    const complete = await new Promise<boolean>((resolve) => {
      const timer = setTimeout(() => resolve(false), timeoutMs);
      pc.onicecandidate = (event) => {
        if (!event.candidate) {
          clearTimeout(timer);
          resolve(true);
          return;
        }
        const parsed = parseCandidate(event.candidate.candidate);
        if (parsed) candidates.push(parsed);
      };
      pc.onicecandidateerror = (event) => {
        const e = event as RTCPeerConnectionIceErrorEvent;
        if (errors.length < 32)
          errors.push({ errorCode: e.errorCode, url: e.url ?? '', errorText: e.errorText });
      };
      pc.onicegatheringstatechange = () => {
        if (pc.iceGatheringState === 'complete') {
          clearTimeout(timer);
          resolve(true);
        }
      };
      // A data channel is all an offer needs to gather; no media, no microphone.
      pc.createDataChannel('probe');
      void pc
        .createOffer()
        .then((offer) => pc.setLocalDescription(offer))
        .catch(() => {
          clearTimeout(timer);
          resolve(false);
        });
    });
    return { candidates, errors, durationMs: Date.now() - started, complete, at: started };
  } finally {
    pc.close();
  }
}

export type DiagnosisCause =
  'no-webrtc' | 'no-relays' | 'udp-blocked' | 'symmetric-nat' | 'host-not-found';
export type Diagnosis = {
  cause: DiagnosisCause;
  /** The short line for the header (also the first sentence of `text`). */
  title: string;
  /** The full status: title, cause and what to do. */
  text: string;
};

/** What a guest knows when it looks for a reason. */
export type DiagnosisInput = {
  webrtc: boolean;
  /** The relay link's state, or undefined when the relay was not tried (yet). */
  relayState?: RelayLinkState;
  relays: Pick<RelayHealth, 'state'>[];
  /** Has the relay link finished its first check of every relay? */
  relaysChecked: boolean;
  ice?: IceVerdict;
  /** Milliseconds the guest has looked for the host. */
  elapsedMs: number;
  /** Has any host answered (a welcome, even one that could not be verified, or a lobby reply)? */
  answered: boolean;
};

/** Seconds of looking, with a working relay path, before "host not found" is the honest answer. */
export const HOST_NOT_FOUND_MS = 25000;

const relayCount = (input: DiagnosisInput) => {
  const ready = input.relays.filter((r) => r.state === 'ready').length;
  return `${ready} of ${input.relays.length} public relay${input.relays.length === 1 ? '' : 's'} answered`;
};

/**
 * The cause to show a guest who is not connected, or undefined while it is too early to say. Ordered
 * by what the person can fix: no WebRTC, then no relay at all (nothing can work), then a host that is
 * simply not there. A blocked or strict network matters only while the relay has not connected
 * either, because the relay gets around both.
 */
export function diagnose(input: DiagnosisInput): Diagnosis | undefined {
  if (input.answered) return undefined;
  const relaysDown =
    input.relaysChecked &&
    input.relays.length > 0 &&
    input.relays.every((r) => r.state !== 'ready');
  if (!input.webrtc)
    return {
      cause: 'no-webrtc',
      title: 'WebRTC blocked',
      text: relaysDown
        ? `${NO_WEBRTC} The relay could not take over either: ${relayCount(input)}.`
        : NO_WEBRTC,
    };
  if (relaysDown)
    return {
      cause: 'no-relays',
      title: 'No relay reachable',
      text: `No relay reachable. ${relayCount(input)}, so neither a direct nor a relayed connection can start. This network, a firewall or a VPN may block them: try another network (a phone hotspot works), or pause the VPN or blocker.`,
    };
  if (input.elapsedMs < HOST_NOT_FOUND_MS) return undefined;
  if (input.relayState === 'ready' || input.relayState === 'degraded')
    return {
      cause: 'host-not-found',
      title: 'Host not found',
      text: `Host not found. The network is fine (${relayCount(input)}), but no host answered. The host may be offline, or this invitation was replaced or ended. Ask the host to open Soundpad Web, or for a new link.`,
    };
  if (input.ice === 'udp-blocked')
    return {
      cause: 'udp-blocked',
      title: 'Direct connections blocked',
      text: 'Direct connections blocked. No STUN server answered, so this network blocks UDP or an extension blocks WebRTC (VPN add-ons often do). Still trying the relay. Turn off "Block WebRTC" in the VPN or privacy extension, or try another network.',
    };
  if (input.ice === 'symmetric-nat')
    return {
      cause: 'symmetric-nat',
      title: 'Strict network',
      text: 'Strict network. This network likely uses symmetric NAT, which direct connections cannot get through. Still trying the relay. Another network (a phone hotspot) usually works.',
    };
  return undefined;
}
