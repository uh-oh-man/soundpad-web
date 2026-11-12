import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { HostIdentity } from '../../web/src/network/identity';

// The guest room is a stand-in: these tests check which room a host page runs, never Trystero itself.
const rooms = vi.hoisted(() => [] as FakeRoom[]);
type FakeRoom = {
  invite: { room: string };
  host: { room: string } | undefined;
  guests: (ids: string[]) => void;
  profiles: { changed: (people: Record<string, { id: string; name: string }>) => void };
  extras: { layouts: (layouts: unknown[]) => void };
  options: {
    policy?: unknown;
    policyChanged: (policy: unknown) => void;
    lobby: (requests: unknown[]) => void;
  };
  closed?: { revoke: boolean };
  disconnected: string[];
};
vi.mock('../../web/src/network/session', () => ({
  MAX_GUESTS: 12,
  Session: class {
    closed?: { revoke: boolean };
    disconnected: string[] = [];
    invite: unknown;
    host: unknown;
    guests: unknown;
    profiles: unknown;
    extras: unknown;
    options: unknown;
    constructor(...args: unknown[]) {
      [this.invite, this.host, , , , , this.guests, , this.profiles, this.extras, , this.options] =
        args;
      rooms.push(this as unknown as FakeRoom);
    }
    broadcast() {}
    broadcastWaveform() {}
    rename() {}
    async publishLayout() {}
    disconnect(peer: string) {
      this.disconnected.push(peer);
    }
    async close(revoke = false) {
      this.closed = { revoke };
    }
  },
}));
const made = vi.hoisted(() => ({ count: 0 }));
const identity = (n: number): HostIdentity => ({
  v: 1,
  room: String(n).padStart(32, 'r'),
  secret: 's'.repeat(43),
  publicKey: 'p'.repeat(120),
  privateKey: 'k'.repeat(184),
  shortCode: '2' + String(n).padStart(43, 'c'),
});
vi.mock('../../web/src/network/identity', async (original) => ({
  ...(await original<typeof import('../../web/src/network/identity')>()),
  createIdentity: vi.fn(async () => identity(100 + ++made.count)),
}));

import { Controller, SESSION_INFO_BYTES } from '../../web/src/controller';
import { createIdentity } from '../../web/src/network/identity';
import { LocalBridge } from '../../web/src/network/local';
import { EMPTY } from '../../web/src/protocol';
import { hostCopy, TAB_VIEW_NOTE, VIEW_NOTE } from '../../web/src/hosting';
import { supports } from '../../web/src/version';
import { DEFAULT_POLICY } from '../../web/src/network/policy';

/** The companion's socket, answered by hand. No real socket is opened. */
class FakeSocket {
  static OPEN = 1;
  static last: FakeSocket;
  readyState = FakeSocket.OPEN;
  sent: Record<string, unknown>[] = [];
  onopen?: () => void;
  onmessage?: (e: { data: string }) => void;
  onerror?: () => void;
  onclose?: () => void;
  constructor(public url: string) {
    FakeSocket.last = this;
  }
  send(text: string) {
    const frame = JSON.parse(text);
    this.sent.push(frame);
    // The host's beginSession is answered at once, as the companion does.
    if (frame.command?.op === 'beginSession')
      queueMicrotask(() => this.receive({ type: 'ack', id: frame.command.id, ok: true }));
  }
  receive(data: unknown) {
    this.onmessage?.({ data: JSON.stringify(data) });
  }
  close() {
    this.readyState = 3;
    this.onclose?.();
  }
  /** Desktop-session messages this page sent (identity, session, ownerAction). */
  posted(type: string) {
    return this.sent.filter((f) => f.type === type);
  }
  commands(op: string) {
    return this.sent.filter((f) => (f.command as { op?: string } | undefined)?.op === op);
  }
}
const storage = () => {
  const values = new Map<string, string>();
  return {
    getItem: (k: string) => values.get(k) ?? null,
    setItem: (k: string, v: string) => void values.set(k, String(v)),
    removeItem: (k: string) => void values.delete(k),
  };
};
const token = 'a'.repeat(64);
const ownerKey = 'b'.repeat(64);
const state = (companionVersion: string, revision = 1) => ({
  ...EMPTY,
  companionVersion,
  revision,
});
let place: { hash: string; pathname: string; search: string; href: string };
const flush = () => vi.advanceTimersByTimeAsync(0);

/** A host page opened from `hash`, connected to a fake companion that reports `version`. */
async function hostPage(hash: string) {
  place.hash = hash;
  const page = new Controller();
  await page.start();
  const socket = FakeSocket.last;
  socket.onopen?.();
  return { page, socket };
}

