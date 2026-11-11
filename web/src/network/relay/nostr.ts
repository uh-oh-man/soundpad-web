/**
 * A small Nostr client for carrying opaque app messages over several public relays at once.
 *
 * It publishes signed ephemeral events (kinds 20000–29999, which relays pass on but do not store),
 * subscribes with one tag filter, drops copies of an event that arrive through more than one relay,
 * paces sends per relay, and watches each relay's health. It knows nothing about encryption: the
 * `content` it carries is whatever text the caller hands over.
 *
 * Browser-only APIs: `WebSocket` (injectable) and WebCrypto.
 *
 * One limit comes from the relays themselves: events are stamped with this computer's clock, and
 * relays refuse ephemeral events whose stamp is far from their own time (strfry: more than about a
 * minute old or 15 minutes ahead). A computer with a badly wrong clock therefore fails the
 * self-check on such relays, and `RelayHealth.error` carries the relay's reason. Trystero's
 * signalling has the same limit.
 */
import { schnorr } from '@noble/secp256k1';
import { copy, toBase64, toHex, utf8 } from './bytes';
import type { Bytes } from './bytes';
import { steadyClock } from './clock';

export type NostrEvent = {
  id: string;
  pubkey: string;
  created_at: number;
  kind: number;
  tags: string[][];
  content: string;
  sig: string;
};

/** The part of the browser `WebSocket` this client uses, so tests can supply an in-memory one. */
export type WebSocketLike = {
  readonly readyState: number;
  readonly bufferedAmount?: number;
  send(data: string): void;
  close(): void;
  onopen: ((event: unknown) => void) | null;
  onmessage: ((event: { data: unknown }) => void) | null;
  onclose: ((event: unknown) => void) | null;
  onerror: ((event: unknown) => void) | null;
};
export type WebSocketFactory = (url: string) => WebSocketLike;

/**
 * - `connecting`: opening the socket.
 * - `checking`: connected; waiting for its own test event to come back.
 * - `ready`: passed the check; used for sending and receiving.
 * - `backoff`: the relay said "rate-limited"; still listening, not sending until the pause ends.
 * - `failed`: connected but the test event was refused or never came back; retried later.
 * - `closed`: no connection; retried later.
 * - `retired`: the relay refused us for good (blocked, needs login or proof of work); not retried.
 */
export type RelayState =
  'connecting' | 'checking' | 'ready' | 'backoff' | 'failed' | 'closed' | 'retired';

export type RelayHealth = {
  url: string;
  state: RelayState;
  /** When the relay last accepted one of our events (by the `now` option's clock). */
  lastOkAt?: number;
  /** The last problem this relay reported or showed, in plain words. */
  error?: string;
  /** Publish-to-receive time of the last self-check, in milliseconds. */
  latencyMs?: number;
  sent: number;
  accepted: number;
  rejected: number;
  /** Events received from this relay, including copies another relay delivered first. */
  received: number;
  /** Events this relay was the first to deliver. */
  delivered: number;
  /** True for the relay currently kept free of bulk events (see `PoolTuning.reserveControlRelay`). */
  controlOnly: boolean;
};

export type PoolTuning = {
  /** Events per second sent to one relay, and how many may go at once after a quiet spell. */
  ratePerSecond: number;
  burst: number;
  /** Tokens bulk sends leave untouched, so a control message never waits behind a snapshot. */
  controlReserve: number;
  /** How many relays carry each bulk event. Control events always go to every ready relay. */
  bulkCopies: number;
  /** Bulk sends wait while a socket has more than this many bytes unsent. */
  bulkBufferBytes: number;
  /**
   * Bulk events one relay may hold that it has not yet answered with OK. Relays differ widely in
   * how fast they take large events (40 KB took 0.6 s on one and 3.3 s on another in the 2026-11
   * live check), so bulk is fed to each relay at the speed it acknowledges, and a control event
   * never has more than this many bulk events in front of it on a socket.
   */
  bulkWindow: number;
  /**
   * With two or more relays ready, the quickest one carries no bulk events at all (except as a
   * last resort for an event every other relay failed to take). Control events then always have
   * one path with nothing queued in front of them, whichever leg of a relay is the slow one.
   */
  reserveControlRelay: boolean;
  /**
   * A further copy of a bulk event is not started on another relay later than this after its
   * first copy was written. The receiver would discard it as a duplicate or as too old, and a
   * slow relay would spend its capacity on it.
   */
  bulkCopyWindowMs: number;
  maxQueuedBulk: number;
  /** How long an event may wait for a send slot before it is dropped as too old to matter. */
  controlQueueMs: number;
  bulkQueueMs: number;
  /** How long a relay has to answer an event with OK. Bulk events are large, so they get longer. */
  ackTimeoutMs: number;
  bulkAckTimeoutMs: number;
  openTimeoutMs: number;
  checkTimeoutMs: number;
  reconnectMs: number;
  maxReconnectMs: number;
  rateBackoffMs: number;
  maxRateBackoffMs: number;
  subscriptionRetryMs: number;
  /** Unanswered or refused events in a row before a relay is disconnected and checked again. */
  maxFailures: number;
  /** `settled` resolves once this many relays are ready (or every relay has had its first try). */
  minReady: number;
  /**
   * The subscription asks for events stamped at most this long before our own clock's "now".
   * Relays apply `since` to live events too, so this is how far ahead of a peer's clock ours may
   * run before the relay stops handing us that peer's events. Ephemeral events are not stored, so
   * a generous value costs nothing.
   */
  sinceSeconds: number;
  seenIds: number;
};

