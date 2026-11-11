// The Nostr client under the relay link: signing, the per-relay self-check, de-duplication,
// pacing, and how relay complaints are handled. In-memory relays only (tests/unit/relay-hub.ts).
import { schnorr } from '@noble/secp256k1';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { RelayPool, createSigner, hasValidId, signEvent } from '../../web/src/network/relay/nostr';
import type { PoolOptions, PoolTuning, RelayState } from '../../web/src/network/relay/nostr';
import { FakeHub, seededBytes } from './relay-hub';
import { advance, until, useFakeClock } from './relay-time';

const KIND = 21234;
const pools: RelayPool[] = [];
const hexBytes = (hex: string) => Uint8Array.from(hex.match(/../g)!, (b) => parseInt(b, 16));

/** A pool that listens on a shared topic and on its own inbox `name`. */
function make(
  hub: FakeHub,
  name: string,
  tuning: Partial<PoolTuning> = {},
  extra: Partial<PoolOptions> = {},
) {
  const got: { content: string; relay: string }[] = [];
  const pool = new RelayPool({
    relays: hub.urls,
    kind: KIND,
    topics: ['room', name],
    probeTopic: name,
    onEvent: (content, relay) => got.push({ content, relay }),
    webSocketFactory: hub.factory,
    random: seededBytes(500 + pools.length),
    tuning,
    ...extra,
  });
  pools.push(pool);
  pool.start();
  const state = (url: string): RelayState => pool.health().find((r) => r.url === url)!.state;
  const text = () => got.map((g) => g.content);
  return { pool, got, state, text };
}

function goodHub(count = 3) {
  const hub = new FakeHub();
  for (let i = 0; i < count; i++) hub.add(`wss://relay${i}.test`);
  return hub;
}

/** Awaits a promise while the simulated clock runs. */
async function done<T>(promise: Promise<T>) {
  let settled = false;
  void promise.finally(() => (settled = true)).catch(() => undefined);
  await until(() => settled);
  return promise;
}

vi.setConfig({ testTimeout: 60_000 });
beforeEach(() => useFakeClock());
afterEach(() => {
  for (const pool of pools.splice(0)) pool.stop();
  vi.useRealTimers();
});

describe('nostr events', () => {
  it('signs events with a valid id and BIP-340 signature', async () => {
    const signer = createSigner((n) => crypto.getRandomValues(new Uint8Array(n)));
    const event = await signEvent(signer, {
      kind: KIND,
      created_at: 1_800_000_000,
      tags: [['x', 'topic']],
      content: 'payload with "quotes" and \\ slashes',
    });
    expect(event.pubkey).toMatch(/^[0-9a-f]{64}$/);
    expect(event.id).toMatch(/^[0-9a-f]{64}$/);
    expect(event.sig).toMatch(/^[0-9a-f]{128}$/);
    expect(await hasValidId(event)).toBe(true);
    const verify = (sig: string, id: string, key: string) =>
      schnorr.verifyAsync(hexBytes(sig), hexBytes(id), hexBytes(key));
    expect(await verify(event.sig, event.id, event.pubkey)).toBe(true);
    expect(await hasValidId({ ...event, content: event.content + '!' })).toBe(false);
    expect(await hasValidId({ ...event, created_at: event.created_at + 1 })).toBe(false);
    const other = createSigner((n) => crypto.getRandomValues(new Uint8Array(n)));
    expect(await verify(event.sig, event.id, other.pubkey)).toBe(false);
    // The id is the SHA-256 of the NIP-01 serialization.
    const serialized = JSON.stringify([
      0,
      event.pubkey,
      event.created_at,
      KIND,
      event.tags,
      event.content,
    ]);
    const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(serialized));
    expect(Buffer.from(digest).toString('hex')).toBe(event.id);
  });
});