beforeEach(() => {
  vi.useFakeTimers();
  rooms.length = 0;
  made.count = 0;
  vi.mocked(createIdentity).mockClear();
  place = { hash: '', pathname: '/', search: '', href: 'https://site.test/' };
  vi.stubGlobal('WebSocket', FakeSocket);
  vi.stubGlobal('location', place);
  vi.stubGlobal('history', { replaceState: () => (place.hash = '') });
  vi.stubGlobal('sessionStorage', storage());
  vi.stubGlobal('localStorage', storage());
});
afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe('desktop session hello (2.36)', () => {
  it('sends protocol 3, a client id per page and the window’s owner key, then hides the key', async () => {
    const { socket } = await hostPage(`#host=1234.${token}&owner=${ownerKey}`);
    const hello = socket.sent[0];
    expect(hello).toMatchObject({ token, protocol: 3, ownerKey });
    expect(hello.clientId).toMatch(/^[0-9a-f-]{36}$/);
    expect(place.hash).toBe('');
    // Kept like the address, so a reload of the window page stays the owner.
    expect(sessionStorage.getItem('soundpad-owner-key')).toBe(ownerKey);
    // The page hands its id over when it goes away, so the reloaded page presents the same one (review, 2.36).
    sessionStorage.setItem('soundpad-client-id', hello.clientId as string);
    const reload = await hostPage('');
    expect(reload.socket.sent[0]).toMatchObject({ token, protocol: 3, ownerKey });
    expect(reload.socket.sent[0].clientId).toBe(hello.clientId);
    // It is taken back on load, so a duplicate of the open tab cannot present it too.
    expect(sessionStorage.getItem('soundpad-client-id')).toBeNull();
  });
  it('sends no owner key from an ordinary launch link, and forgets an earlier one', async () => {
    sessionStorage.setItem('soundpad-owner-key', ownerKey);
    const { socket } = await hostPage(`#host=1234.${token}`);
    expect(socket.sent[0]).toMatchObject({ token, protocol: 3 });
    expect(socket.sent[0]).not.toHaveProperty('ownerKey');
    expect(sessionStorage.getItem('soundpad-owner-key')).toBeNull();
  });
});

