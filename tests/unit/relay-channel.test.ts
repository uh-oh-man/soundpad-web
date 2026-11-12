// The pure parts of the relay transport: key derivation, packet sealing, replay and staleness
// rules, chunking. No sockets and no timers.
import { describe, expect, it } from 'vitest';
import {
  Reader,
  Writer,
  equal,
  fromBase64,
  toBase64,
  utf8,
} from '../../web/src/network/relay/bytes';
import type { Bytes } from '../../web/src/network/relay/bytes';
import {
  DATA,
  Freshness,
  ReplayWindow,
  createLocalKey,
  derivePairKey,
  deriveRoomKeys,
  encodeHeader,
  inboxTopic,
  openInner,
  openPacket,
  readLiveness,
  sealInner,
  sealPacket,
  writeLiveness,
} from '../../web/src/network/relay/channel';
import { steadyClock } from '../../web/src/network/relay/clock';
import { Reassembler, decodeChunk, encodeChunk } from '../../web/src/network/relay/transfer';
import type { Chunk } from '../../web/src/network/relay/transfer';

const room = 'r'.repeat(32);
const secret = 's'.repeat(43);
const random = (n: number) => crypto.getRandomValues(new Uint8Array(n));
const header = (over: Partial<Parameters<typeof encodeHeader>[0]> = {}) =>
  encodeHeader({
    type: DATA,
    sender: 'alice',
    senderEpoch: new Uint8Array(8).fill(1),
    recipient: 'bob',
    recipientEpoch: new Uint8Array(8).fill(2),
    seq: 7,
    timestamp: 1_700_000_000_123,
    ...over,
  });

describe('relay keys', () => {
  it('derives the same keys from the same invitation and unrelated ones otherwise', async () => {
    const a = await deriveRoomKeys(room, secret);
    const b = await deriveRoomKeys(room, secret);
    const otherSecret = await deriveRoomKeys(room, 'x'.repeat(43));
    const otherRoom = await deriveRoomKeys('q'.repeat(32), secret);
    expect(a.roomTopic).toBe(b.roomTopic);
    expect(a.kind).toBe(b.kind);
    expect(a.roomTopic).toMatch(/^[0-9a-f]{64}$/);
    expect(a.kind).toBeGreaterThanOrEqual(20000);
    expect(a.kind).toBeLessThan(30000);
    // The relay topic gives away neither the Trystero room id nor the secret.
    expect(a.roomTopic).not.toContain(room);
    expect(new Set([a.roomTopic, otherSecret.roomTopic, otherRoom.roomTopic]).size).toBe(3);

    const sealed = await sealPacket(a.roomKey, header(), utf8('hello'), random);
    expect((await openPacket(b.roomKey, sealed))?.header.sender).toBe('alice');
    expect(await openPacket(otherSecret.roomKey, sealed)).toBeUndefined();
    expect(await openPacket(otherRoom.roomKey, sealed)).toBeUndefined();
  });

  it('gives every peer its own inbox topic that only invitation holders can compute', async () => {
    const keys = await deriveRoomKeys(room, secret);
    const other = await deriveRoomKeys(room, 'x'.repeat(43));
    const alice = await createLocalKey();
    const bob = await createLocalKey();
    expect(await inboxTopic(keys, alice.publicKey)).toBe(await inboxTopic(keys, alice.publicKey));
    expect(await inboxTopic(keys, alice.publicKey)).not.toBe(await inboxTopic(keys, bob.publicKey));
    expect(await inboxTopic(keys, alice.publicKey)).not.toBe(
      await inboxTopic(other, alice.publicKey),
    );
    expect(alice.publicKey).toHaveLength(65);
    expect(alice.epoch).toHaveLength(8);
    expect(equal(alice.epoch, bob.epoch)).toBe(false);
  });
});

