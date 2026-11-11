/**
 * The secure channel carried inside relay events.
 *
 * Two layers, both AES-256-GCM with a random 12-byte nonce:
 *
 * - **Room layer.** Keys come only from the invitation (room id + secret) through HKDF. It wraps
 *   every packet, header included, so a relay sees ciphertext, sizes and timing and nothing else.
 *   The relay topic is derived the same way and cannot be linked to the Trystero room topic.
 * - **Pair layer.** Each running link has a throwaway ECDH P-256 key. Packets addressed to one peer
 *   are sealed a second time with a key both ends derive from that exchange, with the packet header
 *   as associated data. So another holder of the same invitation can neither read a directed
 *   message nor send one under someone else's peer id, and cannot re-wrap an old message in a new
 *   header.
 *
 * Replay and staleness are judged per sender by `ReplayWindow` and `Freshness` below.
 */
import { Reader, Writer, concat, fromBase64, toBase64, toBase64Url, toHex, utf8 } from './bytes';
import type { Bytes } from './bytes';

export const PROTOCOL_VERSION = 1;
const LABEL = 'soundpad-relay-v1';

/** Packet types. */
export const HELLO = 1; // broadcast presence beacon (room layer only)
export const PING = 2; // directed handshake and keep-alive
export const DATA = 3; // one chunk of an app message
export const BYE = 4; // the sender is leaving

export const EPOCH_BYTES = 8;
export const NONCE_BYTES = 8;
export const PUBLIC_KEY_BYTES = 65;
/** Bytes AES-GCM adds to a plaintext here: 12-byte nonce + 16-byte tag. */
export const SEAL_OVERHEAD = 28;

export type RoomKeys = {
  /** Ephemeral event kind for this room, 20000–29999. */
  kind: number;
  /** Tag value for broadcast beacons. */
  roomTopic: string;
  roomKey: CryptoKey;
  inboxKey: CryptoKey;
  pairSalt: Bytes;
};

/** Derives everything the relay path needs from the invitation, and nothing the Trystero path uses. */
export async function deriveRoomKeys(room: string, secret: string): Promise<RoomKeys> {
  const material = await crypto.subtle.importKey('raw', utf8(secret), 'HKDF', false, [
    'deriveBits',
  ]);
  const salt = utf8(`${LABEL} ${room}`);
  const derive = async (info: string, bytes: number) =>
    new Uint8Array(
      await crypto.subtle.deriveBits(
        { name: 'HKDF', hash: 'SHA-256', salt, info: utf8(info) },
        material,
        bytes * 8,
      ),
    );
  const [topic, kind, room256, inbox, pairSalt] = await Promise.all([
    derive('topic', 32),
    derive('kind', 2),
    derive('room key', 32),
    derive('inbox key', 32),
    derive('pair salt', 32),
  ]);
  const derivedKind = 20000 + (((kind[0] << 8) | kind[1]) % 10000);
  return {
    // 22242 is the one ephemeral kind relays treat specially (NIP-42 login events are never passed
    // on), so a room that lands on it moves one up.
    kind: derivedKind === 22242 ? 22243 : derivedKind,
    roomTopic: toHex(topic),
    roomKey: await crypto.subtle.importKey('raw', room256, 'AES-GCM', false, [
      'encrypt',
      'decrypt',
    ]),
    inboxKey: await crypto.subtle.importKey(
      'raw',
      inbox,
      { name: 'HMAC', hash: 'SHA-256' },
      false,
      ['sign'],
    ),
    pairSalt,
  };
}

/**
 * The tag value a peer listens on for packets addressed to it. Relays route by it, so a peer is
 * never sent (and never has to decrypt) traffic meant for someone else. Without the invitation it
 * is an opaque random-looking string.
 */
export async function inboxTopic(keys: RoomKeys, publicKey: Bytes) {
  return toHex(new Uint8Array(await crypto.subtle.sign('HMAC', keys.inboxKey, publicKey)));
}

export type LocalKey = {
  privateKey: CryptoKey;
  /** Uncompressed P-256 point, 65 bytes. */
  publicKey: Bytes;
  /** First 8 bytes of SHA-256(publicKey): names this running instance in packet headers. */
  epoch: Bytes;
  /** Short printable fingerprint of the public key, for channel binding by the session layer. */
  keyId: string;
};

/** The epoch and printable fingerprint that belong to a public key. */
export async function describeKey(publicKey: Bytes) {
  const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', publicKey));
  return { epoch: digest.slice(0, EPOCH_BYTES), keyId: toBase64Url(digest.slice(0, 16)) };
}

