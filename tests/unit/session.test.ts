import { afterEach, describe, expect, it, vi } from 'vitest';
import { joinRoom } from 'trystero';
import { GUEST_RATES, Session } from '../../web/src/network/session';
import { createIdentity, type HostIdentity } from '../../web/src/network/identity';
import { command, EMPTY, type Command, type Snapshot } from '../../web/src/protocol';
type Handler = (data: unknown, context: { peerId: string }) => void;
type MemoryRoom = {
  id: string;
  handler?: Handler;
  onPeerJoin?: (id: string) => void;
  onPeerLeave?: (id: string) => void;
  makeAction: () => {
    send: (data: unknown, options: { target: string }) => Promise<void>;
    onMessage: Handler | null;
  };
  getPeers: () => Record<string, { close: () => void }>;
  leave: () => Promise<void>;
};
class MemoryNetwork {
  groups = new Map<string, Map<string, MemoryRoom>>();
  transport(id: string) {
    return {
      id,
      join: ((config: { password: string }, roomId: string) => {
        const key = roomId + config.password;
        const peers = this.groups.get(key) ?? new Map<string, MemoryRoom>();
        this.groups.set(key, peers);
        const room: MemoryRoom = {
          id,
          makeAction: () => {
            const action = {
              send: async (data: unknown, options: { target: string }) => {
                const target = peers.get(options.target);
                if (target)
                  queueMicrotask(() =>
                    target.handler?.(JSON.parse(JSON.stringify(data)), { peerId: id }),
                  );
              },
              get onMessage() {
                return room.handler ?? null;
              },
              set onMessage(f: Handler | null) {
                room.handler = f ?? undefined;
              },
            };
            return action;
          },
          getPeers: () =>
            Object.fromEntries(
              [...peers.keys()]
                .filter((x) => x !== id)
                .map((x) => [
                  x,
                  {
                    close: () => {
                      peers.delete(x);
                      room.onPeerLeave?.(x);
                    },
                  },
                ]),
            ),
          leave: async () => {
            peers.delete(id);
            for (const p of peers.values()) p.onPeerLeave?.(id);
          },
        };
        peers.set(id, room);
        queueMicrotask(() => {
          for (const p of peers.values())
            if (p.id !== id) {
              p.onPeerJoin?.(id);
              room.onPeerJoin?.(p.id);
            }
        });
        return room;
      }) as unknown as typeof joinRoom,
    };
  }
}
const sessions: Session[] = [];
afterEach(async () => {
  await Promise.all(sessions.splice(0).map((s) => s.close()));
  vi.useRealTimers();
});
function setup(
  net: MemoryNetwork,
  identity: HostIdentity,
  id: string,
  host = false,
  {
    site = '2.35.0',
    companion = '2.35.0',
    answer,
  }: {
    /** Website version this page reports. Fixed here, so the tests do not follow package.json. */
    site?: string;
    /** Companion version the host's snapshot reports (the roomy control budget needs 2.35). */
    companion?: string;
    /** What the companion answers a relayed command with (host only). */
    answer?: (c: Command) => unknown;
  } = {},
) {
  let state: Snapshot = {
    ...EMPTY,
    available: true,
    companionVersion: companion,
    permissions: { remotePlayback: true, guestCounter: true },
  };
  let status = 'Connecting';
  let guests: string[] = [];
  const executed = new Set<string>();
  const commands: Command[] = [];
  const session = new Session(
    identity,
    host ? identity : undefined,
    () => state,
    async (c) => {
      if (!executed.has(c.id)) {
        executed.add(c.id);
        commands.push(c);
        if (c.op === 'counter')
          state = {
            ...state,
            revision: state.revision + 1,
            counter: state.counter + (c.delta ?? 0),
          };
      }
      return answer?.(c);
    },
    (s) => {
      state = s;
    },
    (s) => {
      status = s;
    },
    (ids) => {
      guests = ids;
    },
    net.transport(id),
    undefined,
    undefined,
    site,
  );
  sessions.push(session);
  return {
    session,
    state: () => state,
    status: () => status,
    guests: () => guests,
    executed,
    commands,
  };
}
/** Records every message that reaches `id`, before the session there handles it. */
function tap(net: MemoryNetwork, id: string) {
  const room = [...net.groups.values()][0].get(id)!;
  const seen: Record<string, unknown>[] = [];
  const inner = room.handler!;
  room.handler = (data, context) => {
    seen.push(data as Record<string, unknown>);
    inner(data, context);
  };
  return seen;
}
/** Hands `id` a message as if `from` had sent it. */
const inject = (net: MemoryNetwork, id: string, from: string, message: unknown) =>
  [...net.groups.values()][0].get(id)!.handler?.(message, { peerId: from });
