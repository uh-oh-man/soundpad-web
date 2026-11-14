// 2.37: relay fallback, reconnect, lobby, capacity and per-guest permissions in Session, over the
// in-memory network (direct) and in-memory relays (tests/unit/relay-hub.ts). No real network.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  ENDED_INVITE,
  LOBBY_TEXT,
  RELAY,
  Session,
  UPLOAD_RELAY,
  type ConnectionInfo,
  type LobbyRequest,
  type RelayFactory,
} from '../../web/src/network/session';
import { createRelayLink } from '../../web/src/network/relay';
import { createIdentity, type HostIdentity } from '../../web/src/network/identity';
import {
  DEFAULT_POLICY,
  effectivePermissions,
  LIST,
  type SessionPolicy,
} from '../../web/src/network/policy';
import { command, EMPTY, type Command, type Snapshot } from '../../web/src/protocol';
import { MemoryNetwork } from './memory-network';
import { FakeHub, seededBytes } from './relay-hub';
import { advance, until, useFakeClock } from './relay-time';

vi.setConfig({ testTimeout: 60_000 });
const sessions: Session[] = [];
let made = 0;
beforeEach(() => {
  made = 0;
  useFakeClock();
});
afterEach(async () => {
  // Closing waits on timers (the relay's goodbye), so the simulated clock runs meanwhile.
  await done(Promise.all(sessions.splice(0).map((s) => s.close())));
  vi.useRealTimers();
});
function goodHub() {
  const hub = new FakeHub();
  for (let i = 0; i < 2; i++) hub.add(`wss://relay${i}.test`);
  return hub;
}
const relayOf =
  (hub: FakeHub): RelayFactory =>
  (options) =>
    createRelayLink({
      ...options,
      relays: hub.urls,
      webSocketFactory: hub.factory,
      minRelays: 1,
      random: seededBytes(5000 + made++),
    });
type PageOptions = {
  host?: boolean;
  hub?: FakeHub;
  person?: { id: string; name: string };
  policy?: Partial<SessionPolicy>;
  direct?: boolean;
  identity?: HostIdentity;
  /** The guest's profile, read for every hello (default: `person`). */
  profile?: () => { id: string; name: string };
  /** Host: this many sounds with long names, so a full report takes seconds over the relay. */
  sounds?: number;
};
function page(net: MemoryNetwork, invite: HostIdentity, id: string, options: PageOptions = {}) {
  let state: Snapshot = {
    ...EMPTY,
    available: true,
    companionVersion: '2.37.0',
    permissions: { remotePlayback: true, guestCounter: true },
    ...(options.sounds
      ? {
          library: {
            version: 'big',
            sounds: Array.from({ length: options.sounds }, (_, i) => ({
              id: String(i),
              name: `${i} ${'x'.repeat(4000)}`,
              icon: null,
            })),
            categories: [],
          },
        }
      : {}),
  };
  let status = 'Connecting';
  let guests: string[] = [];
  let links: Record<string, string> = {};
  let requests: LobbyRequest[] = [];
  let policy: SessionPolicy = { ...DEFAULT_POLICY, ...options.policy };
  const connections: ConnectionInfo[] = [];
  const commands: Command[] = [];
  const session = new Session(
    invite,
    options.host ? (options.identity ?? invite) : undefined,
    () => state,
    async (c) => {
      commands.push(c);
      if (c.op === 'counter')
        state = { ...state, revision: state.revision + 1, counter: state.counter + (c.delta ?? 0) };
    },
    (s) => (state = s),
    (s) => (status = s),
    (ids, kinds) => {
      guests = ids;
      links = kinds;
    },
    net.transport(id),
    (options.profile || options.person) && {
      get: options.profile ?? (() => options.person!),
      changed: () => {},
    },
    undefined,
    '2.37.0',
    {
      relay: options.hub && relayOf(options.hub),
      direct: options.direct,
      policy,
      policyChanged: (p) => (policy = p),
      lobby: (r) => (requests = r),
      connection: (c) => connections.push(c),
      probe: async () => undefined,
      fallbackMs: 3000,
    },
  );
  sessions.push(session);
  return {
    session,
    state: () => state,
    status: () => status,
    guests: () => guests,
    links: () => links,
    requests: () => requests,
    policy: () => policy,
    connection: () => connections.at(-1),
    connections,
    commands,
  };
}
/** Awaits a promise while the simulated clock runs (relay sends resolve on timers). */
async function done<T>(promise: Promise<T>) {
  let settled = false;
  void promise.finally(() => (settled = true)).catch(() => undefined);
  await until(() => settled);
  return promise;
}
const person = (name: string) => ({ id: crypto.randomUUID(), name });