export const DEFAULT_TUNING: PoolTuning = {
  ratePerSecond: 8,
  burst: 16,
  controlReserve: 4,
  bulkCopies: 2,
  bulkBufferBytes: 128 * 1024,
  bulkWindow: 2,
  reserveControlRelay: true,
  bulkCopyWindowMs: 5000,
  maxQueuedBulk: 512,
  controlQueueMs: 10_000,
  bulkQueueMs: 60_000,
  ackTimeoutMs: 5000,
  bulkAckTimeoutMs: 12_000,
  openTimeoutMs: 8000,
  checkTimeoutMs: 6000,
  reconnectMs: 2000,
  maxReconnectMs: 120_000,
  rateBackoffMs: 10_000,
  maxRateBackoffMs: 300_000,
  subscriptionRetryMs: 5000,
  maxFailures: 4,
  minReady: 2,
  sinceSeconds: 900,
  seenIds: 8192,
};

/** Optional taps for measurements (the manual live check uses them). Not needed for normal use. */
export type PoolDebug = {
  /** An event was written to one relay's socket. */
  sent?: (info: { id: string; relay: string; bytes: number; at: number }) => void;
  /** One relay delivered an event (called for every copy, before de-duplication). */
  received?: (info: { id: string; relay: string; duplicate: boolean; at: number }) => void;
  /** One relay answered an event with OK (`ok` false: it refused the event). */
  answered?: (info: { id: string; relay: string; ok: boolean; at: number }) => void;
};

export type PoolOptions = {
  /** Relay URLs (`wss://…`). */
  relays: string[];
  /** Ephemeral event kind, 20000–29999. */
  kind: number;
  /** Values of the `x` tag to subscribe to. An event is published under one tag value. */
  topics: string[];
  /**
   * The subscribed tag value the self-check event is published under. Use one only this client
   * listens to, so other clients never see the check. Defaults to the first of `topics`.
   */
  probeTopic?: string;
  /** Called once per distinct event from another publisher. */
  onEvent: (content: string, relay: string) => void;
  /** Called whenever a relay changes state. */
  onChange?: () => void;
  webSocketFactory?: WebSocketFactory;
  /** Wall clock in milliseconds, for event timestamps. Defaults to `Date.now`. */
  now?: () => number;
  random?: (length: number) => Uint8Array;
  tuning?: Partial<PoolTuning>;
  debug?: PoolDebug;
};

export type Lane = 'control' | 'bulk';
/**
 * - `sent`: written to at least one relay's socket (a relay may still lose it).
 * - `cancelled`: `cancel()` was called before any relay got it.
 * - `failed`: no ready relay took it before it expired, or the pool stopped.
 */
export type PublishResult = 'sent' | 'cancelled' | 'failed';
export type Publication = { done: Promise<PublishResult>; cancel: () => void };

const TAG = 'x';
const TERMINAL = /^(blocked|restricted|auth-required|pow):/;

async function sha256(bytes: Bytes) {
  return new Uint8Array(await crypto.subtle.digest('SHA-256', bytes));
}

/** The canonical NIP-01 serialization an event id is the SHA-256 of. */
const serialize = (e: Omit<NostrEvent, 'id' | 'sig'>) =>
  utf8(JSON.stringify([0, e.pubkey, e.created_at, e.kind, e.tags, e.content]));

/** Makes a throwaway BIP-340 key pair from 48 random bytes. */
export function createSigner(random: (length: number) => Uint8Array) {
  const { secretKey, publicKey } = schnorr.keygen(copy(random(48)));
  return { secretKey: copy(secretKey), pubkey: toHex(publicKey) };
}

/** Builds and signs an event (BIP-340 Schnorr over the event id). */
export async function signEvent(
  signer: { secretKey: Uint8Array; pubkey: string },
  draft: { kind: number; created_at: number; tags: string[][]; content: string },
  random: (length: number) => Uint8Array = (n) => crypto.getRandomValues(new Uint8Array(n)),
): Promise<NostrEvent> {
  const body = { ...draft, pubkey: signer.pubkey };
  const id = await sha256(serialize(body));
  const sig = await schnorr.signAsync(id, signer.secretKey, copy(random(32)));
  return { ...body, id: toHex(id), sig: toHex(sig) };
}

