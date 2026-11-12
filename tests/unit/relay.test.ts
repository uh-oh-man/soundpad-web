// The relay link end to end, against in-memory relays (tests/unit/relay-hub.ts). No real network.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createRelayLink } from '../../web/src/network/relay';
import type {
  JsonValue,
  NostrEvent,
  RelayLink,
  RelayLinkOptions,
  WebSocketLike,
} from '../../web/src/network/relay';
import { equal } from '../../web/src/network/relay/bytes';
import { createSigner, signEvent } from '../../web/src/network/relay/nostr';
import { FakeHub, seeded, seededBytes } from './relay-hub';
import { advance, until, useFakeClock } from './relay-time';

const room = 'r'.repeat(32);
const secret = 's'.repeat(43);
const links: RelayLink[] = [];
let made = 0;
type Received = { from: string; value: JsonValue | Uint8Array };

function make(hub: FakeHub, selfId: string, extra: Partial<RelayLinkOptions> = {}) {
  const link = createRelayLink({
    room,
    secret,
    selfId,
    relays: hub.urls,
    webSocketFactory: hub.factory,
    random: seededBytes(1000 + made++),
    ...extra,
  });
  links.push(link);
  const got: Received[] = [];
  const peers: string[] = [];
  link.onMessage((from, value) => got.push({ from, value }));
  link.onPeer((id, event) => peers.push(`${event} ${id}`));
  /** `link.send`, with the simulated clock running until it resolves. */
  const send = (...args: Parameters<RelayLink['send']>) => done(link.send(...args));
  return { link, got, peers, send };
}

/**
 * Awaits a promise while moving the simulated clock. A send only resolves once relays have taken
 * every chunk, and pacing is driven by timers, so a bare `await` would wait for ever.
 */
async function done<T>(promise: Promise<T>) {
  let settled = false;
  void promise.finally(() => (settled = true)).catch(() => undefined);
  await until(() => settled);
  return promise;
}

/** Starts a host that listens and a guest that announces itself, and waits until both see each other. */
async function pair(
  hub: FakeHub,
  hostExtra: Partial<RelayLinkOptions> = {},
  guestExtra: Partial<RelayLinkOptions> = {},
) {
  const host = make(hub, 'host', { role: 'host', ...hostExtra });
  void host.link.start();
  // The host is up first, so on every relay its sockets are the first ones (see `inboxes`).
  await advance(50);
  const guest = make(hub, 'guest', { role: 'guest', ...guestExtra });
  void guest.link.start();
  guest.link.broadcastHello();
  await until(() => host.peers.includes('seen guest') && guest.peers.includes('seen host'));
  return { host, guest };
}

function goodHub(count = 3) {
  const hub = new FakeHub();
  for (let i = 0; i < count; i++) hub.add(`wss://relay${i}.test`);
  return hub;
}

function bytes(length: number, seed = 1) {
  const random = seeded(seed);
  return Uint8Array.from({ length }, () => Math.floor(random() * 256));
}

/** Byte-for-byte comparison. (`toEqual` walks a megabyte one element at a time and takes seconds.) */
const same = (value: JsonValue | Uint8Array, expected: Uint8Array) =>
  value instanceof Uint8Array && equal(value, expected);

/** The tag values each connected client subscribed to on the first relay, in connection order: [room topic, own inbox]. */
function subscriptions(hub: FakeHub) {
  const relay = [...hub.relays.values()][0];
  return [...relay.sockets].map((s) => [...s.subs.values()][0]['#x'] as string[]);
}

/** Wraps captured ciphertext in a brand-new event from another key, as someone replaying it would. */
async function rewrap(
  event: NostrEvent,
  content = event.content,
  tags = event.tags,
  kind = event.kind,
) {
  const signer = createSigner((n) => crypto.getRandomValues(new Uint8Array(n)));
  const created_at = Math.floor(Date.now() / 1000);
  return signEvent(signer, { kind, tags, content, created_at });
}

/** Events that appeared on the hub while `action` ran. */
async function capture(hub: FakeHub, action: () => Promise<unknown>) {
  const before = new Set(hub.events.map((e) => e.id));
  await action();
  return hub.events.filter((e) => !before.has(e.id));
}