describe('session owner (2.36)', () => {
  it('reuses the identity the companion kept, creating none and sending none', async () => {
    const { page, socket } = await hostPage(`#host=1234.${token}&owner=${ownerKey}`);
    socket.receive({ type: 'role', role: 'owner', identity: identity(7) });
    socket.receive({ type: 'state', state: state('2.36.0') });
    await flush();
    expect(rooms).toHaveLength(1);
    expect(rooms[0].host!.room).toBe(identity(7).room);
    expect(createIdentity).not.toHaveBeenCalled();
    expect(socket.posted('identity')).toEqual([]);
    expect(page.getSnapshot().hosting).toBe('window');
    expect(page.getSnapshot().invite).toBe('https://site.test/#join=' + identity(7).shortCode);
  });
  it('creates an identity when the companion has none, and gives it to the companion', async () => {
    const { page, socket } = await hostPage(`#host=1234.${token}`);
    socket.receive({ type: 'role', role: 'owner', identity: null });
    socket.receive({ type: 'state', state: state('2.36.0') });
    await flush();
    expect(createIdentity).toHaveBeenCalledTimes(1);
    expect(rooms[0].host!.room).toBe(identity(101).room);
    expect(socket.posted('identity')).toEqual([{ type: 'identity', identity: identity(101) }]);
    // A browser tab that owns the session (a companion without its window) keeps the tab copy.
    expect(page.getSnapshot().hosting).toBe('tab');
  });
  it('publishes guests, people and the invite to views, gathered into one message', async () => {
    const { socket } = await hostPage(`#host=1234.${token}&owner=${ownerKey}`);
    socket.receive({ type: 'role', role: 'owner', identity: identity(7) });
    socket.receive({ type: 'state', state: state('2.36.0') });
    await flush();
    await vi.advanceTimersByTimeAsync(300);
    const before = socket.posted('session').length;
    const person = { id: crypto.randomUUID(), name: 'Ann' };
    rooms[0].guests(['peer-a']);
    rooms[0].profiles.changed({ 'peer-a': person });
    rooms[0].guests(['peer-a', 'peer-b']);
    await vi.advanceTimersByTimeAsync(249);
    expect(socket.posted('session')).toHaveLength(before);
    await vi.advanceTimersByTimeAsync(1);
    expect(socket.posted('session').slice(before)).toEqual([
      {
        type: 'session',
        invite: 'https://site.test/#join=' + identity(7).shortCode,
        guests: ['peer-a', 'peer-b'],
        people: { 'peer-a': person },
        layouts: [],
        ownerWindow: true,
        control: { requests: [], policy: DEFAULT_POLICY, links: {} },
      },
    ]);
  });
  it('leaves layouts out of a session message the companion would refuse as too large', async () => {
    // The companion closes a socket over a 200,000-byte message, which would cost the owner its place.
    const { socket } = await hostPage(`#host=1234.${token}&owner=${ownerKey}`);
    socket.receive({ type: 'role', role: 'owner', identity: identity(7) });
    socket.receive({ type: 'state', state: state('2.36.0') });
    await flush();
    await vi.advanceTimersByTimeAsync(300);
    const ids = (n: number) =>
      Array.from({ length: n }, (_, i) => i.toString(16).padStart(64, '0'));
    const layout = (n: number) => ({
      owner: 'peer-a',
      name: 'Big',
      organization: { folders: [], favorites: ids(n), hidden: [], order: ids(n) },
    });
    const sent = () => socket.posted('session').at(-1) as { layouts: unknown[] };
    rooms[0].extras.layouts([layout(10)]);
    await vi.advanceTimersByTimeAsync(250);
    expect(sent().layouts).toHaveLength(1);
    rooms[0].extras.layouts([layout(2000)]);
    await vi.advanceTimersByTimeAsync(250);
    expect(JSON.stringify(layout(2000)).length).toBeGreaterThan(SESSION_INFO_BYTES);
    expect(sent().layouts).toEqual([]);
  });
  it('carries out Disconnect and Replace invite pressed in a view', async () => {
    const { socket } = await hostPage(`#host=1234.${token}&owner=${ownerKey}`);
    socket.receive({ type: 'role', role: 'owner', identity: identity(7) });
    socket.receive({ type: 'state', state: state('2.36.0') });
    await flush();
    rooms[0].guests(['peer-a']);
    socket.receive({ type: 'ownerAction', action: 'disconnect', peer: 'peer-a' });
    expect(rooms[0].disconnected).toEqual(['peer-a']);
    expect(socket.commands('revokePeer')).toHaveLength(1);
    // Someone this room does not have is not disconnected.
    socket.receive({ type: 'ownerAction', action: 'disconnect', peer: 'stranger' });
    expect(rooms[0].disconnected).toEqual(['peer-a']);
    socket.receive({ type: 'ownerAction', action: 'rotate' });
    await flush();
    expect(rooms[0].closed).toEqual({ revoke: true });
    expect(rooms).toHaveLength(2);
    expect(rooms[1].host!.room).toBe(identity(101).room);
    // The companion keeps the new identity for the next owner.
    expect(socket.posted('identity')).toEqual([{ type: 'identity', identity: identity(101) }]);
  });
  it('leaves its room without revoking guests when the window takes over', async () => {
    const { page, socket } = await hostPage(`#host=1234.${token}`);
    socket.receive({ type: 'role', role: 'owner', identity: identity(7) });
    socket.receive({ type: 'state', state: state('2.36.0') });
    await flush();
    rooms[0].guests(['peer-a']);
    socket.receive({ type: 'role', role: 'view', identity: identity(7) });
    await flush();
    expect(rooms[0].closed).toEqual({ revoke: false });
    expect(page.getSnapshot()).toMatchObject({ hosting: 'view', guests: [] });
    // The left room's late reports change nothing a view shows.
    rooms[0].guests(['peer-a']);
    expect(page.getSnapshot().guests).toEqual([]);
    await vi.advanceTimersByTimeAsync(1000);
    socket.receive({ type: 'state', state: state('2.36.0', 2) });
    await flush();
    expect(rooms).toHaveLength(1);
  });
  it('leaves its room when it is demoted on a reconnect, before the companion reports any state', async () => {
    // Review (2.36): the role arrives first on a new socket, while the page still counts as not live.
    const { socket } = await hostPage(`#host=1234.${token}`);
    socket.receive({ type: 'role', role: 'owner', identity: identity(7) });
    socket.receive({ type: 'state', state: state('2.36.0') });
    await flush();
    socket.close();
    await vi.advanceTimersByTimeAsync(2600);
    const again = FakeSocket.last;
    expect(again).not.toBe(socket);
    again.onopen?.();
    again.receive({ type: 'role', role: 'view', identity: null });
    await flush();
    expect(rooms[0].closed).toEqual({ revoke: false });
    expect(rooms).toHaveLength(1);
  });
  it('replaces the invitation once when two requests overlap', async () => {
    const { socket } = await hostPage(`#host=1234.${token}&owner=${ownerKey}`);
    socket.receive({ type: 'role', role: 'owner', identity: identity(7) });
    socket.receive({ type: 'state', state: state('2.36.0') });
    await flush();
    socket.receive({ type: 'ownerAction', action: 'rotate' });
    socket.receive({ type: 'ownerAction', action: 'rotate' });
    await flush();
    // One new room; a second running room would keep its own working invitation.
    expect(rooms).toHaveLength(2);
    expect(rooms[0].closed).toEqual({ revoke: true });
  });
});