/**
 * True when `event.id` really is the hash of the event. Checked before an id is remembered as
 * seen: otherwise one dishonest relay could send junk under the id of a real event it had just
 * received and make us discard the real copy from every honest relay.
 */
export async function hasValidId(event: NostrEvent) {
  return toHex(await sha256(serialize(event))) === event.id;
}

function isEvent(x: unknown): x is NostrEvent {
  if (!x || typeof x !== 'object') return false;
  const e = x as Record<string, unknown>;
  return (
    typeof e.id === 'string' &&
    typeof e.pubkey === 'string' &&
    typeof e.created_at === 'number' &&
    typeof e.kind === 'number' &&
    typeof e.content === 'string' &&
    typeof e.sig === 'string' &&
    Array.isArray(e.tags) &&
    e.tags.every((t) => Array.isArray(t) && t.every((v) => typeof v === 'string'))
  );
}

type Item = {
  lane: Lane;
  topic: string;
  produce: () => Promise<string | undefined>;
  signed?: Promise<NostrEvent | undefined>;
  cancelled: boolean;
  expiresAt: number;
  /** Relays that have taken this item; each relay sends an item at most once. */
  tried: Set<Relay>;
  /** Bulk only: how many more relays should still take it from the shared queue. */
  need: number;
  queued: boolean;
  /** Relays currently holding it (queued for control, or waiting for the relay's OK). */
  active: number;
  written: boolean;
  /** When the first relay wrote it (by the pool's interval clock). */
  writtenAt?: number;
  acked: boolean;
  settled: boolean;
  settle: (result: PublishResult) => void;
};

type Timer = ReturnType<typeof setTimeout>;

class Relay {
  state: RelayState = 'connecting';
  error?: string;
  lastOkAt?: number;
  latencyMs?: number;
  sent = 0;
  accepted = 0;
  rejected = 0;
  received = 0;
  delivered = 0;
  control: Item[] = [];
  /** True once the relay has passed or failed its first check. */
  checked = false;
  private pool: RelayPool;
  readonly url: string;
  private ws?: WebSocketLike;
  private subId = '';
  private subscribed = false;
  private awaiting = new Map<string, { item?: Item; timer?: Timer; bytes: number }>();
  private probes = new Map<string, number>();
  private tokens: number;
  private refilledAt: number;
  private rateScale = 1;
  private sizeLimit = Infinity;
  private failures = 0;
  private attempts = 0;
  private backoffMs = 0;
  private backoffUntil = 0;
  /** Some relays never answer EVENT with OK; once known, a missing OK is not held against them. */
  private silent = false;
  private pumping = false;
  private timers = new Set<Timer>();
  private wakeTimer?: Timer;
  private wakeAt = 0;

  constructor(pool: RelayPool, url: string) {
    this.pool = pool;
    this.url = url;
    this.tokens = pool.tuning.burst;
    this.refilledAt = pool.now();
  }

  private later(run: () => void, ms: number) {
    const timer = setTimeout(() => {
      this.timers.delete(timer);
      if (!this.pool.stopped) run();
    }, ms);
    this.timers.add(timer);
    return timer;
  }
  private cancel(timer?: Timer) {
    if (timer) {
      clearTimeout(timer);
      this.timers.delete(timer);
    }
  }
  private set(state: RelayState) {
    if (this.state === state) return;
    this.state = state;
    this.pool.changed();
  }

  open() {
    if (this.pool.stopped || this.state === 'retired') return;
    this.set('connecting');
    let ws: WebSocketLike;
    try {
      ws = this.pool.factory(this.url);
    } catch {
      this.fail('Could not open a connection.');
      return;
    }
    this.ws = ws;
    const opening = this.later(
      () => this.fail('Timed out opening the connection.'),
      this.pool.tuning.openTimeoutMs,
    );
    ws.onopen = () => {
      if (this.ws !== ws) return;
      this.cancel(opening);
      this.subscribe();
      this.check();
    };
    ws.onmessage = (event) => {
      if (this.ws === ws && typeof event.data === 'string') this.message(event.data);
    };
    ws.onclose = () => {
      if (this.ws === ws) this.fail('Connection closed.');
    };
    ws.onerror = () => {
      if (this.ws === ws) this.fail('Connection error.');
    };
  }

  /** Closes the socket and forgets everything tied to it. Queued and unanswered events go back to the pool. */
  private teardown() {
    for (const timer of this.timers) clearTimeout(timer);
    this.timers.clear();
    this.wakeTimer = undefined;
    const ws = this.ws;
    this.ws = undefined;
    this.subscribed = false;
    if (ws) {
      ws.onopen = ws.onmessage = ws.onclose = ws.onerror = null;
      try {
        ws.close();
      } catch {
        // Already closed.
      }
    }
    this.probes.clear();
    const waiting = [...this.awaiting.values()];
    this.awaiting.clear();
    for (const entry of waiting) if (entry.item) this.pool.release(entry.item, false);
    for (const item of this.control.splice(0)) this.pool.release(item, false);
  }