describe('relay pool', () => {
  it('delivers each event once although every relay carries it', async () => {
    const hub = goodHub(2);
    hub.add('wss://twice.test', { duplicate: true });
    const a = make(hub, 'a');
    const b = make(hub, 'b');
    await done(Promise.all([a.pool.settled, b.pool.settled]));
    await until(() => a.pool.readyCount() === 3 && b.pool.readyCount() === 3);
    for (const r of a.pool.health()) expect(r.latencyMs).toBeGreaterThan(0);

    expect(await done(a.pool.publish('b', async () => 'one').done)).toBe('sent');
    expect(await done(a.pool.publish('room', async () => 'two').done)).toBe('sent');
    await advance(100);
    expect(b.text()).toEqual(['one', 'two']);
    // The publisher does not hear its own events, and nothing addressed elsewhere.
    expect(a.text()).toEqual([]);
    for (const relay of hub.relays.values())
      expect(relay.accepted.filter((e) => ['one', 'two'].includes(e.content))).toHaveLength(2);
    const health = b.pool.health();
    expect(health.reduce((n, r) => n + r.received, 0)).toBe(8);
    expect(health.reduce((n, r) => n + r.delivered, 0)).toBe(2);
    expect(a.pool.health().every((r) => r.sent === 2 && r.accepted >= 2 && r.lastOkAt)).toBe(true);
  });

  it('is not fooled by a relay that sends junk under a real event id', async () => {
    const hub = goodHub(2);
    const b = make(hub, 'b');
    await until(() => b.pool.readyCount() === 2);
    const signer = createSigner((n) => crypto.getRandomValues(new Uint8Array(n)));
    const created_at = Math.floor(Date.now() / 1000);
    const real = await signEvent(signer, {
      kind: KIND,
      created_at,
      tags: [['x', 'b']],
      content: 'real',
    });
    hub.relays.get('wss://relay0.test')!.inject({ ...real, content: 'junk' });
    await advance(50);
    hub.relays.get('wss://relay1.test')!.inject(real);
    hub.relays.get('wss://relay0.test')!.inject(real);
    await advance(50);
    expect(b.got).toEqual([{ content: 'real', relay: 'wss://relay1.test' }]);
    // Events of another kind or topic are ignored even if a relay sends them anyway.
    const relay = hub.relays.get('wss://relay0.test')!;
    const stray = await signEvent(signer, {
      kind: KIND,
      created_at,
      tags: [['x', 'c']],
      content: 'stray',
    });
    relay.say((socket) => ['EVENT', [...socket.subs.keys()][0], stray]);
    relay.say(() => ['EVENT', 'not-my-subscription', { ...real, id: 'f'.repeat(64) }]);
    relay.say(() => ['EVENT']);
    relay.say(() => ['OK', 'unknown', true]);
    for (const socket of relay.sockets) socket.onmessage?.({ data: 'not json' });
    await advance(50);
    expect(b.text()).toEqual(['real']);
  });

  it('uses only relays that pass the self-check', async () => {
    const hub = new FakeHub();
    hub.add('wss://good.test');
    hub.add('wss://dead.test', { dead: true });
    hub.add('wss://hole.test', { drop: true });
    hub.add('wss://mute.test', { noOk: true });
    hub.add('wss://slow.test', { delayMs: 900 });
    const a = make(hub, 'a');
    let settled = false;
    void a.pool.settled.then(() => (settled = true));
    await advance(100);
    // Two relays are ready: enough to begin, without waiting for the slow and broken ones.
    expect(settled).toBe(true);
    expect(a.state('wss://good.test')).toBe('ready');
    expect(a.state('wss://mute.test')).toBe('ready');
    expect(a.state('wss://dead.test')).toBe('closed');
    expect(a.state('wss://hole.test')).toBe('checking');
    expect(a.state('wss://slow.test')).toBe('checking');
    await advance(1000);
    expect(a.state('wss://slow.test')).toBe('ready');
    expect(a.pool.health().find((r) => r.url === 'wss://slow.test')!.latencyMs).toBeGreaterThan(
      800,
    );
    await advance(6000);
    expect(a.pool.health().find((r) => r.url === 'wss://hole.test')).toMatchObject({
      error: 'Self-check failed: the test event did not come back.',
    });
    expect(['failed', 'connecting', 'checking']).toContain(a.state('wss://hole.test'));
    // The check is repeated later, with growing pauses, and a relay that recovers is used again.
    const hole = hub.relays.get('wss://hole.test')!;
    const before = hole.connections;
    await advance(60_000);
    expect(hole.connections).toBeGreaterThan(before);
    expect(hole.connections).toBeLessThan(before + 6);
    hole.behaviour.drop = false;
    await until(() => a.state('wss://hole.test') === 'ready', 120_000);
  });

  it('keeps a relay that never acknowledges events but does deliver them', async () => {
    const hub = new FakeHub();
    hub.add('wss://mute.test', { noOk: true });
    const a = make(hub, 'a');
    const b = make(hub, 'b');
    await until(() => a.pool.readyCount() === 1 && b.pool.readyCount() === 1);
    for (let n = 0; n < 8; n++) a.pool.publish('b', async () => `m${n}`);
    await advance(30_000);
    expect(b.text()).toEqual([0, 1, 2, 3, 4, 5, 6, 7].map((n) => `m${n}`));
    expect(a.state('wss://mute.test')).toBe('ready');
    expect(hub.relays.get('wss://mute.test')!.connections).toBe(2);
  });

  it('pauses a rate-limited relay, sends its events elsewhere, and resumes later', async () => {
    const hub = goodHub(3);
    // (No relay set aside for control events here, so the strict one is sure to be offered bulk.)
    const a = make(hub, 'a', { bulkCopies: 1, reserveControlRelay: false });
    const b = make(hub, 'b');
    await until(() => a.pool.readyCount() === 3 && b.pool.readyCount() === 3);
    const strict = hub.relays.get('wss://relay0.test')!;
    strict.behaviour.rateLimit = 0;
    const results = await done(
      Promise.all(
        Array.from({ length: 6 }, (_, n) => a.pool.publish('b', async () => `b${n}`, 'bulk').done),
      ),
    );
    expect(results).toEqual(Array(6).fill('sent'));
    await advance(200);
    // Every event arrived although the first relay refused its share.
    expect(b.text().sort()).toEqual([0, 1, 2, 3, 4, 5].map((n) => `b${n}`));
    expect(strict.refusals.length).toBeGreaterThan(0);
    expect(a.state('wss://relay0.test')).toBe('backoff');
    expect(a.pool.health()[0].error).toBe('rate-limited: slow down');

    // Nothing is sent there during the pause (10 s the first time).
    const refused = strict.refusals.length;
    strict.behaviour.rateLimit = undefined;
    await advance(9000);
    await done(a.pool.publish('b', async () => 'during').done);
    expect(
      strict.refusals.length + strict.accepted.filter((e) => e.content === 'during').length,
    ).toBe(refused);
    await advance(1500);
    expect(a.state('wss://relay0.test')).toBe('ready');
    await done(a.pool.publish('b', async () => 'after').done);
    await advance(100);
    expect(strict.accepted.some((e) => e.content === 'after')).toBe(true);

    // A second complaint doubles the pause.
    strict.behaviour.rateLimit = 0;
    await done(a.pool.publish('b', async () => 'again').done);
    await advance(100);
    expect(a.state('wss://relay0.test')).toBe('backoff');
    strict.behaviour.rateLimit = undefined;
    await advance(10_500);
    expect(a.state('wss://relay0.test')).toBe('backoff');
    await advance(10_000);
    expect(a.state('wss://relay0.test')).toBe('ready');
    expect(b.text()).toContain('again');
  });

  it('treats a rate-limit NOTICE or CLOSED the same way and resubscribes', async () => {
    const hub = goodHub(2);
    const a = make(hub, 'a');
    const b = make(hub, 'b');
    await until(() => a.pool.readyCount() === 2 && b.pool.readyCount() === 2);
    const relay = hub.relays.get('wss://relay0.test')!;
    const subId = (socket: { subs: Map<string, unknown> }) => [...socket.subs.keys()][0];

    relay.say(() => ['NOTICE', 'Too many requests, slow down']);
    await advance(10);
    expect(b.state('wss://relay0.test')).toBe('backoff');
    await advance(11_000);
    expect(b.state('wss://relay0.test')).toBe('ready');

    // The relay closes our subscription for an ordinary reason: subscribe again shortly after.
    const oldIds = [...relay.sockets].map(subId);
    relay.say((socket) => {
      const id = subId(socket);
      socket.subs.clear();
      return ['CLOSED', id, 'error: restarting'];
    });
    await advance(100);
    await done(a.pool.publish('b', async () => 'while closed').done);
    await advance(6000);
    const newIds = [...relay.sockets].map(subId);
    expect(newIds.every((id) => id && !oldIds.includes(id))).toBe(true);
    expect(b.pool.health()[0].error).toBe('error: restarting');
    await done(a.pool.publish('b', async () => 'after resubscribe').done);
    await advance(100);
    expect(b.text()).toEqual(['while closed', 'after resubscribe']);
    expect(b.got[1].relay).toBeDefined();

    // An ordinary notice is only remembered.
    relay.say(() => ['NOTICE', 'maintenance at midnight']);
    await advance(10);
    expect(b.pool.health()[0]).toMatchObject({ state: 'ready', error: 'maintenance at midnight' });
  });

  it('retires a relay that blocks us or demands login or proof of work', async () => {
    const hub = goodHub(1);
    const reasons = [
      'blocked: not on the list',
      'auth-required: log in first',
      'pow: 28 bits needed',
    ];
    reasons.forEach((reason, i) => hub.add(`wss://no${i}.test`));
    const closer = hub.add('wss://closer.test');
    const a = make(hub, 'a');
    const b = make(hub, 'b');
    await until(() => a.pool.readyCount() === 5 && b.pool.readyCount() === 5);
    reasons.forEach(
      (reason, i) => (hub.relays.get(`wss://no${i}.test`)!.behaviour.reject = () => reason),
    );
    closer.say((socket) => ['CLOSED', [...socket.subs.keys()][0], 'restricted: paid relay']);
    await done(a.pool.publish('b', async () => 'hello').done);
    await advance(100);
    expect(b.text()).toEqual(['hello']);
    reasons.forEach((reason, i) => {
      expect(a.pool.health().find((r) => r.url === `wss://no${i}.test`)).toMatchObject({
        state: 'retired',
        error: reason,
      });
    });
    expect(a.state('wss://closer.test')).toBe('retired');
    expect(b.state('wss://closer.test')).toBe('retired');
    // Retired means the socket is closed and never reopened this session.
    for (let i = 0; i < 3; i++) expect(hub.relays.get(`wss://no${i}.test`)!.sockets.size).toBe(1);
    expect(closer.sockets.size).toBe(0);
    await advance(600_000);
    for (let i = 0; i < 3; i++) expect(hub.relays.get(`wss://no${i}.test`)!.connections).toBe(2);
    expect(closer.connections).toBe(2);
    expect(a.pool.readyCount()).toBe(1);
  });

  it('reconnects after a dropped connection, waiting longer each time', async () => {
    const hub = goodHub(1);
    const relay = hub.relays.get('wss://relay0.test')!;
    const a = make(hub, 'a');
    const b = make(hub, 'b');
    await until(() => a.pool.readyCount() === 1 && b.pool.readyCount() === 1);
    relay.behaviour.dead = true;
    relay.disconnect();
    await advance(10);
    expect(a.state('wss://relay0.test')).toBe('closed');
    expect(a.pool.health()[0].error).toBe('Connection closed.');
    expect(await done(a.pool.publish('b', async () => 'lost').done)).toBe('failed');
    const before = relay.connections;
    await advance(60_000);
    // Per pool: about 2, 4, 8, 16, 32 s apart, so five tries in a minute, not thirty.
    const tries = (relay.connections - before) / 2;
    expect(tries).toBeGreaterThanOrEqual(4);
    expect(tries).toBeLessThanOrEqual(6);
    relay.behaviour.dead = false;
    await until(() => a.pool.readyCount() === 1 && b.pool.readyCount() === 1, 150_000);
    expect(await done(a.pool.publish('b', async () => 'back').done)).toBe('sent');
    await advance(100);
    expect(b.text()).toEqual(['back']);
  });

  it('drops a relay that stops acknowledging and learns a smaller size limit', async () => {
    const hub = goodHub(2);
    const small = hub.add('wss://small.test', { maxEventBytes: 2000 });
    const a = make(hub, 'a', { bulkCopies: 3, reserveControlRelay: false });
    const b = make(hub, 'b');
    await until(() => a.pool.readyCount() === 3 && b.pool.readyCount() === 3);
    const big = 'x'.repeat(5000);
    for (let n = 0; n < 4; n++) await done(a.pool.publish('b', async () => big + n, 'bulk').done);
    await done(a.pool.publish('b', async () => 'small', 'bulk').done);
    await advance(200);
    expect(b.text()).toEqual([0, 1, 2, 3].map((n) => big + n).concat('small'));
    // One refusal taught the pool; the relay stays in use for events that fit.
    expect(small.refusals).toHaveLength(1);
    expect(a.state('wss://small.test')).toBe('ready');
    expect(small.accepted.some((e) => e.content === 'small')).toBe(true);

    const mute = hub.relays.get('wss://relay0.test')!;
    mute.behaviour.noOk = true;
    for (let n = 0; n < 4; n++) await done(a.pool.publish('b', async () => `quiet${n}`).done);
    await advance(6000);
    expect(a.pool.health()[0].error).toBe('The relay stopped acknowledging events.');
    expect(a.state('wss://relay0.test')).toBe('closed');
    // It is disconnected and checked again a little later.
    await advance(3000);
    expect(mute.connections).toBe(3);
    expect(b.text().slice(5)).toEqual([0, 1, 2, 3].map((n) => `quiet${n}`));
  });

  it('paces sends per relay and lets control events jump the queue', async () => {
    const hub = goodHub(1);
    const relay = hub.relays.get('wss://relay0.test')!;
    // (The window of unanswered bulk events is opened wide: this test is about the token bucket.)
    const a = make(hub, 'a', { bulkCopies: 1, bulkWindow: 100 });
    const b = make(hub, 'b');
    await until(() => a.pool.readyCount() === 1 && b.pool.readyCount() === 1);
    const count = () => relay.accepted.filter((e) => /^(bulk|ctl)/.test(e.content)).length;
    for (let n = 0; n < 40; n++) a.pool.publish('b', async () => `bulk${n}`, 'bulk');
    await advance(50);
    // Burst of 16, of which 4 are kept back for control events.
    expect(count()).toBe(12);
    a.pool.publish('b', async () => 'ctl0');
    a.pool.publish('b', async () => 'ctl1');
    await advance(50);
    expect(b.text().slice(-2)).toEqual(['ctl0', 'ctl1']);
    expect(count()).toBe(14);
    // Then 8 a second.
    await advance(1000);
    expect(count()).toBeGreaterThanOrEqual(20);
    expect(count()).toBeLessThanOrEqual(23);
    await advance(4000);
    expect(count()).toBe(42);
    expect(b.text().filter((t) => t.startsWith('bulk'))).toEqual(
      Array.from({ length: 40 }, (_, n) => `bulk${n}`),
    );
  });

  it('feeds bulk to a relay only as fast as it answers, and keeps the quickest relay for control', async () => {
    const hub = new FakeHub();
    const quick = hub.add('wss://quick.test');
    const steady = hub.add('wss://steady.test', { delayMs: 20 });
    // About 2.7 s for each of the 5 KB events below, one after another.
    const slow = hub.add('wss://slow.test', { delayMs: 30, ingestBytesPerSecond: 2000 });
    const a = make(hub, 'a');
    const b = make(hub, 'b');
    await until(() => a.pool.readyCount() === 3 && b.pool.readyCount() === 3);
    const big = 'x'.repeat(5000);
    const bulk = (relay: typeof slow) => relay.incoming.filter((e) => e.content.startsWith(big));
    const results = Array.from(
      { length: 10 },
      (_, n) => a.pool.publish('b', async () => big + n, 'bulk').done,
    );
    expect(await done(Promise.all(results))).toEqual(Array(10).fill('sent'));
    await advance(300);
    // Everything arrived through the relay that keeps up…
    expect(b.text()).toEqual(Array.from({ length: 10 }, (_, n) => big + n));
    expect(bulk(steady)).toHaveLength(10);
    // …the slow one was handed two events and no more until it answers…
    expect(bulk(slow)).toHaveLength(2);
    // …and the relay with the quickest self-check carries no bulk at all.
    expect(bulk(quick)).toHaveLength(0);

    // So a control event has a clear path, and arrives at once.
    const before = b.got.length;
    a.pool.publish('b', async () => 'stop');
    await advance(20);
    expect(b.got.slice(before)).toEqual([{ content: 'stop', relay: 'wss://quick.test' }]);

    // The slow relay is not fed second copies that would arrive too late to matter.
    await advance(20_000);
    expect(bulk(slow).length).toBeLessThanOrEqual(4);
    expect(a.state('wss://slow.test')).toBe('ready');
    expect(b.text().filter((t) => t.startsWith(big))).toHaveLength(10);
  });

  it('lets the relay kept for control carry a bulk event every other relay refused', async () => {
    const hub = new FakeHub();
    hub.add('wss://quick.test');
    const others = [
      hub.add('wss://other1.test', { delayMs: 20 }),
      hub.add('wss://other2.test', { delayMs: 20 }),
    ];
    const a = make(hub, 'a');
    const b = make(hub, 'b');
    await until(() => a.pool.readyCount() === 3 && b.pool.readyCount() === 3);
    for (const relay of others) relay.behaviour.reject = () => 'error: not today';
    expect(await done(a.pool.publish('b', async () => 'refused elsewhere', 'bulk').done)).toBe(
      'sent',
    );
    await advance(100);
    expect(others.map((r) => r.refusals)).toEqual([['error: not today'], ['error: not today']]);
    expect(b.got).toEqual([{ content: 'refused elsewhere', relay: 'wss://quick.test' }]);
  });

  it('gives up on events that waited too long instead of sending them late', async () => {
    const hub = goodHub(1);
    const a = make(hub, 'a', { ratePerSecond: 1, burst: 2, controlQueueMs: 3000 });
    const b = make(hub, 'b');
    await until(() => a.pool.readyCount() === 1 && b.pool.readyCount() === 1);
    const results = await done(
      Promise.all(
        Array.from({ length: 10 }, (_, n) => a.pool.publish('b', async () => `c${n}`).done),
      ),
    );
    await advance(20_000);
    const sent = results.filter((r) => r === 'sent').length;
    expect(sent).toBeGreaterThanOrEqual(4);
    expect(sent).toBeLessThanOrEqual(6);
    expect(results.slice(sent)).toEqual(Array(10 - sent).fill('failed'));
    expect(b.text()).toEqual(Array.from({ length: sent }, (_, n) => `c${n}`));
  });

  it('cancels unsent events and never calls produce for them', async () => {
    const hub = goodHub(1);
    const a = make(hub, 'a', { bulkCopies: 1, bulkWindow: 100 });
    const b = make(hub, 'b');
    await until(() => a.pool.readyCount() === 1 && b.pool.readyCount() === 1);
    let produced = 0;
    const publications = Array.from({ length: 30 }, (_, n) =>
      a.pool.publish('b', async () => (produced++, `x${n}`), 'bulk'),
    );
    await advance(50);
    for (const p of publications) p.cancel();
    const results = await done(Promise.all(publications.map((p) => p.done)));
    expect(results.filter((r) => r === 'sent')).toHaveLength(12);
    expect(results.filter((r) => r === 'cancelled')).toHaveLength(18);
    await advance(10_000);
    expect(produced).toBe(12);
    expect(b.got).toHaveLength(12);
    // An event whose producer declines is dropped quietly.
    expect(await done(a.pool.publish('b', async () => undefined).done)).toBe('failed');
  });

  it('stop() closes sockets and timers and fails what was still queued', async () => {
    const hub = goodHub(2);
    hub.add('wss://dead.test', { dead: true });
    hub.add('wss://hole.test', { drop: true });
    const a = make(hub, 'a');
    await until(() => a.pool.readyCount() === 2);
    const queued = Array.from({ length: 40 }, (_, n) =>
      a.pool.publish('b', async () => `q${n}`, 'bulk'),
    );
    await advance(100);
    a.pool.stop();
    const results = await done(Promise.all(queued.map((p) => p.done)));
    expect(results.filter((r) => r === 'failed').length).toBeGreaterThan(0);
    expect(await done(a.pool.publish('b', async () => 'late').done)).toBe('failed');
    expect(hub.open).toBe(0);
    await advance(50);
    expect(vi.getTimerCount()).toBe(0);
    await advance(300_000);
    expect(hub.open).toBe(0);
    expect(vi.getTimerCount()).toBe(0);
  });
});