describe('session view (2.36)', () => {
  it('joins no room and shows what the owner reports', async () => {
    const { page, socket } = await hostPage(`#host=1234.${token}`);
    socket.receive({ type: 'role', role: 'view', identity: identity(7) });
    socket.receive({ type: 'state', state: state('2.36.0') });
    await flush();
    expect(rooms).toEqual([]);
    expect(socket.commands('beginSession')).toEqual([]);
    const person = { id: crypto.randomUUID(), name: 'Ann' };
    socket.receive({
      type: 'session',
      invite: 'https://site.test/#join=abc',
      guests: ['peer-a'],
      people: { 'peer-a': person },
      layouts: [],
      ownerWindow: true,
    });
    expect(page.getSnapshot()).toMatchObject({
      mode: 'host',
      status: 'Host ready',
      hosting: 'view',
      invite: 'https://site.test/#join=abc',
      guests: ['peer-a'],
      people: { 'peer-a': person },
      ownerWindow: true,
    });
    // Info it cannot read is skipped; the connection stays.
    socket.receive({ type: 'session', invite: 5 });
    expect(socket.readyState).toBe(FakeSocket.OPEN);
    expect(page.getSnapshot().guests).toEqual(['peer-a']);
  });
  it('asks the owner to disconnect a guest or replace the invite', async () => {
    const { page, socket } = await hostPage(`#host=1234.${token}`);
    socket.receive({ type: 'role', role: 'view', identity: null });
    socket.receive({ type: 'state', state: state('2.36.0') });
    await flush();
    page.disconnect('peer-a');
    await page.rotate();
    expect(socket.posted('ownerAction')).toEqual([
      { type: 'ownerAction', action: 'disconnect', peer: 'peer-a' },
      { type: 'ownerAction', action: 'rotate' },
    ]);
    // The owner revokes; the view itself sends no command for it.
    expect(socket.commands('revokePeer')).toEqual([]);
    expect(rooms).toEqual([]);
  });
  it('hosts with the kept identity when promoted', async () => {
    const { page, socket } = await hostPage(`#host=1234.${token}`);
    socket.receive({ type: 'role', role: 'view', identity: identity(7) });
    socket.receive({ type: 'state', state: state('2.36.0') });
    await flush();
    socket.receive({ type: 'role', role: 'owner', identity: identity(7) });
    await flush();
    expect(rooms).toHaveLength(1);
    expect(rooms[0].host!.room).toBe(identity(7).room);
    expect(createIdentity).not.toHaveBeenCalled();
    expect(page.getSnapshot().hosting).toBe('tab');
  });
});

describe('older companions (before 2.36)', () => {
  it('host exactly as before: no role, so this tab runs the room and sends nothing new', async () => {
    const { page, socket } = await hostPage(`#host=1234.${token}`);
    socket.receive({ type: 'state', state: state('2.35.0') });
    await flush();
    expect(rooms).toHaveLength(1);
    expect(createIdentity).toHaveBeenCalledTimes(1);
    // The identity is kept in this tab for a reload, as before.
    expect(JSON.parse(sessionStorage.getItem('soundpad-identity-' + token)!)).toEqual(
      identity(101),
    );
    rooms[0].guests(['peer-a']);
    await vi.advanceTimersByTimeAsync(1000);
    // An older companion reads every message as a command: nothing else may reach it.
    expect(socket.sent.filter((f) => !f.command && !f.token)).toEqual([]);
    page.disconnect('peer-a');
    expect(rooms[0].disconnected).toEqual(['peer-a']);
    expect(page.getSnapshot()).toMatchObject({ hosting: 'tab', guests: ['peer-a'] });
    // A reload reuses this tab's identity.
    const reload = await hostPage('');
    reload.socket.receive({ type: 'state', state: state('2.35.0') });
    await flush();
    expect(rooms[1].host!.room).toBe(identity(101).room);
    expect(createIdentity).toHaveBeenCalledTimes(1);
  });
  it('refuses desktop messages on a bridge whose companion sent no role', () => {
    const bridge = new LocalBridge(
      { port: 1, token },
      () => {},
      () => {},
    );
    const socket = FakeSocket.last;
    socket.onopen?.();
    expect(bridge.post({ type: 'ownerAction', action: 'rotate' })).toBe(false);
    expect(socket.sent).toHaveLength(1); // the hello only
    bridge.close();
  });
});