const pause = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));
describe('authoritative sessions over deterministic transport (no public relay)', () => {
  it('pairs two guests, isolates another host and synchronizes counters with duplicate IDs', async () => {
    const n = new MemoryNetwork(),
      a = await createIdentity(),
      b = await createIdentity();
    const host = setup(n, a, 'hostA', true),
      other = setup(n, b, 'hostB', true),
      g1 = setup(n, a, 'guest1'),
      g2 = setup(n, a, 'guest2'),
      g3 = setup(n, b, 'guest3');
    await vi.waitFor(() => {
      expect(g1.status()).toBe('Connected');
      expect(g2.status()).toBe('Connected');
      expect(g3.status()).toBe('Connected');
    });
    const c = command('counter', { delta: 1 });
    await g1.session.sendCommand(c);
    await g1.session.sendCommand(c);
    host.session.broadcast();
    await vi.waitFor(() => expect(g2.state().counter).toBe(1));
    expect(g1.state().counter).toBe(1);
    expect(other.state().counter).toBe(0);
    expect(g3.state().counter).toBe(0);
    expect(host.guests()).toHaveLength(2);
    expect(other.guests()).toHaveLength(1);
  });
  it('rejects guest administration and unauthenticated host impersonation', async () => {
    const n = new MemoryNetwork(),
      a = await createIdentity();
    const host = setup(n, a, 'host', true),
      g = setup(n, a, 'guest');
    await vi.waitFor(() => expect(g.status()).toBe('Connected'));
    await expect(g.session.sendCommand(command('reset'))).rejects.toThrow('disabled');
    expect(host.executed.size).toBe(0);
    const peers = [...n.groups.values()][0];
    peers
      .get('guest')!
      .handler?.({ v: 1, type: 'state', state: { ...EMPTY, counter: 999 } }, { peerId: 'evil' });
    expect(g.state().counter).toBe(0);
  });
  it('revokes a guest and rejects further commands', async () => {
    const n = new MemoryNetwork(),
      a = await createIdentity();
    const host = setup(n, a, 'host', true),
      g = setup(n, a, 'guest');
    await vi.waitFor(() => expect(g.status()).toBe('Connected'));
    host.session.disconnect('guest');
    await vi.waitFor(() => expect(g.status()).toContain('Access revoked'));
    await expect(g.session.sendCommand(command('counter', { delta: 1 }))).rejects.toThrow(
      'not connected',
    );
  });
  it('recovers a refreshed guest and host without resetting the counter', async () => {
    const n = new MemoryNetwork(),
      a = await createIdentity();
    const h = setup(n, a, 'host', true),
      g = setup(n, a, 'guest');
    await vi.waitFor(() => expect(g.status()).toBe('Connected'));
    await g.session.sendCommand(command('counter', { delta: 1 }));
    await g.session.close();
    const fresh = setup(n, a, 'guest-refreshed');
    await vi.waitFor(() => expect(fresh.status()).toBe('Connected'));
    expect(fresh.state().counter).toBe(1);
    expect(h.state().counter).toBe(1);
  });
  it('reports host unavailable without false connection', async () => {
    vi.useFakeTimers();
    const n = new MemoryNetwork(),
      a = await createIdentity();
    const g = setup(n, a, 'guest');
    await vi.advanceTimersByTimeAsync(30000);
    expect(g.status()).toContain('Host unavailable');
  });
  it('drops malformed messages without execution', async () => {
    const n = new MemoryNetwork(),
      a = await createIdentity();
    const h = setup(n, a, 'host', true),
      g = setup(n, a, 'guest');
    await vi.waitFor(() => expect(g.status()).toBe('Connected'));
    const peers = [...n.groups.values()][0];
    peers
      .get('host')!
      .handler?.({ v: 1, type: 'command', command: { op: 'shell' } }, { peerId: 'guest' });
    await vi.waitFor(() => expect(h.guests()).toHaveLength(0));
    expect(h.executed.size).toBe(0);
  });
  it('answers queries over their budget with a refusal, without revoking the guest', async () => {
    const n = new MemoryNetwork(),
      a = await createIdentity();
    const h = setup(n, a, 'host', true),
      g = setup(n, a, 'guest');
    await vi.waitFor(() => expect(g.status()).toBe('Connected'));
    const replies = await Promise.allSettled(
      Array.from({ length: 14 }, () => g.session.sendCommand(command('statistics'))),
    );
    // The guest's hello counts toward the same budget, so a little under 12 of the 14 get through.
    const refused = replies.filter((r) => r.status === 'rejected');
    expect(refused.length).toBeGreaterThanOrEqual(14 - GUEST_RATES.other);
    for (const r of refused)
      expect(String((r as PromiseRejectedResult).reason)).toContain('Too many requests');
    expect(h.executed.size).toBe(14 - refused.length);
    expect(h.executed.size).toBeGreaterThan(0);
    expect(h.guests()).toEqual(['guest']);
    // Playback has its own budget (2.35): a Stop still goes through while queries are being refused.
    await expect(g.session.sendCommand(command('stop'))).resolves.toBeUndefined();
    await pause(1050);
    await expect(g.session.sendCommand(command('statistics'))).resolves.toBeUndefined();
  });
  it('keeps the 2.34 budget of 12 a second per guest when the companion is older than 2.35', async () => {
    const n = new MemoryNetwork(),
      a = await createIdentity();
    const h = setup(n, a, 'host', true, { companion: '2.34.2' }),
      g = setup(n, a, 'guest');
    await vi.waitFor(() => expect(g.status()).toBe('Connected'));
    const replies = await Promise.allSettled(
      Array.from({ length: 30 }, () => g.session.sendCommand(command('stop'))),
    );
    // An older companion has one 32/s budget for everyone, so the page relays at most 12 a second per guest.
    expect(h.executed.size).toBeLessThanOrEqual(GUEST_RATES.other);
    expect(replies.filter((r) => r.status === 'rejected').length).toBeGreaterThanOrEqual(
      30 - GUEST_RATES.other,
    );
    expect(h.guests()).toEqual(['guest']);
  });
  it('lets a 60-click burst of playback commands through without disconnecting the guest', async () => {
    const n = new MemoryNetwork(),
      a = await createIdentity();
    const h = setup(n, a, 'host', true),
      g = setup(n, a, 'guest');
    await vi.waitFor(() => expect(g.status()).toBe('Connected'));
    const ops = ['play', 'startAt', 'region', 'stop', 'seek', 'pause', 'audioPlay', 'reverse'];
    const replies = await Promise.allSettled(
      Array.from({ length: 60 }, (_, i) =>
        g.session.sendCommand(command(ops[i % ops.length] as Command['op'])),
      ),
    );
    expect(replies.every((r) => r.status === 'fulfilled')).toBe(true);
    expect(h.executed.size).toBe(60);
    expect(h.guests()).toEqual(['guest']);
    expect(g.status()).toBe('Connected');
  });
  it('refuses playback commands beyond their own budget and keeps the guest', async () => {
    const n = new MemoryNetwork(),
      a = await createIdentity();
    const h = setup(n, a, 'host', true),
      g = setup(n, a, 'guest');
    await vi.waitFor(() => expect(g.status()).toBe('Connected'));
    const acks = tap(n, 'guest');
    const sent = Array.from({ length: GUEST_RATES.control + 30 }, () => command('stop'));
    for (const c of sent) inject(n, 'host', 'guest', { v: 1, type: 'command', command: c });
    // Every command is answered: the ones over the budget with a refusal, none with silence.
    await vi.waitFor(() => expect(acks.filter((m) => m.type === 'ack')).toHaveLength(sent.length));
    expect(acks.filter((m) => m.type === 'ack' && m.ok === false)).toHaveLength(30);
    expect(h.executed.size).toBe(GUEST_RATES.control);
    expect(h.guests()).toEqual(['guest']);
  });
  it('disconnects a guest only for a rate no person could produce', async () => {
    const n = new MemoryNetwork(),
      a = await createIdentity();
    const h = setup(n, a, 'host', true),
      g = setup(n, a, 'guest');
    await vi.waitFor(() => expect(g.status()).toBe('Connected'));
    const flood = (count: number) => {
      for (let i = 0; i < count; i++)
        inject(n, 'host', 'guest', { v: 1, type: 'command', command: command('stop') });
    };
    flood(GUEST_RATES.abuse - 50);
    await pause(20);
    expect(h.guests()).toEqual(['guest']); // far over the budget is refused, not punished
    flood(100);
    await vi.waitFor(() => expect(h.guests()).toHaveLength(0));
    expect(h.executed.size).toBeLessThanOrEqual(GUEST_RATES.control);
  });
  it('refuses a well-formed command this page does not know, and drops a malformed one', async () => {
    const n = new MemoryNetwork(),
      a = await createIdentity();
    const h = setup(n, a, 'host', true),
      g = setup(n, a, 'guest');
    await vi.waitFor(() => expect(g.status()).toBe('Connected'));
    const acks = tap(n, 'guest');
    // A guest on a newer website than this host tab: an op and a field this page has never heard of.
    const newer = [
      { ...command('play'), op: 'someLaterOp' },
      { ...command('play'), someLaterField: true },
      { ...command('spotify'), action: 'someLaterAction' },
    ];
    for (const c of newer) inject(n, 'host', 'guest', { v: 1, type: 'command', command: c });
    await vi.waitFor(() => expect(acks.filter((m) => m.type === 'ack')).toHaveLength(3));
    for (const ack of acks.filter((m) => m.type === 'ack')) {
      expect(ack.ok).toBe(false);
      expect(ack.error).toContain('older than yours');
    }
    expect(h.executed.size).toBe(0);
    expect(h.guests()).toEqual(['guest']);
    // A known field of the wrong type is not a newer page; it is malformed traffic.
    inject(n, 'host', 'guest', {
      v: 1,
      type: 'command',
      command: { ...command('play'), soundId: 5 },
    });
    await vi.waitFor(() => expect(h.guests()).toHaveLength(0));
    expect(h.executed.size).toBe(0);
  });
  it('does not let unanswered slow work hold back a Play', async () => {
    const n = new MemoryNetwork(),
      a = await createIdentity();
    // The companion never answers a counter change here; everything else is answered at once.
    const h = setup(n, a, 'host', true, {
        answer: (c) => (c.op === 'counter' ? new Promise(() => {}) : undefined),
      }),
      g = setup(n, a, 'guest');
    await vi.waitFor(() => expect(g.status()).toBe('Connected'));
    const slow = (count: number) =>
      Array.from({ length: count }, () =>
        g.session.sendCommand(command('counter', { delta: 1 })).catch((e: Error) => e.message),
      );
    // Two lots, a second apart, so the 12-a-second budget for such commands refuses none of them.
    const waiting = [...slow(8)];
    await pause(1050);
    waiting.push(...slow(8));
    await vi.waitFor(() => expect(h.executed.size).toBe(16));
    // The guest's 16 places for slow work are taken: one more is refused here, without being sent.
    await expect(g.session.sendCommand(command('counter', { delta: 1 }))).rejects.toThrow(
      'Too many pending commands',
    );
    // Playback does not queue behind them (2.35).
    await expect(g.session.sendCommand(command('play'))).resolves.toBeUndefined();
    await expect(g.session.sendCommand(command('stop'))).resolves.toBeUndefined();
    expect(h.commands.slice(-2).map((c) => c.op)).toEqual(['play', 'stop']);
    expect(h.guests()).toEqual(['guest']);
    await g.session.close();
    expect(await Promise.all(waiting)).toEqual(Array(16).fill('Host disconnected.'));
  });
  it('passes a superseded acknowledgement to the guest as a success', async () => {
    const n = new MemoryNetwork(),
      a = await createIdentity();
    setup(n, a, 'host', true, {
      answer: (c) =>
        c.op === 'play' ? { superseded: true, reason: 'newer' } : { played: true, epoch: c.id },
    });
    const g = setup(n, a, 'guest');
    await vi.waitFor(() => expect(g.status()).toBe('Connected'));
    await expect(g.session.sendCommand(command('play'))).resolves.toEqual({
      superseded: true,
      reason: 'newer',
    });
    const start = command('startAt', { positionMs: 10 });
    await expect(g.session.sendCommand(start)).resolves.toEqual({ played: true, epoch: start.id });
  });
  it('keeps following the host clock from every report, so a clock step does not expire plays', async () => {
    const n = new MemoryNetwork(),
      a = await createIdentity();
    const h = setup(n, a, 'host', true),
      g = setup(n, a, 'guest');
    await vi.waitFor(() => expect(g.status()).toBe('Connected'));
    const reports = tap(n, 'guest');
    const first = command('play');
    await g.session.sendCommand(first);
    expect(Math.abs(h.commands[0].at - first.at)).toBeLessThan(1000);
    // The guest's clock falls a minute behind the host's (set back by hand, or by a time sync). The
    // next report says what time the host has, and the guest's commands follow it.
    const light = { ...g.state(), library: undefined };
    inject(n, 'guest', 'host', {
      v: 1,
      type: 'tick',
      state: light,
      libraryVersion: g.state().library.version,
      hostNow: Date.now() + 60000,
    });
    const second = command('play');
    await g.session.sendCommand(second);
    expect(Math.abs(h.commands[1].at - (second.at + 60000))).toBeLessThan(1000);
    // A report from a host tab older than 2.35 carries no clock and changes nothing.
    inject(n, 'guest', 'host', {
      v: 1,
      type: 'tick',
      state: light,
      libraryVersion: g.state().library.version,
    });
    const third = command('play');
    await g.session.sendCommand(third);
    expect(Math.abs(h.commands[2].at - (third.at + 60000))).toBeLessThan(1000);
    // The host's own reports carry its clock, and the guest comes back to it.
    reports.length = 0;
    h.session.broadcast(true);
    await vi.waitFor(() => expect(reports.some((m) => m.type === 'tick')).toBe(true));
    const tick = reports.find((m) => m.type === 'tick')!;
    expect(Math.abs((tick.hostNow as number) - Date.now())).toBeLessThan(1000);
    const fourth = command('play');
    await g.session.sendCommand(fourth);
    expect(Math.abs(h.commands[3].at - fourth.at)).toBeLessThan(1000);
  });
  it('tells each side the other’s website version', async () => {
    const n = new MemoryNetwork(),
      a = await createIdentity();
    setup(n, a, 'host', true, { site: '2.35.0' });
    const hellos = tap(n, 'host');
    const g = setup(n, a, 'guest', false, { site: '2.36.1' });
    expect(g.session.hostSite).toBe(''); // not known before the welcome
    const welcomes = tap(n, 'guest');
    await vi.waitFor(() => expect(g.status()).toBe('Connected'));
    expect(g.session.hostSite).toBe('2.35.0');
    expect(hellos.find((m) => m.type === 'hello')!.site).toBe('2.36.1');
    const welcome = welcomes.find((m) => m.type === 'welcome')!;
    expect(welcome.site).toBe('2.35.0');
    expect(typeof welcome.hostNow).toBe('number');
  });
  it('keeps the 2.35 command fields from a host tab that does not know them', async () => {
    const assetId = '22222222-2222-4222-8222-222222222222';
    const make = () =>
      command('trimAudio', { assetId, positionMs: 0, endMs: 500, action: 'none', autoplay: true });
    // '' is a tab from before 2.35, which reports no version at all.
    for (const site of ['', '2.34.2']) {
      const n = new MemoryNetwork(),
        a = await createIdentity();
      const h = setup(n, a, 'host', true, { site }),
        g = setup(n, a, 'guest');
      await vi.waitFor(() => expect(g.status()).toBe('Connected'));
      await g.session.sendCommand(make());
      expect(h.commands).toHaveLength(1);
      expect(h.commands[0]).not.toHaveProperty('autoplay');
      expect(h.commands[0]).toMatchObject({ op: 'trimAudio', assetId, endMs: 500 });
      await expect(g.session.sendCommand(command('panic', { action: 'enable' }))).rejects.toThrow(
        'older than yours',
      );
      expect(h.commands).toHaveLength(1); // the op that tab would not know was never sent
      expect(h.guests()).toEqual(['guest']);
    }
    const n = new MemoryNetwork(),
      a = await createIdentity();
    const h = setup(n, a, 'host', true, { site: '2.35.0' }),
      g = setup(n, a, 'guest');
    await vi.waitFor(() => expect(g.status()).toBe('Connected'));
    await g.session.sendCommand(make());
    expect(h.commands[0].autoplay).toBe(true);
    // Panic is the host's alone: a current tab relays nothing and says so.
    await expect(g.session.sendCommand(command('panic', { action: 'enable' }))).rejects.toThrow(
      'disabled',
    );
    expect(h.commands).toHaveLength(1);
  });
  it('holds back a burst toward a host tab from before 2.35, which would disconnect for it', async () => {
    const n = new MemoryNetwork(),
      a = await createIdentity();
    const h = setup(n, a, 'host', true, { site: '2.34.2' }),
      g = setup(n, a, 'guest');
    await vi.waitFor(() => expect(g.status()).toBe('Connected'));
    const replies = await Promise.allSettled(
      Array.from({ length: 30 }, () => g.session.sendCommand(command('stop'))),
    );
    // That tab drops a guest at 25 messages a second; the guest sends 20 and refuses the rest itself.
    const refused = replies.filter((r) => r.status === 'rejected');
    expect(refused).toHaveLength(10);
    expect(String((refused[0] as PromiseRejectedResult).reason)).toContain('Too many requests');
    expect(h.executed.size).toBe(20);
    expect(h.guests()).toEqual(['guest']);
  });
});