  stop() {
    this.teardown();
  }

  /** This connection is over; try again later, waiting longer after each failure in a row. */
  private fail(reason: string, minDelay = 0) {
    if (this.state === 'retired') return;
    const checking = this.state === 'checking';
    this.error = reason;
    this.teardown();
    this.checked = true;
    this.attempts++;
    const { reconnectMs, maxReconnectMs } = this.pool.tuning;
    const base = Math.min(maxReconnectMs, reconnectMs * 2 ** (this.attempts - 1));
    // Jitter, so every guest of a session does not reconnect to a recovering relay in the same instant.
    // A relay that asked us to slow down is left alone at least until that pause is over.
    const delay = Math.max(
      minDelay,
      this.backoffUntil - this.pool.now(),
      base * (0.8 + 0.4 * this.pool.chance()),
    );
    this.later(() => this.open(), delay);
    this.set(checking ? 'failed' : 'closed');
    this.pool.changed();
  }

  private retire(reason: string) {
    this.error = reason;
    this.teardown();
    this.checked = true;
    this.set('retired');
    this.pool.changed();
  }

  /** The relay asked us to slow down: stop sending for a while (twice as long each time) and send slower afterwards. */
  private limit(reason: string) {
    const { rateBackoffMs, maxRateBackoffMs } = this.pool.tuning;
    this.error = reason;
    // Several events of one burst can each be refused; that is one complaint, not several.
    if (this.state === 'backoff') return;
    this.rateScale = Math.max(0.125, this.rateScale / 2);
    this.backoffMs = Math.min(
      maxRateBackoffMs,
      this.backoffMs ? this.backoffMs * 2 : rateBackoffMs,
    );
    this.backoffUntil = this.pool.now() + this.backoffMs;
    if (this.state === 'checking') {
      this.fail(reason, this.backoffMs);
      return;
    }
    if (this.state !== 'ready') return;
    this.set('backoff');
    // Other relays carry what was queued here; holding it would only deliver it late.
    for (const item of this.control.splice(0)) this.pool.release(item, false);
    this.later(() => {
      if (this.state !== 'backoff' || this.pool.now() < this.backoffUntil) return;
      if (!this.subscribed) this.subscribe();
      this.set('ready');
      void this.pump();
    }, this.backoffMs);
  }

  private subscribe() {
    this.subId = toHex(this.pool.random(8));
    const { kind, topics } = this.pool.options;
    const since = Math.floor(this.pool.wall() / 1000) - this.pool.tuning.sinceSeconds;
    this.write(JSON.stringify(['REQ', this.subId, { kinds: [kind], [`#${TAG}`]: topics, since }]));
    this.subscribed = true;
  }

  private write(frame: string) {
    try {
      this.ws?.send(frame);
      return true;
    } catch {
      this.fail('Connection error.');
      return false;
    }
  }

  /**
   * Self-check: publish one event and require the relay to hand it back on our own subscription.
   * A relay that accepts events but never delivers them (seen in the 2026-11 probe) fails here.
   */
  private check() {
    this.set('checking');
    const { checkTimeoutMs } = this.pool.tuning;
    this.later(
      () => this.fail('Self-check failed: the test event did not come back.'),
      checkTimeoutMs,
    );
    // Normally the relay's EOSE starts the probe; this covers relays that never send EOSE.
    this.later(() => void this.probe(), Math.min(1500, checkTimeoutMs / 4));
  }

  private async probe() {
    const ws = this.ws;
    if (this.state !== 'checking' || this.probes.size >= 2) return;
    // Reserve the slot now so EOSE and the timer cannot both start the first probe.
    const slot = `pending-${this.probes.size}`;
    this.probes.set(slot, 0);
    const { probeTopic, topics } = this.pool.options;
    const event = await this.pool.sign(toBase64(this.pool.random(96)), probeTopic ?? topics[0]);
    if (this.ws !== ws || this.state !== 'checking') return;
    this.probes.delete(slot);
    this.probes.set(event.id, this.pool.now());
    this.awaiting.set(event.id, { bytes: 0 });
    if (!this.write(JSON.stringify(['EVENT', event]))) return;
    // A subscription can take a moment to go live on the relay after REQ; one retry covers that.
    this.later(() => void this.probe(), this.pool.tuning.checkTimeoutMs / 3);
  }

  private pass(latencyMs: number) {
    for (const timer of this.timers) clearTimeout(timer);
    this.timers.clear();
    this.wakeTimer = undefined;
    // The probes stay in `awaiting`: a relay may deliver the test event before it answers OK, and
    // that OK still tells us this relay acknowledges events at all (see `unanswered`).
    this.probes.clear();
    this.latencyMs = latencyMs;
    this.attempts = 0;
    this.failures = 0;
    this.checked = true;
    this.error = undefined;
    this.tokens = this.pool.tuning.burst;
    this.refilledAt = this.pool.now();
    this.set('ready');
    this.pool.changed();
    void this.pump();
  }

