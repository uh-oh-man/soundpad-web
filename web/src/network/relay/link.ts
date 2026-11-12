/**
 * Relay link: a fallback message path between session peers through public Nostr relays, for when
 * two networks cannot open a direct WebRTC connection. It carries the app's own small messages
 * (commands, acknowledgements, state, short snapshots), never audio streams or uploads.
 *
 * Shape: `createRelayLink({ room, secret, selfId })` gives something a session can use like a peer
 * connection: `send(peerId, value)`, `onMessage`, `onPeer`. See `RelayLink` for the guarantees.
 */
import { Reader, Writer, concat, copy, equal, fromUtf8, toHex, utf8 } from './bytes';
import type { Bytes } from './bytes';
import {
  BYE,
  DATA,
  EPOCH_BYTES,
  Freshness,
  HELLO,
  NONCE_BYTES,
  PING,
  PUBLIC_KEY_BYTES,
  ReplayWindow,
  SEAL_OVERHEAD,
  createLocalKey,
  derivePairKey,
  deriveRoomKeys,
  describeKey,
  encodeHeader,
  inboxTopic,
  openInner,
  openPacket,
  readLiveness,
  sealInner,
  sealPacket,
  writeLiveness,
} from './channel';
import type { Liveness, LocalKey, Packet, RoomKeys } from './channel';
import { steadyClock } from './clock';
import { RelayPool } from './nostr';
import type {
  Lane,
  PoolDebug,
  PoolTuning,
  Publication,
  RelayHealth,
  WebSocketFactory,
} from './nostr';
import { DEFAULT_RELAYS } from './relays';
import {
  CHUNK_HEADER_BYTES,
  MAX_KEY_BYTES,
  Reassembler,
  decodeChunk,
  encodeChunk,
} from './transfer';

export type JsonValue =
  null | boolean | number | string | JsonValue[] | { [key: string]: JsonValue };

/**
 * Which peers a link pairs with. A `guest` only pairs with a `host` (and the reverse), so twelve
 * guests in relay mode do not also keep connections to each other. `peer` pairs with everyone.
 */
export type RelayRole = 'peer' | 'host' | 'guest';

export type RelayLinkOptions = {
  /** `Invite.room` and `Invite.secret`; every key is derived from these two. */
  room: string;
  secret: string;
  /** This peer's id (the session's own peer id). 1–64 bytes of UTF-8. */
  selfId: string;
  role?: RelayRole;
  /** Relay URLs to try. Defaults to `DEFAULT_RELAYS`. Both ends must share at least one working relay. */
  relays?: string[];
  webSocketFactory?: WebSocketFactory;
  /**
   * Wall clock in milliseconds. Defaults to `Date.now`. Used for relay event timestamps and for
   * measuring intervals (it may step; see `clock.ts`). Never compared with a peer's clock.
   */
  now?: () => number;
  /** Source of random bytes. Defaults to `crypto.getRandomValues`. (ECDH keys always come from WebCrypto.) */
  random?: (length: number) => Uint8Array;
  /** A packet delayed longer than this is dropped. Default 15 000. */
  maxAgeMs?: number;
  /** Largest message `send` accepts. Default 4 MiB. */
  maxPayloadBytes?: number;
  /** Largest message accepted from a peer. Default 4 MiB; a host should set this far lower for guests. */
  maxReceiveBytes?: number;
  /**
   * Largest encrypted packet per event. Default 44 000 bytes, which is a 59 KB event after base64:
   * strfry relays refuse events above 64 KiB (a 100 KB event was rejected in the 2026-11 probe).
   */
  maxCiphertextBytes?: number;
  /** Relays that must pass the self-check for state `ready`. With fewer the state is `degraded`. Default 2. */
  minRelays?: number;
  /** How often each side pings a connected peer. Default 5000. */
  helloIntervalMs?: number;
  /** First interval between beacons while looking for a peer; it grows to 10 s. Default 2000. */
  seekIntervalMs?: number;
  /** A peer not heard from for this long is reported `lost`. Default 20 000. */
  peerTimeoutMs?: number;
  /** An unfinished incoming message is dropped when no chunk arrives for this long. Default 20 000. */
  transferTimeoutMs?: number;
  /**
   * How long a control message waits for an earlier one from the same sender that has not arrived,
   * before it is delivered anyway and the earlier one is discarded if it turns up. Default 400;
   * 0 delivers control messages in arrival order.
   */
  orderWaitMs?: number;
  /** Per-relay pacing and timeouts; see `PoolTuning`. */
  tuning?: Partial<PoolTuning>;
  debug?: PoolDebug;
};