describe('relay packets', () => {
  it('round-trips a packet and hides it completely', async () => {
    const keys = await deriveRoomKeys(room, secret);
    const body = utf8('PLAINTEXT BODY');
    const first = await sealPacket(keys.roomKey, header(), body, random);
    const second = await sealPacket(keys.roomKey, header(), body, random);
    // A fresh nonce every time: the same packet never looks the same twice.
    expect(first).not.toBe(second);
    const raw = String.fromCharCode(...fromBase64(first)!);
    for (const word of ['PLAINTEXT', 'alice', 'bob']) expect(raw).not.toContain(word);
    const packet = (await openPacket(keys.roomKey, first))!;
    expect(packet.header).toMatchObject({
      type: DATA,
      sender: 'alice',
      recipient: 'bob',
      seq: 7,
      timestamp: 1_700_000_000_123,
    });
    expect(equal(packet.headerBytes, header())).toBe(true);
    expect(equal(packet.body, body)).toBe(true);
  });

  it('rejects altered, truncated and foreign content', async () => {
    const keys = await deriveRoomKeys(room, secret);
    const sealed = fromBase64(await sealPacket(keys.roomKey, header(), utf8('x'), random))!;
    for (let at = 0; at < sealed.length; at += 7) {
      const changed = sealed.slice();
      changed[at] ^= 1;
      expect(await openPacket(keys.roomKey, toBase64(changed))).toBeUndefined();
    }
    expect(await openPacket(keys.roomKey, toBase64(sealed.slice(0, -1)))).toBeUndefined();
    expect(await openPacket(keys.roomKey, toBase64(sealed.slice(0, 20)))).toBeUndefined();
    expect(await openPacket(keys.roomKey, '')).toBeUndefined();
    expect(await openPacket(keys.roomKey, 'not base64 !!')).toBeUndefined();
    expect(await openPacket(keys.roomKey, toBase64(random(200)))).toBeUndefined();
  });

  it('seals directed packets so that only the two peers can open or make them', async () => {
    const keys = await deriveRoomKeys(room, secret);
    const [alice, bob, carol] = await Promise.all([
      createLocalKey(),
      createLocalKey(),
      createLocalKey(),
    ]);
    const ab = (await derivePairKey(keys, alice, 'alice', 'bob', bob.publicKey))!;
    const ba = (await derivePairKey(keys, bob, 'bob', 'alice', alice.publicKey))!;
    const sealed = await sealInner(ab, header(), utf8('for bob'), random);
    expect(equal((await openInner(ba, header(), sealed))!, utf8('for bob'))).toBe(true);

    // Carol holds the same invitation, and even uses Alice's or Bob's peer id: still no.
    const asCarol = (await derivePairKey(keys, carol, 'carol', 'bob', bob.publicKey))!;
    const asAlice = (await derivePairKey(keys, carol, 'alice', 'bob', bob.publicKey))!;
    expect(await openInner(asCarol, header(), sealed)).toBeUndefined();
    expect(await openInner(asAlice, header(), sealed)).toBeUndefined();
    const forged = await sealInner(asAlice, header(), utf8('from alice, honest'), random);
    expect(await openInner(ba, header(), forged)).toBeUndefined();

    // The header is part of the seal: an old message cannot be given a new sequence number or time.
    expect(await openInner(ba, header({ seq: 8 }), sealed)).toBeUndefined();
    expect(await openInner(ba, header({ timestamp: 1_700_000_999_000 }), sealed)).toBeUndefined();
    expect(await openInner(ba, header({ recipient: 'carol' }), sealed)).toBeUndefined();

    // Someone with a different invitation cannot pair at all, even knowing both public keys.
    const elsewhere = await deriveRoomKeys(room, 'x'.repeat(43));
    const wrongRoom = (await derivePairKey(elsewhere, bob, 'bob', 'alice', alice.publicKey))!;
    expect(await openInner(wrongRoom, header(), sealed)).toBeUndefined();
    expect(await derivePairKey(keys, alice, 'alice', 'bob', random(65) as Bytes)).toBeUndefined();
  });

  it('encodes the liveness prefix', () => {
    const ping = random(8) as Bytes;
    const echo = random(8) as Bytes;
    const bytes = writeLiveness(new Writer(), { want: true, ping, echo, holdMs: 1234 }).done();
    const live = readLiveness(new Reader(bytes));
    expect(live.want).toBe(true);
    expect(equal(live.ping!, ping)).toBe(true);
    expect(equal(live.echo!, echo)).toBe(true);
    expect(live.holdMs).toBe(1234);
    expect(readLiveness(new Reader(writeLiveness(new Writer(), { want: false }).done()))).toEqual({
      want: false,
    });
    expect(() => readLiveness(new Reader(bytes.slice(0, 5)))).toThrow(RangeError);
  });
});

