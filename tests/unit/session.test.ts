import { afterEach, describe, expect, it, vi } from 'vitest';
import { joinRoom } from 'trystero';
import { Session } from '../../web/src/network/session';
import { createIdentity, type HostIdentity } from '../../web/src/network/identity';
import { command, EMPTY, type Snapshot } from '../../web/src/protocol';
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
function setup(net: MemoryNetwork, identity: HostIdentity, id: string, host = false) {
  let state: Snapshot = {
    ...EMPTY,
    available: true,
    permissions: { remotePlayback: true, guestCounter: true },
  };
  let status = 'Connecting';
  let guests: string[] = [];
  const executed = new Set<string>();
  const session = new Session(
    identity,
    host ? identity : undefined,
    () => state,
    async (c) => {
      if (!executed.has(c.id)) {
        executed.add(c.id);
        if (c.op === 'counter')
          state = {
            ...state,
            revision: state.revision + 1,
            counter: state.counter + (c.delta ?? 0),
          };
      }
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
  );
  sessions.push(session);
  return { session, state: () => state, status: () => status, guests: () => guests, executed };
}
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
  it('backpressures a short legitimate command burst without revoking the guest', async () => {
    const n = new MemoryNetwork(),
      a = await createIdentity();
    const h = setup(n, a, 'host', true),
      g = setup(n, a, 'guest');
    await vi.waitFor(() => expect(g.status()).toBe('Connected'));
    const replies = await Promise.allSettled(
      Array.from({ length: 14 }, () => g.session.sendCommand(command('stop'))),
    );
    expect(replies.some((r) => r.status === 'rejected')).toBe(true);
    expect(h.executed.size).toBeLessThanOrEqual(12);
    expect(h.guests()).toEqual(['guest']);
    await new Promise((resolve) => setTimeout(resolve, 1050));
    await expect(g.session.sendCommand(command('stop'))).resolves.toBeUndefined();
  });
  it('disconnects a guest flooding valid commands', async () => {
    const n = new MemoryNetwork(),
      a = await createIdentity();
    const h = setup(n, a, 'host', true),
      g = setup(n, a, 'guest');
    await vi.waitFor(() => expect(g.status()).toBe('Connected'));
    const peers = [...n.groups.values()][0];
    for (let i = 0; i < 30; i++)
      peers
        .get('host')!
        .handler?.({ v: 1, type: 'command', command: command('stop') }, { peerId: 'guest' });
    await vi.waitFor(() => expect(h.guests()).toHaveLength(0));
    expect(h.executed.size).toBeLessThanOrEqual(12);
  });
});