/** A fresh ECDH key for one run of a link. Always from WebCrypto; the private half never leaves it. */
export async function createLocalKey(): Promise<LocalKey> {
  const pair = await crypto.subtle.generateKey({ name: 'ECDH', namedCurve: 'P-256' }, false, [
    'deriveBits',
  ]);
  const publicKey = new Uint8Array(await crypto.subtle.exportKey('raw', pair.publicKey));
  return { privateKey: pair.privateKey, publicKey, ...(await describeKey(publicKey)) };
}

/**
 * The AES key two peers share. Bound to the invitation (salt), both peer ids and both public keys,
 * so it changes whenever either side restarts. Returns `undefined` for a malformed public key.
 */
export async function derivePairKey(
  keys: RoomKeys,
  local: LocalKey,
  selfId: string,
  peerId: string,
  peerPublicKey: Bytes,
): Promise<CryptoKey | undefined> {
  try {
    const theirs = await crypto.subtle.importKey(
      'raw',
      peerPublicKey,
      { name: 'ECDH', namedCurve: 'P-256' },
      false,
      [],
    );
    const shared = await crypto.subtle.deriveBits(
      { name: 'ECDH', public: theirs },
      local.privateKey,
      256,
    );
    const material = await crypto.subtle.importKey('raw', shared, 'HKDF', false, ['deriveKey']);
    // Both ends must build the same bytes, so order the two parties the same way on each side.
    const mine = [selfId, local.publicKey] as const;
    const other = [peerId, peerPublicKey] as const;
    const [first, second] = selfId < peerId ? [mine, other] : [other, mine];
    const info = new Writer()
      .bytes(utf8(`${LABEL} pair`))
      .short(utf8(first[0]))
      .bytes(first[1])
      .short(utf8(second[0]))
      .bytes(second[1])
      .done();
    return await crypto.subtle.deriveKey(
      { name: 'HKDF', hash: 'SHA-256', salt: keys.pairSalt, info },
      material,
      { name: 'AES-GCM', length: 256 },
      false,
      ['encrypt', 'decrypt'],
    );
  } catch {
    return undefined;
  }
}

export type Header = {
  type: number;
  sender: string;
  senderEpoch: Bytes;
  /** Empty for a broadcast. */
  recipient: string;
  recipientEpoch: Bytes;
  /** Strictly increasing per sender for the life of its link. */
  seq: number;
  /** Sender's clock, milliseconds. Only ever compared with that sender's other packets. */
  timestamp: number;
};

export function encodeHeader(h: Header): Bytes {
  return new Writer()
    .u8(PROTOCOL_VERSION)
    .u8(h.type)
    .short(utf8(h.sender))
    .bytes(h.senderEpoch)
    .short(utf8(h.recipient))
    .bytes(h.recipientEpoch)
    .u64(h.seq)
    .u64(h.timestamp)
    .done();
}

const decoder = new TextDecoder('utf-8', { fatal: true });
function decodeHeader(bytes: Bytes): Header {
  const r = new Reader(bytes);
  if (r.u8() !== PROTOCOL_VERSION) throw RangeError('Unknown protocol version');
  const type = r.u8();
  const sender = decoder.decode(r.short());
  const senderEpoch = r.bytes(EPOCH_BYTES);
  const recipient = decoder.decode(r.short());
  const recipientEpoch = r.bytes(EPOCH_BYTES);
  const header = {
    type,
    sender,
    senderEpoch,
    recipient,
    recipientEpoch,
    seq: r.u64(),
    timestamp: r.u64(),
  };
  if (!sender || r.left) throw RangeError('Malformed header');
  return header;
}

const outerData = utf8(`${LABEL} outer`);
const innerLabel = utf8(`${LABEL} inner`);
type Random = (length: number) => Bytes;

async function encrypt(key: CryptoKey, plain: Bytes, additionalData: Bytes, random: Random) {
  const iv = random(12);
  const sealed = await crypto.subtle.encrypt({ name: 'AES-GCM', iv, additionalData }, key, plain);
  return concat(iv, new Uint8Array(sealed));
}

async function decrypt(key: CryptoKey, sealed: Bytes, additionalData: Bytes) {
  if (sealed.length < SEAL_OVERHEAD) return undefined;
  try {
    const iv = sealed.subarray(0, 12);
    const data = sealed.subarray(12);
    return new Uint8Array(
      await crypto.subtle.decrypt({ name: 'AES-GCM', iv, additionalData }, key, data),
    );
  } catch {
    // Wrong key (another room) or altered bytes.
    return undefined;
  }
}

/** Wraps a header and body for the relay: returns the event content (base64 of nonce + ciphertext). */
export async function sealPacket(roomKey: CryptoKey, header: Bytes, body: Bytes, random: Random) {
  const plain = new Writer().u16(header.length).bytes(header).bytes(body).done();
  return toBase64(await encrypt(roomKey, plain, outerData, random));
}

export type Packet = { header: Header; headerBytes: Bytes; body: Bytes };

