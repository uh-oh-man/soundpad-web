// An in-memory stand-in for public Nostr relays, for the relay transport tests. Each FakeRelay
// speaks the relay side of NIP-01 (REQ / EOSE / EVENT / OK / CLOSE) to in-memory sockets and can be
// told to misbehave the way real relays do. Nothing here opens a network connection, and nothing
// here needs the test runner: `tests/manual/relay-live.mjs --dry-run` uses it too.
import type { NostrEvent, WebSocketLike } from '../../web/src/network/relay/nostr';

export type RelayBehaviour = {
  /** Connections never open. */
  dead?: boolean;
  /** Larger events are refused with "invalid: event too large". */
  maxEventBytes?: number;
  /** Events accepted per second from all clients; the rest are refused with "rate-limited:". */
  rateLimit?: number;
  /** Refuse an event with this OK message (for "blocked:", "auth-required:", "pow:" …). */
  reject?: (event: NostrEvent) => string | undefined;
  /** Accept the event (OK true) but never deliver it. */
  drop?: boolean | ((event: NostrEvent) => boolean);
  /** Deliver every event twice. */
  duplicate?: boolean;
  /** Delivery delay in ms. A function that varies makes the relay reorder events. */
  delayMs?: number | ((event: NostrEvent) => number);
  /** Never answer EVENT with OK. */
  noOk?: boolean;
  /**
   * The relay takes in each client's events one after another at this speed; the OK and the
   * delivery follow when an event has been taken in. (Real relays are slow with large events.)
   */
  ingestBytesPerSecond?: number;
  /** Each subscriber is sent its events one after another at this speed. */
  deliverBytesPerSecond?: number;
};

type Filter = { kinds?: number[]; '#x'?: string[]; since?: number };

export class FakeSocket implements WebSocketLike {
  readyState = 0;
  bufferedAmount = 0;
  onopen: ((event: unknown) => void) | null = null;
  onmessage: ((event: { data: unknown }) => void) | null = null;
  onclose: ((event: unknown) => void) | null = null;
  onerror: ((event: unknown) => void) | null = null;
  subs = new Map<string, Filter>();
  /** Until when the relay is busy taking in events from, and sending events to, this client. */
  inBusyUntil = 0;
  outBusyUntil = 0;
  private relay: FakeRelay;
  constructor(relay: FakeRelay) {
    this.relay = relay;
    setTimeout(() => {
      if (this.readyState !== 0) return;
      if (relay.behaviour.dead) {
        this.readyState = 3;
        this.onerror?.({});
        this.onclose?.({});
        return;
      }
      this.readyState = 1;
      relay.sockets.add(this);
      this.onopen?.({});
    }, 5);
  }
  send(data: string) {
    if (this.readyState !== 1) throw Error('Socket is not open');
    this.relay.handle(this, data);
  }
  close() {
    if (this.readyState === 3) return;
    this.readyState = 3;
    this.relay.sockets.delete(this);
    // Like a browser, report the close a moment later, never from inside close().
    if (this.onclose) setTimeout(() => this.onclose?.({}), 0);
  }
  /** The relay drops the connection. */
  drop() {
    if (this.readyState === 3) return;
    this.readyState = 3;
    this.relay.sockets.delete(this);
    this.onclose?.({});
  }
  receive(frame: unknown[]) {
    if (this.readyState === 1) this.onmessage?.({ data: JSON.stringify(frame) });
  }
}

export class FakeRelay {
  sockets = new Set<FakeSocket>();
  /** Every event a client published here and the relay accepted, oldest first. */
  accepted: NostrEvent[] = [];
  /** OK-false messages the relay sent. */
  refusals: string[] = [];
  /** How many sockets were ever opened towards this relay. */
  connections = 0;
  /** Every event a client sent here, the moment it was sent (before it was taken in or refused). */
  incoming: NostrEvent[] = [];
  private recent: number[] = [];
  readonly url: string;
  behaviour: RelayBehaviour;
  constructor(url: string, behaviour: RelayBehaviour) {
    this.url = url;
    this.behaviour = behaviour;
  }

  connect() {
    this.connections++;
    return new FakeSocket(this);
  }