describe('session settings (2.37)', () => {
  const kept = {
    ...DEFAULT_POLICY,
    admission: 'ask',
    capacity: 3,
    overrides: { '11111111-1111-4111-8111-111111111111': { remotePlayback: false } },
  };
  it('starts the room with the settings the companion kept, and sends every change back', async () => {
    const { page, socket } = await hostPage(`#host=1234.${token}&owner=${ownerKey}`);
    socket.receive({ type: 'role', role: 'owner', identity: identity(7), settings: kept });
    socket.receive({ type: 'state', state: state('2.37.0') });
    await flush();
    expect(rooms[0].options.policy).toEqual(kept);
    expect(page.getSnapshot().policy).toEqual(kept);
    const changed = { ...kept, extra: 1, admitted: ['22222222-2222-4222-8222-222222222222'] };
    rooms[0].options.policyChanged(changed);
    expect(page.getSnapshot().policy).toEqual(changed);
    await vi.advanceTimersByTimeAsync(300);
    expect(socket.posted('sessionSettings')).toEqual([
      { type: 'sessionSettings', settings: changed },
    ]);
    // A reloaded owner gets them back in its role, and a new room starts with them.
    const reload = await hostPage(`#host=1234.${token}&owner=${ownerKey}`);
    reload.socket.receive({
      type: 'role',
      role: 'owner',
      identity: identity(7),
      settings: changed,
    });
    reload.socket.receive({ type: 'state', state: state('2.37.0') });
    await flush();
    expect(rooms[1].options.policy).toEqual(changed);
  });
  it('keeps the settings in the page only with an older companion, and reads bad ones as defaults', async () => {
    const { page, socket } = await hostPage(`#host=1234.${token}`);
    socket.receive({
      type: 'role',
      role: 'owner',
      identity: identity(7),
      settings: { capacity: 99 },
    });
    socket.receive({ type: 'state', state: state('2.36.0') });
    await flush();
    expect(rooms[0].options.policy).toEqual(DEFAULT_POLICY);
    rooms[0].options.policyChanged(kept);
    await vi.advanceTimersByTimeAsync(300);
    expect(page.getSnapshot().policy).toEqual(kept);
    expect(socket.posted('sessionSettings')).toEqual([]);
  });
  it('shows a view the owner’s waiting guests and settings', async () => {
    const { page, socket } = await hostPage(`#host=1234.${token}`);
    socket.receive({ type: 'role', role: 'view', identity: null });
    socket.receive({ type: 'state', state: state('2.37.0') });
    await flush();
    const request = {
      id: '33333333-3333-4333-8333-333333333333',
      name: 'Ann',
      kind: 'admit',
      at: 1,
      expiresAt: 2,
    };
    socket.receive({
      type: 'session',
      invite: 'https://site.test/#join=x',
      guests: ['peer-a'],
      people: {},
      layouts: [],
      control: { requests: [request], policy: kept, links: { 'peer-a': 'relay' } },
    });
    expect(page.getSnapshot().requests).toEqual([request]);
    expect(page.getSnapshot().policy).toEqual(kept);
    expect(page.getSnapshot().links).toEqual({ 'peer-a': 'relay' });
  });
});

describe('host copy (2.36)', () => {
  it('says what keeps the session running, and keeps the old copy for older companions', () => {
    expect(supports('2.35.0', 'desktopSession')).toBe(false);
    expect(supports('2.36.0', 'desktopSession')).toBe(true);
    expect(hostCopy('view', '2.35.0').place).toBe('tab');
    expect(hostCopy('tab', '2.36.0').sidebar).toBe('Keep this tab open to host.');
    expect(hostCopy('window', '2.36.0').sidebar).toBe('This window keeps your session running.');
    expect(hostCopy('view', '2.36.0').note).toBe(VIEW_NOTE);
    expect(VIEW_NOTE).toBe(
      'Hosting runs in the Soundpad Web window. This tab is a view; closing it does not affect guests.',
    );
    expect(hostCopy('view', '2.36.0', false).note).toBe(TAB_VIEW_NOTE);
  });
});