/**
 * - `idle`: `start()` not called yet.
 * - `connecting`: relays are being opened and checked; none is usable yet.
 * - `ready`: at least `minRelays` relays passed the self-check.
 * - `degraded`: fewer than `minRelays` passed, but at least one works; messages still flow.
 * - `failed`: no relay works right now (they are retried in the background).
 * - `stopped`: `stop()` was called.
 */
export type RelayLinkState = 'idle' | 'connecting' | 'ready' | 'degraded' | 'failed' | 'stopped';

/**
 * - `sent`: every chunk was written to at least one relay. Delivery is likely, not certain: the
 *   session keeps its own acknowledgements and timeouts.
 * - `superseded`: a newer `send` with the same `supersedeKey` replaced this one before it was all sent.
 * - `failed`: no relay could take it in time.
 * - `too-large`: above `maxPayloadBytes`.
 * - `unknown-peer`: that peer is not connected over the relay link (no `seen` yet, or `lost`).
 * - `stopped`: the link is stopped.
 */
export type SendResult =
  'sent' | 'superseded' | 'failed' | 'too-large' | 'unknown-peer' | 'stopped';

export type SendOptions = {
  /**
   * `control` messages go to every relay ahead of all bulk traffic and arrive in the order sent.
   * `bulk` messages yield to control messages and are unordered. Default: `control` when the
   * message fits one event, else `bulk`.
   */
  priority?: Lane;
  /**
   * Names a stream where only the newest message matters (for example `'state'`). Sending again
   * with the same key to the same peer abandons the unsent rest of the older message, and the
   * receiver discards anything older that still turns up. At most 64 bytes.
   */
  supersedeKey?: string;
};

export type RelayPeer = {
  id: string;
  role: RelayRole;
  /** Fingerprint of the peer's link key; see `RelayLinkStatus.key`. */
  key: string;
  /** Last measured round trip to this peer through the relays. */
  rttMs?: number;
  /** When a verified packet last arrived from this peer (by the `now` option's clock). */
  lastHeardAt: number;
};

/** Why incoming packets were ignored, counted since `start()`. */
export type DropCounts = {
  /** Not made with this invitation's keys, or altered. */
  unauthentic: number;
  /** Addressed to another peer, or to an earlier run of this one. */
  recipient: number;
  /** A sequence number that was already used. */
  replay: number;
  /** Older than `maxAgeMs`. */
  stale: number;
  /** A directed packet from a peer that has not proven it is live. */
  unknownPeer: number;
  /** Arrived after a newer message with the same supersede key. */
  superseded: number;
  /** A control message that arrived after later ones had already been delivered. */
  late: number;
  malformed: number;
};

export type RelayLinkStatus = {
  state: RelayLinkState;
  relays: RelayHealth[];
  peers: RelayPeer[];
  /** Lowest current round trip among connected peers. */
  rttMs?: number;
  /**
   * Fingerprint of this link's own key (set once `start()` has resolved). The link proves only that
   * a peer holds the invitation, not who it is. To let a guest tell that the relay peer called
   * "host" really is the host, the host can put this value inside its signed welcome, and the guest
   * compares it with `peers[].key` for the peer the welcome came from.
   */
  key?: string;
  dropped: DropCounts;
};