  handle(socket: FakeSocket, data: string) {
    const frame = JSON.parse(data) as unknown[];
    if (frame[0] === 'REQ') {
      socket.subs.set(frame[1] as string, frame[2] as Filter);
      setTimeout(() => socket.receive(['EOSE', frame[1]]), 1);
    } else if (frame[0] === 'CLOSE') socket.subs.delete(frame[1] as string);
    else if (frame[0] === 'EVENT') {
      const event = frame[1] as NostrEvent;
      this.incoming.push(event);
      const rate = this.behaviour.ingestBytesPerSecond;
      if (!rate) {
        this.publish(socket, event);
        return;
      }
      const now = Date.now();
      socket.inBusyUntil = Math.max(now, socket.inBusyUntil) + (data.length / rate) * 1000;
      setTimeout(() => {
        if (socket.readyState === 1) this.publish(socket, event);
      }, socket.inBusyUntil - now);
    }
  }

  private publish(socket: FakeSocket, event: NostrEvent) {
    const b = this.behaviour;
    const now = Date.now();
    this.recent = this.recent.filter((at) => now - at < 1000);
    const size = JSON.stringify(event).length;
    const refusal =
      b.maxEventBytes && size > b.maxEventBytes
        ? `invalid: event too large: ${size}`
        : b.rateLimit !== undefined && this.recent.length >= b.rateLimit
          ? 'rate-limited: slow down'
          : b.reject?.(event);
    if (refusal) {
      this.refusals.push(refusal);
      setTimeout(() => socket.receive(['OK', event.id, false, refusal]), 1);
      return;
    }
    this.recent.push(now);
    this.accepted.push(event);
    if (!b.noOk) setTimeout(() => socket.receive(['OK', event.id, true, '']), 1);
    if (typeof b.drop === 'function' ? b.drop(event) : b.drop) return;
    this.inject(event);
  }

  /** Delivers an event to every matching subscription, as if a client had just published it. */
  inject(event: NostrEvent) {
    const b = this.behaviour;
    const delay = typeof b.delayMs === 'function' ? b.delayMs(event) : (b.delayMs ?? 1);
    for (const socket of this.sockets)
      for (const [id, filter] of socket.subs) {
        const topics = event.tags.filter((t) => t[0] === 'x').map((t) => t[1]);
        const matches =
          (!filter.kinds || filter.kinds.includes(event.kind)) &&
          (!filter['#x'] || filter['#x'].some((t) => topics.includes(t))) &&
          (filter.since === undefined || event.created_at >= filter.since);
        if (!matches) continue;
        for (let copy = 0; copy < (b.duplicate ? 2 : 1); copy++) {
          let wait = delay;
          if (b.deliverBytesPerSecond) {
            const now = Date.now();
            const took = (JSON.stringify(event).length / b.deliverBytesPerSecond) * 1000;
            socket.outBusyUntil = Math.max(now + delay, socket.outBusyUntil) + took;
            wait = socket.outBusyUntil - now;
          }
          setTimeout(() => socket.receive(['EVENT', id, event]), wait);
        }
      }
  }

  /** Sends a raw frame (NOTICE, CLOSED, …) to every connected client. */
  say(frame: (socket: FakeSocket) => unknown[]) {
    for (const socket of this.sockets) socket.receive(frame(socket));
  }

  /** Drops every connection, as a restarting relay does. */
  disconnect() {
    for (const socket of [...this.sockets]) socket.drop();
  }
}

export class FakeHub {
  relays = new Map<string, FakeRelay>();
  add(url: string, behaviour: RelayBehaviour = {}) {
    const relay = new FakeRelay(url, behaviour);
    this.relays.set(url, relay);
    return relay;
  }
  /** Pass as `webSocketFactory`. */
  factory = (url: string): WebSocketLike => {
    const relay = this.relays.get(url);
    if (!relay) throw Error('Unknown relay ' + url);
    return relay.connect();
  };
  get urls() {
    return [...this.relays.keys()];
  }
  /** Sockets currently open across all relays. */
  get open() {
    let n = 0;
    for (const relay of this.relays.values()) n += relay.sockets.size;
    return n;
  }
  /** Every accepted event across all relays, de-duplicated by id. */
  get events() {
    const all = new Map<string, NostrEvent>();
    for (const relay of this.relays.values()) for (const e of relay.accepted) all.set(e.id, e);
    return [...all.values()];
  }
}

/** Repeatable "random" bytes for the `random` option, so reconnect jitter is the same on every run. */
export function seededBytes(seed: number) {
  const next = seeded(seed);
  return (length: number) => Uint8Array.from({ length }, () => Math.floor(next() * 256));
}

/** A repeatable pseudo-random number in [0, 1), for delays that reorder events the same way every run. */
export function seeded(seed: number) {
  return () => {
    seed = (seed + 0x6d2b79f5) | 0;
    let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}