  private message(data: string) {
    if (data.length > 1_000_000) return;
    let frame: unknown;
    try {
      frame = JSON.parse(data);
    } catch {
      return;
    }
    if (!Array.isArray(frame)) return;
    const [type, a, b, c] = frame as unknown[];
    if (type === 'EVENT') {
      if (a !== this.subId || !isEvent(b)) return;
      const { kind, topics } = this.pool.options;
      if (b.kind !== kind || !b.tags.some((t) => t[0] === TAG && topics.includes(t[1]))) return;
      if (b.pubkey === this.pool.pubkey) {
        const sentAt = this.probes.get(b.id);
        if (sentAt !== undefined && this.state === 'checking') this.pass(this.pool.now() - sentAt);
        return;
      }
      this.received++;
      void this.pool.receive(b, this);
    } else if (type === 'OK') {
      if (typeof a === 'string') this.ok(a, b === true, typeof c === 'string' ? c : '');
    } else if (type === 'EOSE') {
      if (a === this.subId && this.state === 'checking' && !this.probes.size) void this.probe();
    } else if (type === 'CLOSED') {
      if (a !== this.subId) return;
      const reason = typeof b === 'string' ? b : '';
      this.subscribed = false;
      if (TERMINAL.test(reason)) this.retire(reason);
      else if (reason.startsWith('rate-limited:')) this.limit(reason);
      else if (this.state === 'checking') this.fail(reason || 'The relay closed the subscription.');
      else {
        this.error = reason || 'The relay closed the subscription.';
        this.later(() => {
          if (!this.subscribed && this.ws) this.subscribe();
        }, this.pool.tuning.subscriptionRetryMs);
        this.pool.changed();
      }
    } else if (type === 'NOTICE') {
      // Free text. Remember it for diagnostics; only an explicit rate complaint changes behaviour.
      const notice = typeof a === 'string' ? a.slice(0, 200) : '';
      if (/rate.?limit|too many|slow down/i.test(notice)) this.limit(notice);
      else if (notice) {
        this.error = notice;
        this.pool.changed();
      }
    }
  }

  private ok(id: string, accepted: boolean, reason: string) {
    const entry = this.awaiting.get(id);
    if (!entry) return;
    this.awaiting.delete(id);
    this.cancel(entry.timer);
    const probing = this.probes.has(id);
    this.pool.options.debug?.answered?.({
      id,
      relay: this.url,
      ok: accepted,
      at: this.pool.wall(),
    });
    if (accepted || reason.startsWith('duplicate:')) {
      this.accepted++;
      this.failures = 0;
      this.lastOkAt = this.pool.wall();
      // Forget a pause only after the relay has behaved for as long again; a complaint soon
      // after resuming doubles it instead.
      if (this.pool.now() >= this.backoffUntil + this.backoffMs) this.backoffMs = 0;
      if (entry.item) this.pool.release(entry.item, true);
      // An answered bulk event makes room for the next one (see `bulkWindow`).
      void this.pump();
      return;
    }
    this.rejected++;
    this.error = reason.slice(0, 200) || 'The relay refused an event.';
    if (entry.item) this.pool.release(entry.item, false);
    if (TERMINAL.test(reason)) this.retire(this.error);
    else if (reason.startsWith('rate-limited:')) this.limit(this.error);
    else if (probing) this.fail('Self-check refused: ' + this.error);
    else if (/too (large|big|long)|max.*size/i.test(reason)) {
      // This relay has a smaller size limit than expected. Keep it for smaller events.
      this.sizeLimit = Math.min(this.sizeLimit, entry.bytes - 1);
      this.pool.changed();
    } else if (++this.failures >= this.pool.tuning.maxFailures)
      this.fail('The relay kept refusing events: ' + this.error);
    else this.pool.changed();
    void this.pump();
  }

  private unanswered(id: string) {
    const entry = this.awaiting.get(id);
    if (!entry) return;
    this.awaiting.delete(id);
    if (!this.accepted && !this.rejected) {
      // Never answered anything, yet it passed the self-check, so it does deliver.
      this.silent = true;
      const waiting = [entry, ...this.awaiting.values()];
      this.awaiting.clear();
      for (const other of waiting) {
        this.cancel(other.timer);
        if (other.item) this.pool.release(other.item, true);
      }
      void this.pump();
      return;
    }
    if (entry.item) this.pool.release(entry.item, false);
    if (++this.failures >= this.pool.tuning.maxFailures)
      this.fail('The relay stopped acknowledging events.');
    else void this.pump();
  }