export type RelayLink = {
  /** Opens the relays and starts listening. Resolves once the relays have been checked; see `status().state`. */
  start(): Promise<void>;
  /** Tells connected peers goodbye, then closes every socket and timer. Resolves when that is done. */
  stop(): Promise<void>;
  /** Sends one message to a connected peer. Never rejects for network reasons; see `SendResult`. */
  send(toPeerId: string, value: JsonValue | Uint8Array, options?: SendOptions): Promise<SendResult>;
  /**
   * Announces "I want to talk over the relay" to everyone holding the invitation, and keeps
   * announcing until a peer answers. A link that only waits for others (the host) need not call it:
   * it answers announcements by itself.
   */
  broadcastHello(): void;
  /** Called once per message, with the sender's verified peer id. Returns a function that removes the listener. */
  onMessage(listener: (fromPeerId: string, value: JsonValue | Uint8Array) => void): () => void;
  /** `seen`: the peer proved it is live and can be sent to. `lost`: it left, restarted or went silent. */
  onPeer(listener: (peerId: string, event: 'seen' | 'lost') => void): () => void;
  /** Called when `status().state` or a relay's state changes. */
  onStatus(listener: (status: RelayLinkStatus) => void): () => void;
  status(): RelayLinkStatus;
};

type Timer = ReturnType<typeof setTimeout>;
type Transfer = {
  id: number;
  order?: number;
  cancelled: boolean;
  superseded: boolean;
  cancel: () => void;
};
type Peer = {
  id: string;
  /** `id/epoch`: names one run of the peer. */
  tag: string;
  epoch: Bytes;
  keyId: string;
  role: number;
  inbox: string;
  pairKey: CryptoKey;
  /** The peer has echoed one of our pings, so it is live and may send data. */
  anchored: boolean;
  gone: boolean;
  createdAt: number;
  window: ReplayWindow;
  fresh: Freshness;
  lastHeardAt: number;
  pingQueuedAt: number;
  myPings: Map<string, number>;
  theirPing?: { nonce: Bytes; at: number };
  rttMs?: number;
  /** Control messages: last order number sent, next expected, and those waiting for an earlier one. */
  outOrder: number;
  inNext: number;
  held: Map<number, JsonValue | Uint8Array>;
  gapTimer?: Timer;
  /** Newest message id seen per supersede key. */
  newest: Map<string, number>;
  outgoing: Set<Transfer>;
  keyed: Map<string, Transfer>;
};

const ROLES: RelayRole[] = ['peer', 'host', 'guest'];
const MIB = 1024 * 1024;
const noEpoch = new Uint8Array(EPOCH_BYTES);