describe('relay fallback (2.37)', () => {
  it('falls back to the relay when direct never connects, and runs commands and state over it', async () => {
    const hub = goodHub(),
      net = new MemoryNetwork(true),
      a = await createIdentity();
    const h = page(net, a, 'host', { host: true, hub }),
      g = page(net, a, 'guest', { hub, person: person('Ann') });
    await advance(2000);
    expect(g.connection()?.relay).toBeUndefined(); // direct is tried first
    await until(() => g.status() === 'Connected', 30000);
    expect(g.session.transport).toBe('relay');
    expect(g.connection()?.transport).toBe('relay');
    // The host lists the guest once, under a peer id that cannot collide with a WebRTC one.
    expect(h.guests()).toEqual([RELAY + 'guest']);
    expect(h.links()).toEqual({ [RELAY + 'guest']: 'relay' });
    await until(
      () => h.connection()?.links?.[Object.keys(h.connection()!.links!)[0]] !== undefined,
    );
    // Command and acknowledgement, then the new state, all over the relay.
    await done(g.session.sendCommand(command('counter', { delta: 1 })));
    expect(h.commands.map((c) => c.op)).toEqual(['counter']);
    h.session.broadcast();
    await until(() => g.state().counter === 1);
    // Uploads are refused over the relay with a clear message.
    await expect(g.session.upload(new Uint8Array(10), 'a.wav')).rejects.toThrow(UPLOAD_RELAY);
  });
  it('switches back to direct when it starts working, and closes the relay', async () => {
    const hub = goodHub(),
      net = new MemoryNetwork(true),
      a = await createIdentity();
    const h = page(net, a, 'host', { host: true, hub }),
      g = page(net, a, 'guest', { hub, person: person('Ann') });
    await until(() => g.status() === 'Connected' && g.session.transport === 'relay', 30000);
    net.heal();
    await until(() => g.session.transport === 'direct');
    expect(g.connection()?.relay).toBeUndefined();
    // The relay connection goes; the guest stays listed once, now direct.
    await until(() => h.links()['guest'] === 'direct' && h.guests().length === 1, 30000);
    await done(g.session.sendCommand(command('play')));
    expect(h.commands.at(-1)?.op).toBe('play');
    expect(g.status()).toBe('Connected');
  });
  it('goes straight to the relay without WebRTC', async () => {
    const hub = goodHub(),
      net = new MemoryNetwork(),
      a = await createIdentity();
    page(net, a, 'host', { host: true, hub });
    const g = page(net, a, 'guest', { hub, direct: false });
    expect(g.status()).toMatch(/^WebRTC blocked/);
    await until(() => g.status() === 'Connected', 15000);
    expect(g.session.transport).toBe('relay');
    expect(g.connection()?.webrtc).toBe(false);
  });
  it('says no relay is reachable when every relay is down', async () => {
    const hub = new FakeHub();
    hub.add('wss://dead0.test', { dead: true });
    hub.add('wss://dead1.test', { dead: true });
    const net = new MemoryNetwork(true),
      a = await createIdentity();
    const g = page(net, a, 'guest', { hub });
    await until(() => g.connection()?.diagnosis?.cause === 'no-relays', 30000);
    expect(g.status()).toMatch(/^No relay reachable\. 0 of 2 public relays answered/);
  });
  it('says the host was not found when the relays work but nobody answers', async () => {
    const hub = goodHub(),
      net = new MemoryNetwork(true),
      a = await createIdentity();
    const g = page(net, a, 'guest', { hub });
    await until(() => g.connection()?.diagnosis?.cause === 'host-not-found', 40000);
    expect(g.status()).toMatch(/^Host not found\. The network is fine \(2 of 2/);
  });
});

describe('reconnect (2.37)', () => {
  it('reconnects to the same session by itself and says so once', async () => {
    const net = new MemoryNetwork(),
      a = await createIdentity();
    const h = page(net, a, 'host', { host: true }),
      g = page(net, a, 'guest');
    await until(() => g.status() === 'Connected');
    await h.session.close();
    await until(() => g.status() === 'Reconnecting');
    expect(g.connection()?.reconnecting).toBe(true);
    // Commands made meanwhile are refused at once, never queued for later.
    await expect(g.session.sendCommand(command('play'))).rejects.toThrow('not connected');
    await advance(20000);
    expect(g.status()).toBe('Reconnecting'); // never gives up
    expect(Date.now() - g.connection()!.since).toBeGreaterThanOrEqual(20000);
    // The companion kept the identity: a new host page (a reload) is the same session.
    page(net, a, 'host-2', { host: true });
    await until(() => g.status() === 'Connected', 10000);
    expect(g.connections.filter((c) => c.reconnected)).toHaveLength(1);
    expect(g.connection()?.reconnecting).toBe(false);
  });
  it('tells a guest the invitation ended when the host answers with another identity', async () => {
    const net = new MemoryNetwork(),
      a = await createIdentity(),
      b = await createIdentity();
    const h = page(net, a, 'host', { host: true }),
      g = page(net, a, 'guest');
    await until(() => g.status() === 'Connected');
    await h.session.close();
    // The status can already read 'Reconnecting for N s' when it is first sampled under load.
    await until(() => g.status().startsWith('Reconnecting'));
    // The same room, but a new key: what a host that started over looks like to this invitation.
    page(net, a, 'host-new', { host: true, identity: { ...b, room: a.room, secret: a.secret } });
    await until(() => g.status() === ENDED_INVITE, 60000);
    expect(g.connection()?.ended).toBe(true);
  });
});

describe('lobby and capacity (2.37)', () => {
  it('holds a guest until the host lets them in, keyed by their participant id', async () => {
    const net = new MemoryNetwork(),
      a = await createIdentity(),
      ann = person('Ann');
    const h = page(net, a, 'host', { host: true, policy: { admission: 'ask' } }),
      g = page(net, a, 'guest', { person: ann });
    await until(() => g.status() === LOBBY_TEXT.waiting);
    expect(g.connection()?.lobby).toBe('waiting');
    expect(h.requests()).toMatchObject([{ id: ann.id, name: 'Ann', kind: 'admit' }]);
    expect(h.guests()).toEqual([]);
    await h.session.letIn(ann.id);
    await until(() => g.status() === 'Connected');
    expect(h.requests()).toEqual([]);
    expect(h.policy().admitted).toEqual([ann.id]);
    // A reload is a new peer id with the same participant id: in at once, no new request.
    await g.session.close();
    const again = page(net, a, 'guest-reloaded', { person: ann });
    await until(() => again.status() === 'Connected');
    expect(h.requests()).toEqual([]);
  });
  it('turns a guest away calmly and remembers it', async () => {
    const net = new MemoryNetwork(),
      a = await createIdentity(),
      bob = person('Bob');
    const h = page(net, a, 'host', { host: true, policy: { admission: 'ask' } }),
      g = page(net, a, 'guest', { person: bob });
    await until(() => h.requests().length === 1);
    h.session.turnAway(bob.id);
    await until(() => g.status() === LOBBY_TEXT.turnedAway);
    expect(h.policy().turnedAway).toEqual([bob]);
    expect(h.requests()).toEqual([]);
    // Opening the invitation again does not ask the host again.
    const again = page(net, a, 'guest-2', { person: bob });
    await until(() => again.status() === LOBBY_TEXT.turnedAway);
    expect(h.requests()).toEqual([]);
    // Allow again: the next visit is in.
    await h.session.letIn(bob.id);
    expect(h.policy().turnedAway).toEqual([]);
    const third = page(net, a, 'guest-3', { person: bob });
    await until(() => third.status() === 'Connected');
  });
  it('lets a request lapse after two minutes, and the guest can ask again', async () => {
    const net = new MemoryNetwork(),
      a = await createIdentity(),
      cat = person('Cat');
    const h = page(net, a, 'host', { host: true, policy: { admission: 'ask' } }),
      g = page(net, a, 'guest', { person: cat });
    await until(() => h.requests().length === 1);
    await advance(118000);
    expect(h.requests()).toHaveLength(1);
    await until(() => g.status() === LOBBY_TEXT.expired, 10000);
    expect(h.requests()).toEqual([]);
    await advance(5000);
    expect(h.requests()).toEqual([]); // the guest's hellos do not ask again by themselves
    g.session.askAgain();
    await until(() => h.requests().length === 1);
    // An answer to a hello sent before the click can still say "expired"; the next one says waiting.
    await until(() => g.status() === LOBBY_TEXT.waiting, 5000);
  });
  it('offers an extra place in a full session, and Let in adds one for this session', async () => {
    const net = new MemoryNetwork(),
      a = await createIdentity(),
      ann = person('Ann'),
      bob = person('Bob');
    const h = page(net, a, 'host', { host: true, policy: { capacity: 1 } }),
      first = page(net, a, 'guest-a', { person: ann });
    await until(() => first.status() === 'Connected');
    const second = page(net, a, 'guest-b', { person: bob });
    await until(() => second.status() === LOBBY_TEXT.full);
    expect(second.connection()?.canAskExtra).toBe(true);
    expect(h.requests()).toEqual([]);
    second.session.askForPlace();
    await until(() => h.requests().length === 1);
    expect(h.requests()[0]).toMatchObject({ id: bob.id, kind: 'extra' });
    await h.session.letIn(bob.id);
    await until(() => second.status() === 'Connected');
    expect(h.policy().extra).toBe(1);
    expect(h.guests()).toHaveLength(2);
  });
  it('keeps a place for a guest who just left, so their reload is not refused', async () => {
    const net = new MemoryNetwork(),
      a = await createIdentity(),
      ann = person('Ann');
    const h = page(net, a, 'host', { host: true, policy: { capacity: 1 } }),
      g = page(net, a, 'guest', { person: ann });
    await until(() => g.status() === 'Connected');
    await g.session.close();
    await until(() => h.guests().length === 0);
    const other = page(net, a, 'other', { person: person('Zed') });
    await until(() => other.status() === LOBBY_TEXT.full);
    const back = page(net, a, 'guest-back', { person: ann });
    await until(() => back.status() === 'Connected');
  });
});

describe('per-guest permissions (2.37)', () => {
  it('blocks one guest’s playback but not another’s, and shows each their own permissions', async () => {
    const net = new MemoryNetwork(),
      a = await createIdentity(),
      ann = person('Ann'),
      bob = person('Bob');
    const h = page(net, a, 'host', { host: true }),
      ga = page(net, a, 'guest-a', { person: ann }),
      gb = page(net, a, 'guest-b', { person: bob });
    await until(() => ga.status() === 'Connected' && gb.status() === 'Connected');
    h.session.setPolicy({
      ...h.policy(),
      overrides: { [ann.id]: { remotePlayback: false }, [bob.id]: { guestUploads: false } },
    });
    await until(() => ga.state().permissions.remotePlayback === false);
    expect(gb.state().permissions.remotePlayback).toBe(true);
    await expect(ga.session.sendCommand(command('play'))).rejects.toThrow('disabled');
    await done(gb.session.sendCommand(command('play')));
    expect(h.commands.map((c) => c.op)).toEqual(['play']);
    // Bob's own switch for uploads covers removing his clips as well.
    const removal = command('removeAudio', { assetId: crypto.randomUUID() });
    await expect(gb.session.sendCommand(removal)).rejects.toThrow('disabled');
    await done(ga.session.sendCommand(command('removeAudio', { assetId: crypto.randomUUID() })));
    expect(h.commands.map((c) => c.op)).toEqual(['play', 'removeAudio']);
    // "Same as everyone" again.
    h.session.setPolicy({ ...h.policy(), overrides: {} });
    await until(() => ga.state().permissions.remotePlayback === true);
    await done(ga.session.sendCommand(command('play')));
  });
});

describe('review fixes (2.37)', () => {
  /** A peer that speaks the protocol by hand, as a modified guest page could. */
  function raw(net: MemoryNetwork, invite: HostIdentity, id: string) {
    const room = net
      .transport(id)
      .join(
        { appId: 'org.soundpad-web.v1', password: invite.secret } as never,
        invite.room,
      ) as unknown as {
      makeAction: () => {
        send: (data: unknown, options: { target: string }) => Promise<void>;
        onMessage: ((data: unknown) => void) | null;
      };
    };
    const action = room.makeAction();
    const received: { type: string; lobby?: string }[] = [];
    action.onMessage = (data) => received.push(data as { type: string });
    const nonce = crypto.randomUUID();
    return {
      received,
      hello: (person: { id: string; name: string }) =>
        action.send({ v: 1, type: 'hello', nonce, person, site: '2.37.0' }, { target: 'host' }),
    };
  }

  it('lets a relay guest with a large library in: a hello while the welcome is on its way does not restart it', async () => {
    const hub = goodHub(),
      net = new MemoryNetwork(),
      a = await createIdentity();
    // About 1.6 MB: well over the 2.5 s between a waiting guest's hellos at the relay's pace.
    const h = page(net, a, 'host', { host: true, hub, sounds: 400 });
    const g = page(net, a, 'guest', { hub, direct: false, person: person('Ann') });
    await until(() => g.status() === 'Connected', 60000);
    expect(g.session.transport).toBe('relay');
    expect(g.state().library.sounds).toHaveLength(400);
    expect(h.guests()).toEqual([RELAY + 'guest']);
    // And it stays connected: the host's reports keep arriving.
    await advance(35000);
    expect(g.status()).toBe('Connected');
    expect(g.connection()?.reconnecting).toBe(false);
  });

  it('switching to "Let guests in at once" lets in only as many waiting guests as there are places', async () => {
    const net = new MemoryNetwork(),
      a = await createIdentity();
    const h = page(net, a, 'host', { host: true, policy: { admission: 'ask', capacity: 2 } });
    const guests = ['A', 'B', 'C', 'D'].map((n, i) =>
      page(net, a, `guest-${i}`, { person: person(n) }),
    );
    await until(() => h.requests().length === 4);
    h.session.setPolicy({ ...h.policy(), admission: 'open' });
    await until(() => h.guests().length >= 2);
    await advance(3000);
    expect(h.guests()).toHaveLength(2);
    expect(guests.filter((g) => g.status() === 'Connected')).toHaveLength(2);
    expect(h.requests()).toHaveLength(2);
  });

  it('two hellos at once never both take the last place', async () => {
    const net = new MemoryNetwork(),
      a = await createIdentity();
    const h = page(net, a, 'host', { host: true, policy: { capacity: 1 } });
    await advance(100);
    const one = raw(net, a, 'one'),
      two = raw(net, a, 'two');
    await Promise.all([one.hello(person('One')), two.hello(person('Two'))]);
    await advance(500);
    expect(h.guests()).toHaveLength(1);
    const types = [...one.received, ...two.received].map((m) => m.lobby ?? m.type);
    expect(types.sort()).toEqual(['full', 'welcome']);
  });

  it('an override can only take a permission away', () => {
    const off = { remotePlayback: false, guestCounter: false, guestMimic: false };
    expect(effectivePermissions(off, { remotePlayback: true, guestMimic: true })).toEqual(off);
    const on = { remotePlayback: true, guestCounter: true };
    expect(effectivePermissions(on, { remotePlayback: false })).toEqual({
      remotePlayback: false,
      guestCounter: true,
    });
    expect(effectivePermissions(on, { guestCounter: true })).toEqual(on);
  });

  it('a connection keeps the id it was let in with, so a new id cannot shed its own "Off"', async () => {
    const net = new MemoryNetwork(),
      a = await createIdentity(),
      ann = person('Ann');
    let profile = ann;
    const h = page(net, a, 'host', { host: true });
    const g = page(net, a, 'guest', { profile: () => profile });
    await until(() => g.status() === 'Connected');
    h.session.setPolicy({ ...h.policy(), overrides: { [ann.id]: { remotePlayback: false } } });
    await until(() => g.state().permissions.remotePlayback === false);
    // The same connection says hello again as somebody new (a modified page), with a new name too.
    profile = { id: crypto.randomUUID(), name: 'Zed' };
    g.session.rename();
    await advance(1000);
    await expect(g.session.sendCommand(command('play'))).rejects.toThrow('disabled');
    expect(h.commands).toEqual([]);
    expect(h.policy().admitted).toEqual([ann.id]);
    expect(g.state().permissions.remotePlayback).toBe(false);
  });

  it('one connection holds one lobby request, and the lobby has a limit', async () => {
    const net = new MemoryNetwork(),
      a = await createIdentity();
    const h = page(net, a, 'host', { host: true, policy: { admission: 'ask' } });
    // A page that says hello as somebody new every time.
    page(net, a, 'guest', { profile: () => person('Again') });
    await advance(20000);
    expect(h.requests()).toHaveLength(1);
    // Many invitation holders at once: the list stops at what the host's views can show.
    const peers = Array.from({ length: LIST + 6 }, (_, i) => raw(net, a, `raw-${i}`));
    for (const [i, p] of peers.entries()) await p.hello(person(`P${i}`));
    await advance(1000);
    expect(h.requests()).toHaveLength(LIST);
    expect(peers.at(-1)!.received.map((m) => m.lobby)).toContain('full');
  });
});