  private refill() {
    const { ratePerSecond, burst } = this.pool.tuning;
    const now = this.pool.now();
    const gained = (Math.max(0, now - this.refilledAt) * ratePerSecond * this.rateScale) / 1000;
    this.tokens = Math.min(burst, this.tokens + gained);
    this.refilledAt = now;
  }

  private wake(ms: number) {
    const at = this.pool.now() + ms;
    // A control event that can go sooner must not wait for a timer set for a bulk event.
    if (this.wakeTimer && this.wakeAt <= at) return;
    this.cancel(this.wakeTimer);
    this.wakeAt = at;
    this.wakeTimer = this.later(
      () => {
        this.wakeTimer = undefined;
        void this.pump();
      },
      Math.max(1, Math.ceil(ms)),
    );
  }

  /** Picks the next event this relay may send now, or arranges to look again when one can go. */
  private next(ws: WebSocketLike): Item | undefined {
    const { ratePerSecond, burst, controlReserve, bulkBufferBytes, bulkWindow } = this.pool.tuning;
    const perMs = (ratePerSecond * this.rateScale) / 1000;
    const now = this.pool.now();
    while (this.control.length) {
      const item = this.control[0];
      if (item.cancelled || now > item.expiresAt) {
        this.control.shift();
        this.pool.release(item, false);
        continue;
      }
      if (this.tokens < 1) {
        this.wake((1 - this.tokens) / perMs);
        return undefined;
      }
      this.control.shift();
      return item;
    }
    const bulk = this.pool.bulk;
    const floor = 1 + Math.min(controlReserve, burst - 1);
    // Enough bulk is on its way through this relay; `ok` and `unanswered` pump again.
    let flying = 0;
    for (const entry of this.awaiting.values()) if (entry.item?.lane === 'bulk') flying++;
    if (flying >= bulkWindow) return undefined;
    const lastResortOnly = this.pool.controlOnly === this;
    for (let i = 0; i < bulk.length; i++) {
      const item = bulk[i];
      if (this.pool.spent(item, now)) {
        bulk.splice(i--, 1);
        this.pool.unqueue(item);
        continue;
      }
      if (item.tried.has(this)) continue;
      if (lastResortOnly && !this.pool.lastResort(item, this)) continue;
      if (this.tokens < floor) {
        this.wake((floor - this.tokens) / perMs);
        return undefined;
      }
      // Control messages share this socket: never let a snapshot pile up in front of them.
      if ((ws.bufferedAmount ?? 0) > bulkBufferBytes) {
        this.wake(50);
        return undefined;
      }
      item.tried.add(this);
      item.active++;
      if (--item.need <= 0) {
        bulk.splice(i, 1);
        item.queued = false;
      }
      return item;
    }
    return undefined;
  }

  /** Sends what the token bucket allows, control lane first. One loop per relay at a time, so order is kept. */
  async pump() {
    if (this.pumping) return;
    this.pumping = true;
    try {
      for (;;) {
        const ws = this.ws;
        if (this.pool.stopped || this.state !== 'ready' || !ws || ws.readyState !== 1) return;
        this.refill();
        const item = this.next(ws);
        if (!item) return;
        // Signing (and the caller's encryption) happen only now, so a queued event carries the
        // time it was really sent and a cancelled one costs nothing.
        const event = await this.pool.signed(item);
        if (!event || item.cancelled || this.ws !== ws || this.state !== 'ready') {
          this.pool.release(item, false);
          continue;
        }
        const frame = JSON.stringify(['EVENT', event]);
        if (frame.length > this.sizeLimit) {
          this.pool.release(item, false);
          continue;
        }
        this.tokens -= 1;
        const entry: { item?: Item; timer?: Timer; bytes: number } = { bytes: frame.length };
        if (this.silent) this.pool.release(item, true);
        else {
          const { ackTimeoutMs, bulkAckTimeoutMs } = this.pool.tuning;
          entry.item = item;
          entry.timer = this.later(
            () => this.unanswered(event.id),
            item.lane === 'bulk' ? bulkAckTimeoutMs : ackTimeoutMs,
          );
          this.awaiting.set(event.id, entry);
        }
        if (!this.write(frame)) return;
        this.sent++;
        this.pool.wrote(item, this, event.id, frame.length);
      }
    } finally {
      this.pumping = false;
    }
  }

  /** Writes one event at once, outside the pacing (used for the goodbye when the link stops). */
  sendNow(event: NostrEvent) {
    if (this.state === 'ready' && this.ws?.readyState === 1)
      this.write(JSON.stringify(['EVENT', event]));
  }

  health(): RelayHealth {
    const {
      url,
      state,
      lastOkAt,
      error,
      latencyMs,
      sent,
      accepted,
      rejected,
      received,
      delivered,
    } = this;
    return {
      url,
      state,
      lastOkAt,
      error,
      latencyMs,
      sent,
      accepted,
      rejected,
      received,
      delivered,
      controlOnly: this.pool.controlOnly === this,
    };
  }
}