describe('replay window', () => {
  it('accepts each sequence number once, in any order, within the window', () => {
    const window = new ReplayWindow(-1, 100);
    expect([0, 1, 5, 3, 2, 4].map((n) => window.accept(n))).toEqual(Array(6).fill(true));
    expect([0, 3, 5].map((n) => window.accept(n))).toEqual([false, false, false]);
    expect(window.accept(500)).toBe(true);
    expect(window.top).toBe(500);
    // Far behind the newest number: refused even though never seen.
    expect(window.accept(399)).toBe(false);
    expect(window.accept(400)).toBe(false);
    expect(window.accept(401)).toBe(true);
    expect(window.accept(401)).toBe(false);
  });

  it('starts above a floor carried over from an earlier connection', () => {
    const window = new ReplayWindow(41);
    expect(window.accept(41)).toBe(false);
    expect(window.accept(7)).toBe(false);
    expect(window.accept(42)).toBe(true);
  });

  it('stays small over a long session', () => {
    const window = new ReplayWindow(-1, 64);
    for (let n = 0; n < 100_000; n++) expect(window.accept(n)).toBe(true);
    expect((window as unknown as { seen: Set<number> }).seen.size).toBeLessThanOrEqual(129);
    expect(window.accept(99_999)).toBe(false);
  });
});

describe('freshness', () => {
  it('judges age against the sender, not against the wall clock', () => {
    const fresh = new Freshness(15_000);
    // The sender's clock is two hours behind ours. Unknown sender: nothing is trusted yet.
    const theirs = (ours: number) => ours - 7_200_000;
    expect(fresh.accept(theirs(1000), 1100)).toBe(false);
    // It echoes a ping we sent at 1000: made after that, so at most 150 ms old.
    expect(fresh.accept(theirs(1050), 1150, 1000)).toBe(true);
    // Ordinary packets, 100 ms in transit.
    expect(fresh.accept(theirs(2000), 2100)).toBe(true);
    expect(fresh.accept(theirs(5000), 5080)).toBe(true);
    // Held back 14 s: still inside the limit. Held back 16 s: refused.
    expect(fresh.accept(theirs(6000), 20_000)).toBe(true);
    expect(fresh.accept(theirs(6000), 22_100)).toBe(false);
    // A replayed packet from long ago is refused however it is timed.
    expect(fresh.accept(theirs(2000), 60_000)).toBe(false);
  });

  it('does not let an old echo vouch for a delayed packet', () => {
    const fresh = new Freshness(15_000);
    expect(fresh.accept(500, 1100, 1000)).toBe(true);
    // Echoes the ping from 1000 but arrives 20 s later: the echo proves nothing useful any more.
    expect(fresh.accept(600, 21_000, 1000)).toBe(false);
  });

  it('recovers when either clock is changed', () => {
    const fresh = new Freshness(15_000);
    expect(fresh.accept(10_000, 10_100, 10_000)).toBe(true);
    // The sender's clock is set back an hour: its packets now look an hour old…
    expect(fresh.accept(20_000 - 3_600_000, 20_100)).toBe(false);
    // …until one echoes a recent ping of ours, which re-bases the comparison.
    expect(fresh.accept(21_000 - 3_600_000, 21_100, 20_900)).toBe(true);
    expect(fresh.accept(22_000 - 3_600_000, 22_100)).toBe(true);
    // The sender's clock is set forward again: accepted straight away.
    expect(fresh.accept(23_000, 23_100)).toBe(true);
    expect(fresh.accept(23_500, 39_900)).toBe(false);
  });
});