// These tests simulate minutes of traffic and do real encryption and signing for every event.
vi.setConfig({ testTimeout: 60_000 });
beforeEach(() => {
  made = 0;
  useFakeClock();
});
afterEach(async () => {
  await Promise.all(links.splice(0).map((link) => link.stop()));
  vi.useRealTimers();
});

describe('relay link', () => {
  it('exchanges JSON and binary both ways through 3 relays with one dead and one silently dropping', async () => {
    const hub = new FakeHub();
    hub.add('wss://good.test');
    hub.add('wss://dead.test', { dead: true });
    hub.add('wss://hole.test', { drop: true });
    const { host, guest } = await pair(hub);

    expect(await guest.send('host', { op: 'play', n: 1 })).toBe('sent');
    expect(await host.send('guest', { type: 'ack', ok: true })).toBe('sent');
    const blob = bytes(3000);
    expect(await guest.send('host', blob)).toBe('sent');
    expect(await host.send('guest', blob.slice(0, 10))).toBe('sent');
    await until(() => host.got.length === 2 && guest.got.length === 2);

    expect(host.got[0]).toEqual({ from: 'guest', value: { op: 'play', n: 1 } });
    expect(host.got[1].value).toEqual(blob);
    expect(guest.got[0]).toEqual({ from: 'host', value: { type: 'ack', ok: true } });
    expect(guest.got[1].value).toEqual(blob.slice(0, 10));

    // Let the self-check of the relay that swallows events run out.
    await advance(7000);
    const status = guest.link.status();
    // Only one relay works, so the link says so instead of claiming full health.
    expect(status.state).toBe('degraded');
    const relay = (url: string) => status.relays.find((r) => r.url === url)!;
    expect(relay('wss://good.test').state).toBe('ready');
    expect(relay('wss://good.test').lastOkAt).toBeGreaterThan(0);
    expect(relay('wss://dead.test').error).toMatch(/Connection (error|closed)/);
    expect(relay('wss://hole.test').error).toMatch(/did not come back/);
    expect(relay('wss://hole.test').delivered).toBe(0);
    expect(status.peers.map((p) => p.id)).toEqual(['host']);
    expect(status.rttMs).toBeGreaterThanOrEqual(0);
  });

  it('delivers a message once when relays duplicate it', async () => {
    const hub = goodHub(2);
    hub.add('wss://twice.test', { duplicate: true });
    const { host, guest } = await pair(hub);
    for (let n = 0; n < 5; n++) await guest.send('host', { n });
    await advance(1000);
    expect(host.got.map((m) => m.value)).toEqual([0, 1, 2, 3, 4].map((n) => ({ n })));
    // All three relays carried every message: four copies of each reached the host.
    const received = host.link.status().relays.reduce((n, r) => n + r.received, 0);
    const delivered = host.link.status().relays.reduce((n, r) => n + r.delivered, 0);
    expect(received).toBeGreaterThanOrEqual(delivered + 15);
  });

  it('rejects replayed and tampered events', async () => {
    const hub = goodHub();
    const { host, guest } = await pair(hub);
    const sent = await capture(hub, async () => {
      await guest.send('host', { op: 'play' });
      await until(() => host.got.length === 1);
    });
    expect(sent.length).toBeGreaterThan(0);
    const relay = hub.relays.get('wss://relay0.test')!;

    // The very same event again: recognised by its id.
    for (const event of sent) relay.inject(event);
    await advance(200);
    expect(host.got).toHaveLength(1);
    expect(host.link.status().dropped.replay).toBe(0);

    // The same ciphertext inside a new event (new id): caught by the sequence number.
    for (const event of sent) relay.inject(await rewrap(event));
    await advance(200);
    expect(host.got).toHaveLength(1);
    expect(host.link.status().dropped.replay).toBe(sent.length);

    // One character of ciphertext changed: fails authentication.
    const before = host.link.status().dropped.unauthentic;
    for (const event of sent) {
      const flipped = event.content.replace(/.$/, (c) => (c === 'A' ? 'B' : 'A'));
      const at = Math.floor(event.content.length / 2);
      const middle = event.content.slice(0, at) + (event.content[at] === 'A' ? 'B' : 'A');
      relay.inject(await rewrap(event, middle + event.content.slice(at + 1)));
      relay.inject(await rewrap(event, flipped));
      relay.inject(await rewrap(event, 'not base64 at all!'));
    }
    await advance(200);
    expect(host.got).toHaveLength(1);
    expect(host.link.status().dropped.unauthentic).toBeGreaterThanOrEqual(before + 2 * sent.length);

    // The link is unharmed.
    await guest.send('host', { op: 'stop' });
    await until(() => host.got.length === 2);
    expect(host.got[1].value).toEqual({ op: 'stop' });
  });

  it('ignores packets for another recipient and packets from another room or secret', async () => {
    const hub = goodHub();
    const { host, guest } = await pair(hub);
    const other = make(hub, 'other', { role: 'guest' });
    void other.link.start();
    other.link.broadcastHello();
    await until(() => other.peers.includes('seen host'));
    const [hostTopics, , otherTopics] = subscriptions(hub);
    expect(hostTopics[0]).toBe(otherTopics[0]);
    expect(hostTopics[1]).not.toBe(otherTopics[1]);

    const sent = await capture(hub, async () => {
      await guest.send('host', { secret: 'for the host only' });
      await until(() => host.got.length === 1);
    });
    await advance(500);
    // Relays route by inbox, so the other guest was never even handed the message…
    expect(other.got).toHaveLength(0);
    // …and if a relay hands it over anyway, it is not addressed to them.
    const relay = hub.relays.get('wss://relay1.test')!;
    const forHost = sent.filter((e) => e.tags[0][1] === hostTopics[1]);
    for (const event of forHost)
      relay.inject(await rewrap(event, event.content, [['x', otherTopics[1]]]));
    await advance(200);
    expect(other.got).toHaveLength(0);
    // (Its own keep-alives to the host may be among the captured events; those it simply skips.)
    expect(other.link.status().dropped.recipient).toBeGreaterThanOrEqual(1);
    expect(other.link.status().dropped.recipient).toBeLessThanOrEqual(forHost.length);

    // A link with another secret lives on another topic and never meets this session.
    const stranger = make(hub, 'guest2', { secret: 'x'.repeat(43) });
    void stranger.link.start();
    stranger.link.broadcastHello();
    const theirs = await capture(hub, () => advance(3000));
    expect(stranger.peers).toEqual([]);
    expect(host.peers.filter((p) => p.includes('guest2'))).toEqual([]);
    // Even pushed onto this session's topic, its packets do not open with this session's keys.
    const before = host.link.status().dropped.unauthentic;
    const beacons = theirs.filter((e) => !hostTopics.includes(e.tags[0][1]));
    expect(beacons.length).toBeGreaterThan(0);
    for (const event of beacons)
      relay.inject(await rewrap(event, event.content, [['x', hostTopics[0]]], sent[0].kind));
    await advance(200);
    expect(host.link.status().dropped.unauthentic).toBe(before + beacons.length);
    expect(
      host.link
        .status()
        .peers.map((p) => p.id)
        .sort(),
    ).toEqual(['guest', 'other']);

    // Another room with the same secret is just as separate.
    const elsewhere = make(hub, 'guest3', { room: 'q'.repeat(32) });
    void elsewhere.link.start();
    elsewhere.link.broadcastHello();
    await advance(3000);
    expect(elsewhere.peers).toEqual([]);
  });

  it('shows relays nothing but ciphertext', async () => {
    const hub = goodHub();
    const { host, guest } = await pair(hub);
    await guest.send('host', { marker: 'PLAINTEXT-MARKER-12345' });
    await host.send('guest', new TextEncoder().encode('PLAINTEXT-MARKER-67890'));
    await until(() => host.got.length === 1 && guest.got.length === 1);
    const seen = JSON.stringify(hub.events);
    for (const word of ['PLAINTEXT', 'host', 'guest', room, secret])
      expect(seen).not.toContain(word);
    for (const event of hub.events) {
      expect(event.kind).toBeGreaterThanOrEqual(20000);
      expect(event.kind).toBeLessThan(30000);
      expect(event.tags).toHaveLength(1);
      expect(event.tags[0][1]).toMatch(/^[0-9a-f]{64}$/);
      expect(atob(event.content).includes('MARKER')).toBe(false);
    }
  });

  it('carries a 1 MiB payload intact in events under the size limit', async () => {
    const hub = new FakeHub();
    // strfry refuses events above 64 KiB.
    for (let i = 0; i < 3; i++) hub.add(`wss://relay${i}.test`, { maxEventBytes: 65536 });
    const { host, guest } = await pair(hub);
    const payload = bytes(1024 * 1024, 7);
    const data = await capture(hub, async () => {
      expect(await host.send('guest', payload)).toBe('sent');
      await until(() => guest.got.length === 1);
    });
    expect(guest.got[0].from).toBe('host');
    expect(same(guest.got[0].value, payload)).toBe(true);
    for (const relay of hub.relays.values()) expect(relay.refusals).toEqual([]);
    const sizes = hub.events.map((e) => JSON.stringify(e).length);
    expect(Math.max(...sizes)).toBeLessThanOrEqual(60_000);
    expect(Math.max(...hub.events.map((e) => atob(e.content).length))).toBeLessThanOrEqual(44_000);
    expect(data.length).toBeGreaterThanOrEqual(24);

    // A large JSON value takes the same path.
    const big = { rows: Array.from({ length: 4000 }, (_, i) => ({ id: i, name: `Sound ${i}` })) };
    expect(await guest.send('host', big)).toBe('sent');
    await until(() => host.got.length === 1);
    expect(host.got[0].value).toEqual(big);
  });

  it('lets control messages overtake a bulk transfer', async () => {
    const hub = goodHub();
    const { host, guest } = await pair(hub);
    const snapshot = bytes(3 * 1024 * 1024, 3);
    const bulk = host.link.send('guest', snapshot, { priority: 'bulk' });
    // Part-way through the snapshot…
    await advance(1000);
    expect(guest.got).toHaveLength(0);
    const started = Date.now();
    expect(await host.send('guest', { op: 'stop' })).toBe('sent');
    expect(await host.send('guest', { op: 'panic' })).toBe('sent');
    await until(() => guest.got.length === 2);
    // …the commands arrive at once, in order, and the snapshot is still on its way.
    expect(Date.now() - started).toBeLessThan(300);
    expect(guest.got.map((m) => m.value)).toEqual([{ op: 'stop' }, { op: 'panic' }]);
    expect(await done(bulk)).toBe('sent');
    await until(() => guest.got.length === 3);
    expect(same(guest.got[2].value, snapshot)).toBe(true);
  });

  // Real relays take large events slowly: in the 2026-11 live check a 40 KB event took 0.6 s through
  // one relay and 3.3 s through another. Whichever leg is slow, a command must not queue behind a
  // snapshot there.
  for (const leg of ['ingestBytesPerSecond', 'deliverBytesPerSecond'] as const)
    it(`gets a stop command through at once while a snapshot crawls through slow relays (${leg})`, async () => {
      const hub = new FakeHub();
      for (let i = 0; i < 3; i++) hub.add(`wss://relay${i}.test`, { [leg]: 100_000 });
      const { host, guest } = await pair(hub);
      const snapshot = bytes(600_000, 11);
      const bulk = host.link.send('guest', snapshot, { priority: 'bulk' });
      await advance(1500);
      expect(guest.got).toHaveLength(0);
      const started = Date.now();
      expect(await host.send('guest', { op: 'stop' })).toBe('sent');
      await until(() => guest.got.length === 1);
      expect(Date.now() - started).toBeLessThan(300);
      expect(guest.got[0].value).toEqual({ op: 'stop' });
      // The snapshot still arrives whole (about 8 s at this speed), and nothing was thrown away as too old.
      expect(await done(bulk)).toBe('sent');
      await until(() => guest.got.length === 2, 60_000);
      expect(same(guest.got[1].value, snapshot)).toBe(true);
      expect(guest.link.status().dropped.stale).toBe(0);
      expect(host.peers).toEqual(['seen guest']);
      expect(guest.peers).toEqual(['seen host']);
    });

  it('abandons a superseded bulk transfer', async () => {
    const hub = goodHub();
    const { host, guest } = await pair(hub);
    const chunks = async (action: () => Promise<unknown>) => (await capture(hub, action)).length;
    const old = host.link.send('guest', bytes(3 * 1024 * 1024, 5), { supersedeKey: 'state' });
    let newer: Promise<string> | undefined;
    const published = await chunks(async () => {
      await advance(500);
      newer = host.link.send('guest', { revision: 2 }, { priority: 'bulk', supersedeKey: 'state' });
      expect(await done(old)).toBe('superseded');
      expect(await done(newer!)).toBe('sent');
      await advance(10_000);
    });
    // A 3 MiB snapshot is about 70 events; most of them were never published.
    expect(published).toBeLessThan(45);
    expect(guest.got.map((m) => m.value)).toEqual([{ revision: 2 }]);
    // The half-received snapshot is forgotten too; a third message is unaffected.
    await host.send('guest', { revision: 3 }, { priority: 'bulk', supersedeKey: 'state' });
    await until(() => guest.got.length === 2);
    expect(guest.got[1].value).toEqual({ revision: 3 });
  });

  it('never delivers older state after newer state with the same key', async () => {
    let slow = false;
    const hub = new FakeHub();
    for (let i = 0; i < 2; i++)
      hub.add(`wss://relay${i}.test`, { delayMs: () => (slow ? 2000 : 1) });
    const { host, guest } = await pair(hub);
    slow = true;
    await host.send('guest', { revision: 1 }, { priority: 'bulk', supersedeKey: 'state' });
    await advance(30);
    slow = false;
    await host.send('guest', { revision: 2 }, { priority: 'bulk', supersedeKey: 'state' });
    await host.send('guest', { other: true }, { priority: 'bulk', supersedeKey: 'layout' });
    await advance(4000);
    expect(guest.got.map((m) => m.value)).toEqual([{ revision: 2 }, { other: true }]);
    expect(guest.link.status().dropped.superseded).toBe(1);
  });

  it('backs off rate-limited relays and keeps working', async () => {
    const hub = new FakeHub();
    hub.add('wss://good.test');
    const strict = [hub.add('wss://strict1.test'), hub.add('wss://strict2.test')];
    const { host, guest } = await pair(hub);
    for (const relay of strict) relay.behaviour.rateLimit = 2;
    const results = await done(
      Promise.all(Array.from({ length: 20 }, (_, n) => guest.link.send('host', { n }))),
    );
    expect(results.every((r) => r === 'sent')).toBe(true);
    await until(() => host.got.length === 20);
    expect(host.got.map((m) => (m.value as { n: number }).n)).toEqual([...Array(20).keys()]);

    const relay = (url: string) => guest.link.status().relays.find((r) => r.url === url)!;
    expect(relay('wss://strict1.test').state).toBe('backoff');
    expect(relay('wss://strict1.test').error).toMatch(/rate-limited/);
    expect(relay('wss://strict2.test').state).toBe('backoff');
    expect(guest.link.status().state).toBe('degraded');

    // While paused, the guest sends those relays nothing at all.
    const quiet = () => strict.map((r) => r.accepted.length + r.refusals.length);
    const before = quiet();
    for (const relay of strict) relay.behaviour.rateLimit = undefined;
    await advance(3000);
    for (let n = 20; n < 25; n++) await guest.send('host', { n });
    await until(() => host.got.length === 25);
    // Only the host's own keep-alives may have reached them in that time, not the guest's five messages.
    expect(quiet()[0] - before[0]).toBeLessThan(5);
    // After the pause they are used again.
    await advance(10_000);
    expect(relay('wss://strict1.test').state).toBe('ready');
    expect(guest.link.status().state).toBe('ready');
  });

  it('drops a packet a relay held back too long, whatever the two clocks say', async () => {
    let slow = false;
    const hub = new FakeHub();
    hub.add('wss://relay0.test', { delayMs: () => (slow ? 20_000 : 1) });
    // The two computers disagree about the time by twelve minutes. (The in-memory relay applies the
    // subscription's `since` to live events, as real relays do, so this also covers that filter.)
    const { host, guest } = await pair(
      hub,
      { now: () => Date.now() + 600_000 },
      { now: () => Date.now() - 120_000 },
    );
    await guest.send('host', { n: 1 });
    await until(() => host.got.length === 1);
    slow = true;
    await guest.send('host', { n: 2, late: true }, { priority: 'bulk' });
    await advance(30);
    slow = false;
    await guest.send('host', { n: 3 });
    await until(() => host.got.length === 2);
    await advance(25_000);
    expect(host.got.map((m) => m.value)).toEqual([{ n: 1 }, { n: 3 }]);
    expect(host.link.status().dropped.stale).toBe(1);
    // The link itself stayed up throughout.
    expect(host.peers).toEqual(['seen guest']);
    expect(guest.peers).toEqual(['seen host']);
  });

  it('keeps going when a wall clock is corrected backwards, and recovers from a jump forwards', async () => {
    const hub = goodHub();
    const skew = { host: 0, guest: 0 };
    const { host, guest } = await pair(
      hub,
      { now: () => Date.now() + skew.host },
      { now: () => Date.now() + skew.guest },
    );
    skew.guest = -30_000;
    await advance(40_000);
    expect(host.peers).toEqual(['seen guest']);
    expect(guest.peers).toEqual(['seen host']);
    await guest.send('host', { n: 1 });
    await until(() => host.got.length === 1);

    // A forward jump looks like a long silence: the peer is dropped, then found again by itself.
    skew.host = 60_000;
    await until(() => host.peers.join() === 'seen guest,lost guest,seen guest');
    await guest.send('host', { n: 2 });
    await host.send('guest', { n: 3 });
    await until(() => host.got.length === 2 && guest.got.length === 1);
  });

  it('delivers control messages in the order sent although relays reorder them', async () => {
    const random = seeded(42);
    const hub = new FakeHub();
    for (let i = 0; i < 3; i++)
      hub.add(`wss://relay${i}.test`, { delayMs: () => 1 + random() * 300 });
    const { host, guest } = await pair(hub);
    const sends = Array.from({ length: 40 }, (_, n) => guest.link.send('host', { n }));
    // A message of several chunks keeps its place in the order as well.
    sends.push(guest.link.send('host', bytes(100_000, 9), { priority: 'control' }));
    sends.push(guest.link.send('host', { n: 'last' }));
    expect((await done(Promise.all(sends))).every((r) => r === 'sent')).toBe(true);
    await until(() => host.got.length === 42);
    expect(host.got.slice(0, 40).map((m) => (m.value as { n: number }).n)).toEqual([
      ...Array(40).keys(),
    ]);
    expect(same(host.got[40].value, bytes(100_000, 9))).toBe(true);
    expect(host.got[41].value).toEqual({ n: 'last' });
    expect(host.link.status().dropped.late).toBe(0);
  });

  it('pairs guests with the host only', async () => {
    const hub = goodHub();
    const { host, guest } = await pair(hub);
    const second = make(hub, 'guest-b', { role: 'guest' });
    void second.link.start();
    second.link.broadcastHello();
    await until(() => second.peers.includes('seen host'));
    await advance(10_000);
    expect(
      host.link
        .status()
        .peers.map((p) => p.id)
        .sort(),
    ).toEqual(['guest', 'guest-b']);
    expect(guest.link.status().peers.map((p) => p.id)).toEqual(['host']);
    expect(second.link.status().peers.map((p) => p.id)).toEqual(['host']);
    expect(await second.send('guest', { hi: 1 })).toBe('unknown-peer');
    // The fingerprint each side reports for the other is the one that side reports for itself.
    expect(guest.link.status().peers[0].key).toBe(host.link.status().key);
    expect(host.link.status().peers.find((p) => p.id === 'guest')!.key).toBe(
      guest.link.status().key,
    );
  });

  it('does not let another invitation holder take over the id of a connected peer', async () => {
    const hub = goodHub();
    const { host, guest } = await pair(hub);
    const impostor = make(hub, 'guest', { role: 'guest' });
    void impostor.link.start();
    impostor.link.broadcastHello();
    await advance(30_000);
    expect(host.peers).toEqual(['seen guest']);
    expect(impostor.peers).toEqual([]);
    expect(await impostor.send('host', { op: 'play' })).toBe('unknown-peer');
    await guest.send('host', { real: true });
    await until(() => host.got.length === 1);
    expect(host.got).toEqual([{ from: 'guest', value: { real: true } }]);
    expect(host.link.status().peers[0].key).toBe(guest.link.status().key);
  });

  it('reports a silent peer lost and accepts its restart under the same id', async () => {
    const hub = goodHub();
    const sockets: WebSocketLike[] = [];
    const tracked = (url: string) => {
      const socket = hub.factory(url);
      sockets.push(socket);
      return socket;
    };
    const { host, guest } = await pair(hub, {}, { webSocketFactory: tracked });
    // The guest's browser dies without a goodbye: its sockets stay open but nothing moves.
    for (const socket of sockets) {
      socket.onmessage = null;
      socket.send = () => undefined;
    }
    const again = make(hub, 'guest', { role: 'guest' });
    void again.link.start();
    again.link.broadcastHello();
    await until(() => again.peers.includes('seen host') && host.peers.length === 3, 40_000);
    expect(host.peers).toEqual(['seen guest', 'lost guest', 'seen guest']);
    await again.send('host', { back: true });
    await until(() => host.got.length === 1);
    expect(host.link.status().peers[0].key).toBe(again.link.status().key);
    expect(host.link.status().peers[0].key).not.toBe(guest.link.status().key);
  });

  it('finds its peer again after every relay was unreachable for a while', async () => {
    const hub = goodHub();
    const { host, guest } = await pair(hub);
    for (const relay of hub.relays.values()) {
      relay.behaviour.dead = true;
      relay.disconnect();
    }
    await advance(1000);
    expect(guest.link.status().state).toBe('failed');
    expect(['failed', 'unknown-peer']).toContain(await guest.send('host', { n: 1 }));
    await advance(25_000);
    expect(host.peers).toEqual(['seen guest', 'lost guest']);
    expect(guest.peers).toEqual(['seen host', 'lost host']);
    expect(await guest.send('host', { n: 2 })).toBe('unknown-peer');

    for (const relay of hub.relays.values()) relay.behaviour.dead = false;
    await until(() => guest.peers.length === 3 && host.peers.length === 3, 120_000);
    await until(() => guest.link.status().state === 'ready', 120_000);
    expect(await guest.send('host', { n: 3 })).toBe('sent');
    await until(() => host.got.length === 1);
    expect(host.got[0].value).toEqual({ n: 3 });
  });

  it('refuses messages above the size limits', async () => {
    const hub = goodHub();
    const { host, guest } = await pair(
      hub,
      { maxReceiveBytes: 8192 },
      { maxPayloadBytes: 100_000 },
    );
    expect(await guest.send('host', bytes(100_001))).toBe('too-large');
    // The guest may send this, but the host accepts at most 8 KiB from a peer.
    expect(await guest.send('host', bytes(50_000))).toBe('sent');
    expect(await guest.send('host', bytes(8192))).toBe('sent');
    await until(() => host.got.length === 1);
    await advance(2000);
    expect(host.got).toHaveLength(1);
    expect((host.got[0].value as Uint8Array).length).toBe(8192);
  });

  it('stop() says goodbye, then closes every socket and timer', async () => {
    const hub = goodHub();
    const { host, guest } = await pair(hub);
    expect(hub.open).toBe(6);
    const unfinished = guest.link.send('host', bytes(2 * 1024 * 1024), { priority: 'bulk' });
    await advance(200);
    await guest.link.stop();
    expect(await done(unfinished)).toBe('stopped');
    expect(hub.open).toBe(3);
    expect(guest.link.status().state).toBe('stopped');
    expect(await guest.send('host', { n: 1 })).toBe('stopped');
    // The host learns it from the goodbye, long before the 20 s silence timeout.
    await advance(100);
    expect(host.peers).toEqual(['seen guest', 'lost guest']);

    await host.link.stop();
    expect(hub.open).toBe(0);
    // Let the in-memory relays finish what was already in flight; nothing of ours may remain.
    await advance(100);
    expect(vi.getTimerCount()).toBe(0);
    await advance(60_000);
    expect(hub.open).toBe(0);
    expect(vi.getTimerCount()).toBe(0);
    for (const relay of hub.relays.values()) expect(relay.connections).toBe(2);
  });

  it('can be stopped before it finished starting', async () => {
    const hub = goodHub();
    const { link } = make(hub, 'early');
    const starting = link.start();
    const stopping = link.stop();
    await advance(100);
    await Promise.all([starting, stopping]);
    expect(link.status().state).toBe('stopped');
    expect(hub.open).toBe(0);
    await advance(100);
    expect(vi.getTimerCount()).toBe(0);
  });
});