/** Connections to a set of relays that behave as one lossy, duplicate-free broadcast channel. */
export class RelayPool {
  readonly options: PoolOptions;
  readonly tuning: PoolTuning;
  readonly factory: WebSocketFactory;
  /** Wall clock, for event timestamps and reports. */
  readonly wall: () => number;
  /** Interval clock for pacing, timeouts and pauses; unaffected by the wall clock stepping back. */
  readonly now: () => number;
  readonly random: (length: number) => Bytes;
  /** Our throwaway public key (hex). Events signed with it are our own and are not delivered back. */
  readonly pubkey: string;
  /** Resolves when enough relays are ready, or every relay has had its first try. */
  readonly settled: Promise<void>;
  stopped = false;
  bulk: Item[] = [];
  /** The ready relay kept free of bulk events, if any (see `PoolTuning.reserveControlRelay`). */
  controlOnly?: Relay;
  private signer: { secretKey: Bytes; pubkey: string };
  private relays: Relay[];
  private seen = new Set<string>();
  private settle!: () => void;
  private sweeper?: Timer;
  private started = false;

  constructor(options: PoolOptions) {
    this.options = options;
    this.tuning = { ...DEFAULT_TUNING, ...options.tuning };
    this.factory =
      options.webSocketFactory ?? ((url) => new WebSocket(url) as unknown as WebSocketLike);
    this.wall = options.now ?? Date.now;
    this.now = steadyClock(this.wall);
    const random =
      options.random ?? ((length: number) => crypto.getRandomValues(new Uint8Array(length)));
    this.random = (length) => copy(random(length));
    this.signer = createSigner(this.random);
    this.pubkey = this.signer.pubkey;
    this.settled = new Promise((resolve) => (this.settle = resolve));
    this.relays = [...new Set(options.relays)].map((url) => new Relay(this, url));
  }

  /** Opens every relay. Safe to call once. */
  start() {
    if (this.started || this.stopped) return;
    this.started = true;
    if (!this.relays.length) this.settle();
    for (const relay of this.relays) relay.open();
  }

  /** Closes every socket and timer. Unsent events resolve as `failed`. */
  stop() {
    if (this.stopped) return;
    this.stopped = true;
    if (this.sweeper) clearTimeout(this.sweeper);
    this.sweeper = undefined;
    for (const relay of this.relays) relay.stop();
    for (const item of this.bulk.splice(0)) item.settle('failed');
    this.settle();
  }

  /** A number in [0, 1) from the injected randomness. */
  chance() {
    return new DataView(this.random(4).buffer).getUint32(0) / 0x100000000;
  }

  sign(content: string, topic: string) {
    const created_at = Math.floor(this.wall() / 1000);
    const draft = { kind: this.options.kind, created_at, tags: [[TAG, topic]], content };
    return signEvent(this.signer, draft, this.random);
  }

  signed(item: Item) {
    item.signed ??= item
      .produce()
      .then((content) =>
        content === undefined || this.stopped ? undefined : this.sign(content, item.topic),
      )
      .catch(() => undefined);
    return item.signed;
  }

  changed() {
    const ready = this.readyCount();
    if (ready >= this.tuning.minReady || this.relays.every((r) => r.checked)) this.settle();
    this.reserve();
    this.options.onChange?.();
  }

  /** Chooses the relay that carries no bulk events (see `reserveControlRelay`). */
  private reserve() {
    const ready = this.relays.filter((r) => r.state === 'ready');
    let next: Relay | undefined;
    if (this.tuning.reserveControlRelay && ready.length >= 2)
      // Keep the current choice while it works: moving it would only put bulk events in front of
      // control events on one more relay. Otherwise take the one whose self-check was quickest.
      next =
        this.controlOnly && ready.includes(this.controlOnly)
          ? this.controlOnly
          : ready.reduce((a, b) => ((b.latencyMs ?? Infinity) < (a.latencyMs ?? Infinity) ? b : a));
    if (next === this.controlOnly) return;
    this.controlOnly = next;
    // The relay that lost the reservation may now take bulk events. Not from inside this call:
    // it runs in the middle of a relay's state change.
    queueMicrotask(() => {
      if (!this.stopped) for (const relay of this.relays) void relay.pump();
    });
  }

  /** True when an event should not be started on any further relay. */
  spent(item: Item, now: number) {
    return (
      item.cancelled ||
      now > item.expiresAt ||
      (item.lane === 'bulk' &&
        item.writtenAt !== undefined &&
        now - item.writtenAt > this.tuning.bulkCopyWindowMs)
    );
  }

  /** True when no relay accepted or still holds this bulk event and every other ready relay has had it. */
  lastResort(item: Item, relay: Relay) {
    return (
      !item.acked &&
      item.active <= 0 &&
      this.relays.every((r) => r === relay || r.state !== 'ready' || item.tried.has(r))
    );
  }

  readyCount() {
    return this.relays.filter((r) => r.state === 'ready').length;
  }