/** Creates a relay link. Nothing touches the network until `start()`. */
export function createRelayLink(options: RelayLinkOptions): RelayLink {
  const selfId = options.selfId;
  const selfBytes = utf8(selfId).length;
  if (!selfBytes || selfBytes > 64) throw RangeError('selfId must be 1 to 64 bytes.');
  const role = ROLES.indexOf(options.role ?? 'peer');
  const wall = options.now ?? Date.now;
  const clock = steadyClock(wall);
  const randomSource =
    options.random ?? ((length: number) => crypto.getRandomValues(new Uint8Array(length)));
  const random = (length: number) => copy(randomSource(length));
  const maxAgeMs = options.maxAgeMs ?? 15_000;
  const maxPayloadBytes = options.maxPayloadBytes ?? 4 * MIB;
  const maxReceiveBytes = options.maxReceiveBytes ?? 4 * MIB;
  const maxCiphertextBytes = options.maxCiphertextBytes ?? 44_000;
  const minRelays = options.minRelays ?? 2;
  const helloIntervalMs = options.helloIntervalMs ?? 5000;
  const seekIntervalMs = options.seekIntervalMs ?? 2000;
  const peerTimeoutMs = options.peerTimeoutMs ?? 20_000;
  const orderWaitMs = options.orderWaitMs ?? 400;
  // An id is not handed to a new run while its current run is clearly alive. This stops another
  // holder of the invitation from pushing a connected peer aside by claiming its id.
  const takeoverAfterMs = 2 * helloIntervalMs + 2000;

  let keys: RoomKeys | undefined;
  let local: LocalKey | undefined;
  let inbox = '';
  let pool: RelayPool | undefined;
  let starting: Promise<void> | undefined;
  let stopping: Promise<void> | undefined;
  let stopped = false;
  let settled = false;
  let seq = 0;
  let messages = 0;
  let announcing = false;
  let helloWaiting = false;
  let helloAt = -Infinity;
  let seekMs = seekIntervalMs;
  let beat: ReturnType<typeof setInterval> | undefined;
  let lastState: RelayLinkState = 'idle';
  let queue = Promise.resolve();

  const peers = new Map<string, Peer>();
  const candidates = new Map<string, Peer>();
  /** Highest sequence number seen from runs of peers we no longer track, so their old packets stay refused. */
  const retired = new Map<string, number>();
  const broadcastPings = new Map<string, number>();
  const reassembler = new Reassembler({
    maxMessageBytes: maxReceiveBytes,
    maxPendingBytes: Math.max(4 * maxReceiveBytes, 16 * MIB),
    maxPendingPerOwner: 8,
    timeoutMs: options.transferTimeoutMs ?? 20_000,
  });
  const dropped: DropCounts = {
    unauthentic: 0,
    recipient: 0,
    replay: 0,
    stale: 0,
    unknownPeer: 0,
    superseded: 0,
    late: 0,
    malformed: 0,
  };
  const messageListeners = new Set<(from: string, value: JsonValue | Uint8Array) => void>();
  const peerListeners = new Set<(peerId: string, event: 'seen' | 'lost') => void>();
  const statusListeners = new Set<(status: RelayLinkStatus) => void>();

  /** A listener that throws must not stop packet handling, and must not be silenced either. */
  function emit<A extends unknown[]>(listeners: Set<(...args: A) => void>, ...args: A) {
    for (const listener of [...listeners])
      try {
        listener(...args);
      } catch (error) {
        queueMicrotask(() => {
          throw error;
        });
      }
  }

  function state(): RelayLinkState {
    if (stopped) return 'stopped';
    if (!starting) return 'idle';
    const ready = pool?.readyCount() ?? 0;
    if (ready >= minRelays) return 'ready';
    if (ready > 0) return 'degraded';
    return settled ? 'failed' : 'connecting';
  }

  function status(): RelayLinkStatus {
    const list = [...peers.values()].map((p) => ({
      id: p.id,
      role: ROLES[p.role],
      key: p.keyId,
      rttMs: p.rttMs,
      lastHeardAt: wall() - (clock() - p.lastHeardAt),
    }));
    const rtts = list.flatMap((p) => (p.rttMs === undefined ? [] : [p.rttMs]));
    return {
      state: state(),
      relays: pool?.health() ?? [],
      peers: list,
      rttMs: rtts.length ? Math.min(...rtts) : undefined,
      key: local?.keyId,
      dropped: { ...dropped },
    };
  }

  function notify() {
    lastState = state();
    if (statusListeners.size) emit(statusListeners, status());
  }

  function remember<V>(map: Map<string, V>, key: string, value: V, limit: number) {
    map.delete(key);
    map.set(key, value);
    if (map.size > limit) map.delete(map.keys().next().value!);
  }

  // ---- sending ------------------------------------------------------------------------------

  /** Builds one directed packet. Called at the moment a relay is about to send it, so the sequence number and time are current. */
  async function sealDirected(
    peer: Peer,
    type: number,
    payload?: Bytes,
    ping = false,
    echo = peer.theirPing,
  ) {
    const now = clock();
    const header = encodeHeader({
      type,
      sender: selfId,
      senderEpoch: local!.epoch,
      recipient: peer.id,
      recipientEpoch: peer.epoch,
      seq: seq++,
      timestamp: now,
    });
    const live: Liveness = { want: !peer.anchored };
    if (ping) {
      live.ping = random(NONCE_BYTES);
      remember(peer.myPings, toHex(live.ping), now, 4);
    }
    if (echo && now - echo.at <= maxAgeMs) {
      live.echo = echo.nonce;
      live.holdMs = now - echo.at;
    }
    const inner = writeLiveness(new Writer(), live);
    if (payload) inner.bytes(payload);
    const sealed = await sealInner(peer.pairKey, header, inner.done(), random);
    // A ping carries our public key in the room layer, so a peer that has never heard of us can
    // derive the pair key and open the rest.
    const body = type === PING ? concat(local!.publicKey, Uint8Array.of(role), sealed) : sealed;
    return sealPacket(keys!.roomKey, header, body, random);
  }

  function sendPing(peer: Peer, echo?: { nonce: Bytes; at: number }) {
    peer.pingQueuedAt = clock();
    pool?.publish(peer.inbox, async () =>
      stopped || peer.gone ? undefined : sealDirected(peer, PING, undefined, true, echo),
    );
  }

  function hello() {
    if (stopped || !pool || !keys || !local) return;
    if (!pool.readyCount()) {
      helloWaiting = true;
      return;
    }
    helloWaiting = false;
    helloAt = clock();
    const roomKey = keys.roomKey;
    const publicKey = local.publicKey;
    const epoch = local.epoch;
    pool.publish(keys.roomTopic, async () => {
      if (stopped) return undefined;
      const now = clock();
      const nonce = random(NONCE_BYTES);
      remember(broadcastPings, toHex(nonce), now, 8);
      const header = encodeHeader({
        type: HELLO,
        sender: selfId,
        senderEpoch: epoch,
        recipient: '',
        recipientEpoch: noEpoch,
        seq: seq++,
        timestamp: now,
      });
      return sealPacket(roomKey, header, concat(publicKey, Uint8Array.of(role), nonce), random);
    });
  }

  function broadcastHello() {
    announcing = true;
    seekMs = seekIntervalMs;
    hello();
  }

  function chunkCapacity(peer: Peer, keyBytes: number) {
    const header = 2 + 1 + selfBytes + EPOCH_BYTES + 1 + utf8(peer.id).length + EPOCH_BYTES + 16;
    const liveness = 1 + NONCE_BYTES + 4;
    return (
      maxCiphertextBytes -
      SEAL_OVERHEAD -
      2 -
      header -
      SEAL_OVERHEAD -
      liveness -
      CHUNK_HEADER_BYTES -
      keyBytes
    );
  }

  async function send(
    to: string,
    value: JsonValue | Uint8Array,
    sendOptions: SendOptions = {},
  ): Promise<SendResult> {
    const peer = peers.get(to);
    if (stopped || !pool) return 'stopped';
    if (!peer) return 'unknown-peer';
    const binary = value instanceof Uint8Array;
    const bytes = binary ? copy(value) : utf8(JSON.stringify(value));
    if (bytes.length > maxPayloadBytes) return 'too-large';
    const key = sendOptions.supersedeKey ?? '';
    const keyBytes = utf8(key).length;
    if (keyBytes > MAX_KEY_BYTES) throw RangeError('supersedeKey must be at most 64 bytes.');
    const size = chunkCapacity(peer, keyBytes);
    if (size < 256) throw RangeError('maxCiphertextBytes is too small.');
    const count = Math.max(1, Math.ceil(bytes.length / size));
    if (count > 0xffff) return 'too-large';
    const lane: Lane = sendOptions.priority ?? (count === 1 ? 'control' : 'bulk');

    const publications: Publication[] = [];
    const transfer: Transfer = {
      id: ++messages,
      cancelled: false,
      superseded: false,
      cancel: () => {
        transfer.cancelled = true;
        for (const p of publications) p.cancel();
      },
    };
    if (key) {
      const older = peer.keyed.get(key);
      if (older) {
        older.superseded = true;
        older.cancel();
      }
      peer.keyed.set(key, transfer);
    }
    peer.outgoing.add(transfer);
    for (let index = 0; index < count; index++) {
      const data = bytes.subarray(index * size, (index + 1) * size) as Bytes;
      const publication = pool.publish(
        peer.inbox,
        async () => {
          if (stopped || peer.gone || transfer.cancelled) return undefined;
          // Numbered when the first chunk really goes out, so a message that was superseded
          // before sending leaves no gap for the receiver to wait on.
          transfer.order ??= lane === 'control' ? ++peer.outOrder : 0;
          const order = transfer.order;
          const total = bytes.length;
          const chunk = { messageId: transfer.id, order, index, count, binary, key, total, data };
          return sealDirected(peer, DATA, encodeChunk(chunk));
        },
        lane,
      );
      publications.push(publication);
      // One lost chunk makes the rest useless; stop spending relay budget on them.
      void publication.done.then((result) => {
        if (result === 'failed') transfer.cancel();
      });
    }
    const results = await Promise.all(publications.map((p) => p.done));
    peer.outgoing.delete(transfer);
    if (peer.keyed.get(key) === transfer) peer.keyed.delete(key);
    if (results.every((r) => r === 'sent')) return 'sent';
    if (stopped) return 'stopped';
    if (peer.gone) return 'unknown-peer';
    return transfer.superseded && !results.includes('failed') ? 'superseded' : 'failed';
  }

  // ---- peers --------------------------------------------------------------------------------

  const compatible = (theirs: number) => role === 0 || theirs === 0 || role !== theirs;

  function lose(peer: Peer) {
    if (peer.gone) return;
    peer.gone = true;
    for (const transfer of [...peer.outgoing]) transfer.cancel();
    if (peer.gapTimer) clearTimeout(peer.gapTimer);
    peer.gapTimer = undefined;
    peer.held.clear();
    reassembler.dropOwner(peer.tag);
    remember(retired, peer.tag, peer.window.top, 256);
    candidates.delete(peer.tag);
    if (peers.get(peer.id) === peer) {
      peers.delete(peer.id);
      emit(peerListeners, peer.id, 'lost');
      notify();
    }
  }

  function anchor(peer: Peer) {
    candidates.delete(peer.tag);
    const previous = peers.get(peer.id);
    if (previous) lose(previous);
    peer.anchored = true;
    peers.set(peer.id, peer);
    emit(peerListeners, peer.id, 'seen');
    notify();
  }

  /** Finds or creates the not-yet-verified record for one run of a peer. */
  async function candidate(id: string, epoch: Bytes, publicKey: Bytes, theirRole: number) {
    const tag = `${id}/${toHex(epoch)}`;
    const existing = candidates.get(tag);
    if (existing) return existing;
    if (theirRole >= ROLES.length || !compatible(theirRole) || utf8(id).length > 64)
      return undefined;
    const current = peers.get(id);
    if (current && clock() - current.lastHeardAt < takeoverAfterMs) return undefined;
    const described = await describeKey(publicKey);
    if (!equal(described.epoch, epoch)) return undefined;
    const pairKey = await derivePairKey(keys!, local!, selfId, id, publicKey);
    if (!pairKey) return undefined;
    const now = clock();
    const peer: Peer = {
      id,
      tag,
      epoch,
      keyId: described.keyId,
      role: theirRole,
      inbox: await inboxTopic(keys!, publicKey),
      pairKey,
      anchored: false,
      gone: false,
      createdAt: now,
      window: new ReplayWindow(retired.get(tag) ?? -1),
      fresh: new Freshness(maxAgeMs),
      lastHeardAt: now,
      pingQueuedAt: -Infinity,
      myPings: new Map(),
      outOrder: 0,
      inNext: 1,
      held: new Map(),
      newest: new Map(),
      outgoing: new Set(),
      keyed: new Map(),
    };
    remember(candidates, tag, peer, 32);
    return peer;
  }

  // ---- receiving ----------------------------------------------------------------------------

  async function onHello(packet: Packet) {
    const { header, body } = packet;
    if (header.recipient || body.length !== PUBLIC_KEY_BYTES + 1 + NONCE_BYTES) {
      dropped.malformed++;
      return;
    }
    const publicKey = body.subarray(0, PUBLIC_KEY_BYTES) as Bytes;
    const theirRole = body[PUBLIC_KEY_BYTES];
    const echo = { nonce: body.subarray(PUBLIC_KEY_BYTES + 1) as Bytes, at: clock() };
    // A beacon is readable by everyone with the invitation, so it proves nothing by itself. It
    // only makes us offer a handshake; the peer becomes usable when it echoes our ping back.
    const known = peers.get(header.sender);
    const peer =
      known && equal(known.epoch, header.senderEpoch)
        ? known
        : await candidate(header.sender, header.senderEpoch, publicKey, theirRole);
    if (peer && clock() - peer.pingQueuedAt >= 1000) sendPing(peer, echo);
  }

  async function onDirected(packet: Packet) {
    const { header, headerBytes } = packet;
    let body = packet.body;
    let peer = peers.get(header.sender);
    if (!peer || !equal(peer.epoch, header.senderEpoch))
      peer = candidates.get(`${header.sender}/${toHex(header.senderEpoch)}`);
    if (header.type === PING) {
      if (body.length < PUBLIC_KEY_BYTES + 1 + SEAL_OVERHEAD) {
        dropped.malformed++;
        return;
      }
      const publicKey = body.subarray(0, PUBLIC_KEY_BYTES) as Bytes;
      peer ??= await candidate(
        header.sender,
        header.senderEpoch,
        publicKey,
        body[PUBLIC_KEY_BYTES],
      );
      body = body.subarray(PUBLIC_KEY_BYTES + 1) as Bytes;
    }
    if (!peer || peer.gone) {
      dropped.unknownPeer++;
      return;
    }
    const inner = await openInner(peer.pairKey, headerBytes, body);
    if (!inner) {
      dropped.unauthentic++;
      return;
    }
    if (peer.gone) return;
    if (!peer.window.accept(header.seq)) {
      dropped.replay++;
      return;
    }
    let live: Liveness;
    const reader = new Reader(inner);
    try {
      live = readLiveness(reader);
    } catch {
      dropped.malformed++;
      return;
    }
    const now = clock();
    let madeAfter: number | undefined;
    if (live.echo) {
      const nonce = toHex(live.echo);
      const sentAt = peer.myPings.get(nonce) ?? broadcastPings.get(nonce);
      if (sentAt !== undefined && now >= sentAt && now - sentAt <= maxAgeMs) {
        madeAfter = sentAt;
        peer.rttMs = Math.max(0, now - sentAt - (live.holdMs ?? 0));
      }
    }
    if (!peer.anchored && madeAfter === undefined) {
      // Not proven live yet: ask it to echo a ping, and act on nothing it says until it does.
      if (live.ping) peer.theirPing = { nonce: live.ping, at: now };
      if (header.type !== PING) dropped.unknownPeer++;
      if (now - peer.pingQueuedAt >= 1000) sendPing(peer);
      return;
    }
    if (!peer.fresh.accept(header.timestamp, now, madeAfter)) {
      dropped.stale++;
      return;
    }
    if (live.ping) peer.theirPing = { nonce: live.ping, at: now };
    if (!peer.anchored) anchor(peer);
    peer.lastHeardAt = now;
    // The peer has not verified us yet: answer at once instead of at the next keep-alive.
    if (live.want && now - peer.pingQueuedAt >= 250) sendPing(peer);
    if (header.type === BYE) lose(peer);
    else if (header.type === DATA) onData(peer, reader);
  }

  function onData(peer: Peer, reader: Reader) {
    let chunk;
    try {
      chunk = decodeChunk(reader);
    } catch {
      dropped.malformed++;
      return;
    }
    if (chunk.key) {
      const newest = peer.newest.get(chunk.key) ?? 0;
      if (chunk.messageId < newest) {
        dropped.superseded++;
        return;
      }
      if (chunk.messageId > newest) {
        remember(peer.newest, chunk.key, chunk.messageId, 64);
        reassembler.dropOlder(peer.tag, chunk.key, chunk.messageId);
      }
    }
    const bytes = reassembler.add(peer.tag, chunk, clock());
    if (!bytes) return;
    let value: JsonValue | Uint8Array;
    try {
      value = chunk.binary ? bytes : (JSON.parse(fromUtf8(bytes)) as JsonValue);
    } catch {
      dropped.malformed++;
      return;
    }
    if (!chunk.order || !orderWaitMs) {
      emit(messageListeners, peer.id, value);
      return;
    }
    if (chunk.order < peer.inNext) {
      dropped.late++;
      return;
    }
    peer.held.set(chunk.order, value);
    flush(peer);
  }

  /** Hands over held control messages in order; waits a short while for a missing one. */
  function flush(peer: Peer) {
    for (;;) {
      const value = peer.held.get(peer.inNext);
      if (value === undefined) break;
      peer.held.delete(peer.inNext++);
      emit(messageListeners, peer.id, value);
      if (peer.gone) return;
    }
    if (!peer.held.size) {
      if (peer.gapTimer) clearTimeout(peer.gapTimer);
      peer.gapTimer = undefined;
    } else if (peer.held.size > 64) skip(peer);
    else
      peer.gapTimer ??= setTimeout(() => {
        peer.gapTimer = undefined;
        if (stopped || peer.gone) return;
        // A large earlier message that is still arriving chunk by chunk is not missing.
        const progress = reassembler.progress(peer.tag, peer.inNext);
        if (progress !== undefined && clock() - progress < orderWaitMs) flush(peer);
        else skip(peer);
      }, orderWaitMs);
  }

  function skip(peer: Peer) {
    if (!peer.held.size) return;
    peer.inNext = Math.min(...peer.held.keys());
    flush(peer);
  }

  async function receive(content: string) {
    if (stopped || !keys || !local) return;
    const packet = await openPacket(keys.roomKey, content);
    if (stopped) return;
    if (!packet) {
      dropped.unauthentic++;
      return;
    }
    const { header } = packet;
    // Our own beacons come back from every relay.
    if (header.sender === selfId) return;
    if (header.type === HELLO) return onHello(packet);
    if (header.recipient !== selfId || !equal(header.recipientEpoch, local.epoch)) {
      dropped.recipient++;
      return;
    }
    if (header.type === PING || header.type === DATA || header.type === BYE)
      return onDirected(packet);
    dropped.malformed++;
  }

  // ---- lifecycle ----------------------------------------------------------------------------

  function tick() {
    if (stopped) return;
    const now = clock();
    if (announcing && !peers.size && now - helloAt >= seekMs) {
      // Keep looking, but ever less often: an idle link should cost relays next to nothing.
      seekMs = Math.min(10_000, seekMs * 1.5);
      hello();
    }
    for (const peer of [...peers.values()])
      if (now - peer.lastHeardAt > peerTimeoutMs) lose(peer);
      else if (now - peer.pingQueuedAt >= helloIntervalMs) sendPing(peer);
    for (const [tag, peer] of candidates) if (now - peer.createdAt > 30_000) candidates.delete(tag);
    for (const [nonce, at] of broadcastPings) if (now - at > maxAgeMs) broadcastPings.delete(nonce);
    reassembler.expire(now);
  }

  function start() {
    starting ??= (async () => {
      keys = await deriveRoomKeys(options.room, options.secret);
      local = await createLocalKey();
      inbox = await inboxTopic(keys, local.publicKey);
      if (stopped) return;
      pool = new RelayPool({
        relays: options.relays ?? DEFAULT_RELAYS,
        kind: keys.kind,
        topics: [keys.roomTopic, inbox],
        probeTopic: inbox,
        // One packet at a time: handling is asynchronous (decryption), and peer records must not
        // be changed by two packets at once.
        onEvent: (content) => {
          queue = queue.then(() => receive(content)).catch(() => undefined);
        },
        onChange: () => {
          if (helloWaiting) hello();
          if (state() !== lastState || statusListeners.size) notify();
        },
        webSocketFactory: options.webSocketFactory,
        now: wall,
        random,
        tuning: { minReady: minRelays, ...options.tuning },
        debug: options.debug,
      });
      pool.start();
      beat = setInterval(tick, 1000);
      notify();
      await pool.settled;
      settled = true;
      if (!stopped) notify();
    })();
    return starting;
  }

  function stop() {
    stopping ??= (async () => {
      const leaving = [...peers.values()];
      stopped = true;
      if (beat) clearInterval(beat);
      for (const peer of leaving) {
        if (peer.gapTimer) clearTimeout(peer.gapTimer);
        peer.gapTimer = undefined;
        for (const transfer of [...peer.outgoing]) transfer.cancel();
      }
      try {
        // Best effort: lets peers show "left" at once instead of after the silence timeout.
        const live = pool;
        if (live && local)
          await Promise.all(
            leaving.map(async (peer) => live.publishNow(peer.inbox, await sealDirected(peer, BYE))),
          );
      } catch {
        // Leaving anyway.
      }
      pool?.stop();
      peers.clear();
      candidates.clear();
      notify();
    })();
    return stopping;
  }

  const listen =
    <L>(listeners: Set<L>) =>
    (listener: L) => {
      listeners.add(listener);
      return () => void listeners.delete(listener);
    };

  return {
    start,
    stop,
    send,
    broadcastHello,
    onMessage: listen(messageListeners),
    onPeer: listen(peerListeners),
    onStatus: listen(statusListeners),
    status,
  };
}