describe('chunks', () => {
  const chunk = (over: Partial<Chunk> = {}): Chunk => ({
    messageId: 3,
    order: 0,
    index: 0,
    count: 3,
    binary: true,
    key: '',
    total: 30,
    data: new Uint8Array(10).fill(over.index ?? 0),
    ...over,
  });
  const limits = {
    maxMessageBytes: 1000,
    maxPendingBytes: 2500,
    maxPendingPerOwner: 3,
    timeoutMs: 20_000,
  };

  it('round-trips the chunk header', () => {
    const original = chunk({ index: 2, key: 'state', order: 9, binary: false });
    const decoded = decodeChunk(new Reader(encodeChunk(original)));
    expect(decoded).toEqual(original);
    expect(() => decodeChunk(new Reader(encodeChunk(chunk({ index: 3 }))))).toThrow(RangeError);
    expect(() => decodeChunk(new Reader(encodeChunk(chunk({ messageId: 0 }))))).toThrow(RangeError);
    expect(() => decodeChunk(new Reader(encodeChunk(chunk()).slice(0, 9)))).toThrow(RangeError);
    expect(() => decodeChunk(new Reader(encodeChunk(chunk({ count: 1, total: 11 }))))).toThrow(
      RangeError,
    );
  });

  it('reassembles chunks arriving in any order, once', () => {
    const r = new Reassembler(limits);
    expect(r.add('a', chunk({ index: 2 }), 0)).toBeUndefined();
    expect(r.add('a', chunk({ index: 0 }), 0)).toBeUndefined();
    expect(r.add('a', chunk({ index: 0 }), 0)).toBeUndefined();
    // Another sender using the same message id does not get mixed in.
    expect(r.add('b', chunk({ index: 1 }), 0)).toBeUndefined();
    const whole = r.add('a', chunk({ index: 1 }), 0)!;
    expect([...whole]).toEqual([...Array(10).fill(0), ...Array(10).fill(1), ...Array(10).fill(2)]);
    expect(r.size).toBe(1);
  });

  it('drops inconsistent, oversized and stalled messages', () => {
    const r = new Reassembler(limits);
    expect(r.add('a', chunk({ total: 1001 }), 0)).toBeUndefined();
    expect(r.size).toBe(0);
    r.add('a', chunk(), 0);
    // A chunk that disagrees with the first one cancels the message.
    r.add('a', chunk({ index: 1, total: 31 }), 0);
    expect(r.size).toBe(0);
    // More data than announced.
    r.add('a', chunk({ total: 25 }), 0);
    r.add('a', chunk({ total: 25, index: 1 }), 0);
    expect(r.add('a', chunk({ total: 25, index: 2 }), 0)).toBeUndefined();
    expect(r.size).toBe(0);
    // Stalled.
    r.add('a', chunk(), 1000);
    r.expire(20_999);
    expect(r.size).toBe(1);
    expect(r.progress('a', 0)).toBe(1000);
    r.expire(21_001);
    expect(r.size).toBe(0);
  });

  it('keeps memory bounded by dropping the message that has waited longest', () => {
    const r = new Reassembler(limits);
    for (let id = 1; id <= 4; id++) r.add('a', chunk({ messageId: id }), id);
    // Three per sender: message 1 gave way to message 4.
    expect(r.size).toBe(3);
    expect(r.add('a', chunk({ messageId: 1, index: 1 }), 5)).toBeUndefined();
    expect(r.size).toBe(3);
    // 2500 bytes in total across senders: two 1000-byte messages fit, the third evicts the oldest.
    const big = (owner: string, at: number) =>
      r.add(owner, chunk({ messageId: 9, total: 1000, count: 100 }), at);
    big('b', 10);
    big('c', 11);
    big('d', 12);
    const owners = new Set(
      [...(r as unknown as { pending: Map<string, unknown> }).pending.keys()].map((k) => k[0]),
    );
    expect(owners.has('d')).toBe(true);
    expect(owners.has('a')).toBe(false);

    r.dropOwner('d');
    r.add('c', chunk({ messageId: 20, key: 'state' }), 13);
    r.add('c', chunk({ messageId: 21, key: 'state' }), 13);
    r.dropOlder('c', 'state', 21);
    expect(r.progress('c', 0)).toBeDefined();
    r.dropOwner('c');
    r.dropOwner('b');
    expect(r.size).toBe(0);
  });
});

describe('relay helpers', () => {
  it('packs and unpacks numbers and byte fields', () => {
    const bytes = new Writer()
      .u8(200)
      .u16(65535)
      .u32(4_000_000_000)
      .u64(Number.MAX_SAFE_INTEGER)
      .u64(1_700_000_000_123)
      .short(utf8('héllo'))
      .bytes(Uint8Array.of(1, 2, 3))
      .done();
    const r = new Reader(bytes);
    expect([r.u8(), r.u16(), r.u32(), r.u64(), r.u64()]).toEqual([
      200,
      65535,
      4_000_000_000,
      Number.MAX_SAFE_INTEGER,
      1_700_000_000_123,
    ]);
    expect(new TextDecoder().decode(r.short())).toBe('héllo');
    expect([...r.rest()]).toEqual([1, 2, 3]);
    expect(() => r.u8()).toThrow(RangeError);
    expect(() => new Writer().short(new Uint8Array(256))).toThrow(RangeError);
  });

  it('encodes large buffers as base64 without overflowing the stack', () => {
    const big = new Uint8Array(1_000_000).map((_, i) => i % 251);
    expect(equal(fromBase64(toBase64(big))!, big)).toBe(true);
    expect(fromBase64('@@@')).toBeUndefined();
  });

  it('has an interval clock that never runs backwards', () => {
    let wall = 1_000_000;
    const clock = steadyClock(() => wall);
    expect(clock()).toBe(0);
    wall += 500;
    expect(clock()).toBe(500);
    wall -= 3_600_000;
    expect(clock()).toBe(500);
    wall += 250;
    expect(clock()).toBe(750);
  });
});