  health(): RelayHealth[] {
    return this.relays.map((r) => r.health());
  }

  /**
   * Queues one event under the tag value `topic`. `produce` is called once, just before the first
   * relay sends it, and returns the event content (or `undefined` to drop it). Control events go
   * to every ready relay ahead of all bulk events; bulk events go to `bulkCopies` relays.
   */
  publish(
    topic: string,
    produce: () => Promise<string | undefined>,
    lane: Lane = 'control',
  ): Publication {
    let resolve!: (result: PublishResult) => void;
    const done = new Promise<PublishResult>((r) => (resolve = r));
    const now = this.now();
    const ready = this.relays.filter((r) => r.state === 'ready');
    // Asking for more copies than there are relays to carry them would leave the event queued.
    const carriers = ready.length - (this.controlOnly ? 1 : 0);
    const item: Item = {
      lane,
      topic,
      produce,
      cancelled: false,
      expiresAt: now + (lane === 'control' ? this.tuning.controlQueueMs : this.tuning.bulkQueueMs),
      tried: new Set(),
      need: Math.max(1, Math.min(this.tuning.bulkCopies, carriers)),
      queued: false,
      active: 0,
      written: false,
      acked: false,
      settled: false,
      settle: (result) => {
        if (item.settled) return;
        item.settled = true;
        resolve(result);
      },
    };
    const cancel = () => {
      item.cancelled = true;
      item.settle('cancelled');
      // Leave the shared queue at once, so abandoned snapshots never count against its limit.
      const at = this.bulk.indexOf(item);
      if (at >= 0) this.bulk.splice(at, 1);
      item.queued = false;
    };
    if (this.stopped || !ready.length) item.settle('failed');
    else if (lane === 'control')
      for (const relay of ready) {
        item.tried.add(relay);
        item.active++;
        relay.control.push(item);
        void relay.pump();
      }
    else if (this.bulk.length >= this.tuning.maxQueuedBulk) item.settle('failed');
    else {
      item.queued = true;
      this.bulk.push(item);
      this.sweep();
      for (const relay of ready) void relay.pump();
    }
    return { done, cancel };
  }

  /** Signs and writes one event to every ready relay at once, skipping the queues. */
  async publishNow(topic: string, content: string) {
    const event = await this.sign(content, topic);
    if (!this.stopped) for (const relay of this.relays) relay.sendNow(event);
  }

  /** A relay is done with an item: it was accepted (`ok`) or it failed there. */
  release(item: Item, ok: boolean) {
    item.active--;
    if (ok) item.acked = true;
    else if (!item.acked && !this.stopped && !this.spent(item, this.now())) {
      // Refused, unanswered or the connection dropped, and no relay has confirmed it: try another.
      if (item.lane === 'bulk') {
        item.need++;
        if (!item.queued) {
          item.queued = true;
          this.bulk.unshift(item);
          this.sweep();
        }
        for (const relay of this.relays) if (!item.tried.has(relay)) void relay.pump();
      } else
        for (const relay of this.relays)
          if (relay.state === 'ready' && !item.tried.has(relay)) {
            item.tried.add(relay);
            item.active++;
            relay.control.unshift(item);
            void relay.pump();
          }
    }
    this.unqueue(item);
  }

  /** Resolves an item that can no longer reach any relay. */
  unqueue(item: Item) {
    if (!this.bulk.includes(item)) item.queued = false;
    if (!item.written && !item.queued && item.active <= 0)
      item.settle(item.cancelled ? 'cancelled' : 'failed');
  }

  wrote(item: Item, relay: Relay, id: string, bytes: number) {
    item.written = true;
    item.writtenAt ??= this.now();
    item.settle('sent');
    this.options.debug?.sent?.({ id, relay: relay.url, bytes, at: this.wall() });
  }

  /** While bulk events wait, drop the ones that expired with no relay able to take them. */
  private sweep() {
    if (this.sweeper || this.stopped) return;
    this.sweeper = setTimeout(() => {
      this.sweeper = undefined;
      const now = this.now();
      for (const item of this.bulk.filter((i) => this.spent(i, now))) {
        this.bulk.splice(this.bulk.indexOf(item), 1);
        this.unqueue(item);
      }
      if (this.bulk.length) this.sweep();
    }, 1000);
  }

  async receive(event: NostrEvent, relay: Relay) {
    const duplicate = this.seen.has(event.id);
    this.options.debug?.received?.({ id: event.id, relay: relay.url, duplicate, at: this.wall() });
    if (duplicate || !(await hasValidId(event))) return;
    if (this.stopped || this.seen.has(event.id)) return;
    this.seen.add(event.id);
    // A Set keeps insertion order, so the first key is the oldest id.
    if (this.seen.size > this.tuning.seenIds) this.seen.delete(this.seen.values().next().value!);
    relay.delivered++;
    this.options.onEvent(event.content, relay.url);
  }
}