/** Opens an event's content. `undefined` when it was not made with this room's keys or is malformed. */
export async function openPacket(roomKey: CryptoKey, content: string): Promise<Packet | undefined> {
  const sealed = fromBase64(content);
  if (!sealed) return undefined;
  const plain = await decrypt(roomKey, sealed, outerData);
  if (!plain) return undefined;
  try {
    const r = new Reader(plain);
    const headerBytes = r.bytes(r.u16());
    return { header: decodeHeader(headerBytes), headerBytes, body: r.rest() };
  } catch {
    return undefined;
  }
}

/** Seals the private part of a directed packet. The header is authenticated with it. */
export const sealInner = (pairKey: CryptoKey, header: Bytes, plain: Bytes, random: Random) =>
  encrypt(pairKey, plain, concat(innerLabel, header), random);

export const openInner = (pairKey: CryptoKey, header: Bytes, sealed: Bytes) =>
  decrypt(pairKey, sealed, concat(innerLabel, header));

/**
 * The first bytes inside every directed packet. They let each side prove to the other that it is
 * alive now, without either trusting the other's wall clock.
 */
export type Liveness = {
  /** The sender has not yet verified us, and asks for an immediate answer. */
  want: boolean;
  /** A fresh random value the sender asks us to send back. */
  ping?: Bytes;
  /** The recipient's most recent ping, proving this packet was made after that ping was sent. */
  echo?: Bytes;
  /** How long the sender held that ping before echoing it, for a round-trip estimate. */
  holdMs?: number;
};

export function writeLiveness(w: Writer, l: Liveness) {
  w.u8((l.want ? 4 : 0) | (l.echo ? 2 : 0) | (l.ping ? 1 : 0));
  if (l.ping) w.bytes(l.ping);
  if (l.echo) w.bytes(l.echo).u32(Math.min(0xffffffff, Math.max(0, Math.round(l.holdMs ?? 0))));
  return w;
}

export function readLiveness(r: Reader): Liveness {
  const flags = r.u8();
  const live: Liveness = { want: (flags & 4) !== 0 };
  if (flags & 1) live.ping = r.bytes(NONCE_BYTES);
  if (flags & 2) {
    live.echo = r.bytes(NONCE_BYTES);
    live.holdMs = r.u32();
  }
  return live;
}

/**
 * Accepts each sequence number from one sender at most once. Relays reorder and duplicate, so this
 * is a sliding window, not "must be higher than the last": anything at or below `floor` is refused,
 * and the floor follows `size` behind the highest number seen.
 */
export class ReplayWindow {
  private seen = new Set<number>();
  private floor: number;
  private size: number;
  /** Highest sequence number accepted so far (or the starting floor). */
  top: number;
  constructor(floor = -1, size = 4096) {
    this.floor = floor;
    this.top = floor;
    this.size = size;
  }
  accept(seq: number) {
    if (seq <= this.floor || this.seen.has(seq)) return false;
    this.seen.add(seq);
    if (seq > this.top) {
      this.top = seq;
      this.floor = Math.max(this.floor, seq - this.size);
      if (this.seen.size > this.size * 2)
        for (const old of this.seen) if (old <= this.floor) this.seen.delete(old);
    }
    return true;
  }
}

/**
 * Decides whether a packet is too old, using only the receiver's clock.
 *
 * A sender's timestamps are compared with each other, never with our wall clock: `offset` is the
 * smallest "our receive time minus their timestamp" seen so far, which is their clock difference
 * plus the fastest delivery. A packet whose difference is more than `maxAgeMs` above that was held
 * back somewhere and is refused.
 *
 * A packet that echoes one of our recent pings is known to have been made after we sent that ping,
 * whatever the clocks say. Such a packet is accepted if the ping is recent enough, and it corrects
 * `offset` when a clock was changed on either side.
 */
export class Freshness {
  private offset?: number;
  private maxAgeMs: number;
  constructor(maxAgeMs: number) {
    this.maxAgeMs = maxAgeMs;
  }
  /**
   * @param timestamp the sender's timestamp in the packet
   * @param now our clock at arrival
   * @param madeAfter our clock when we sent the ping this packet echoes, if it echoes one
   */
  accept(timestamp: number, now: number, madeAfter?: number) {
    const sample = now - timestamp;
    if (madeAfter !== undefined && now - madeAfter <= this.maxAgeMs && now >= madeAfter) {
      // True offset is at least `madeAfter - timestamp`; a lower estimate means a clock moved.
      if (this.offset === undefined || this.offset < madeAfter - timestamp || sample < this.offset)
        this.offset = sample;
      return true;
    }
    if (this.offset === undefined) return false;
    if (sample < this.offset) this.offset = sample;
    return sample - this.offset <= this.maxAgeMs;
  }
}
