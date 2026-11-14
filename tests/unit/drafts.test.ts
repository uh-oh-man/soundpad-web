import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  BAND_COLORS,
  bandColor,
  bandLabel,
  byRecent,
  draftLabel,
  draftListSchema,
  draftState,
  formatMb,
  guestBudgetSchema,
  hostBudgetSchema,
  newBands,
  sameRange,
  type Draft,
  type DraftList,
} from '../../web/src/mimic/drafts';
import { forgetSections, recall, reloadSections, remember } from '../../web/src/sections';
import { commandSchema, EMPTY, isGuestAllowed, parseSnapshot } from '../../web/src/protocol';
import { siteRelaysDrafts, supports, missingFeatures } from '../../web/src/version';
import { effectivePermissions, PERMISSION_KEYS } from '../../web/src/network/policy';
import { LocalBridge } from '../../web/src/network/local';

const RATE = 16000;
const id = (n: number) => n.toString(16).padStart(32, '0');
const draft = (n: number, updated: number, extra: Partial<Draft> = {}): Draft => ({
  id: id(n),
  startFrame: n * RATE,
  endFrame: n * RATE + RATE * 2,
  name: null,
  createdUtcMs: updated - 1000,
  updatedUtcMs: updated,
  sessionId: 'session-1',
  inTemp: false,
  ...extra,
});
const list = (drafts: Draft[], others: DraftList['others'] = []): DraftList => ({
  drafts,
  others,
  sessionId: 'session-1',
});

describe('draft UI state (2.40)', () => {
  it('shows this person’s drafts newest first, the open one, and notices when it is gone', () => {
    const replies = list(
      [draft(1, 100), draft(2, 300), draft(3, 200)],
      [
        {
          id: id(9),
          ownerName: 'Alex',
          tag: 'abcd1234',
          startFrame: 0,
          endFrame: RATE,
          name: null,
        },
      ],
    );
    const parsed = draftListSchema.parse(replies);
    const state = draftState(parsed, 'session-1', id(3));
    expect(state.mine.map((d) => d.id)).toEqual([id(2), id(3), id(1)]);
    expect(state.open?.id).toBe(id(3));
    expect(state.others).toHaveLength(1);
    expect(state.gone).toBe(false);
    // Kept and closed elsewhere: the window closes it too.
    expect(draftState(list([draft(1, 100)]), 'session-1', id(3)).gone).toBe(true);
    // A list for another (discarded) history, or none yet: nothing open, nothing "gone" either.
    expect(draftState(list([draft(3, 1)]), 'session-2', id(3))).toMatchObject({
      listed: false,
      open: null,
      gone: false,
    });
    expect(draftState(null, 'session-1', id(3)).gone).toBe(false);
    // An older companion (drafts off): nothing at all.
    expect(draftState(parsed, 'session-1', id(3), false).mine).toEqual([]);
  });

  it('labels drafts and other people’s bands, and finds new captures for a quiet notice', () => {
    expect(draftLabel(draft(1, 0, { name: 'Hello there' }), RATE)).toBe('Hello there · 2.0 s');
    expect(draftLabel(draft(1, Date.UTC(2026, 0, 1, 12)), RATE)).toMatch(/^Draft at .+ · 2\.0 s$/);
    expect(bandLabel({ ownerName: 'Alex' })).toBe('Alex’s selection');
    expect(BAND_COLORS).toContain(bandColor('abcd1234'));
    expect(bandColor('abcd1234')).toBe(bandColor('abcd1234'));
    const bands = draftListSchema.parse(
      list(
        [],
        [1, 2].map((n) => ({
          id: id(n),
          ownerName: 'Sam',
          tag: 't' + n,
          startFrame: 0,
          endFrame: 1,
          name: null,
        })),
      ),
    ).others;
    expect(newBands(new Set([id(1)]), bands).map((b) => b.id)).toEqual([id(2)]);
    expect(byRecent([draft(1, 1), draft(2, 2)])[0].id).toBe(id(2));
    expect(sameRange({ startFrame: 1, endFrame: 2 }, { startFrame: 1, endFrame: 2 })).toBe(true);
    expect(sameRange({ startFrame: 1, endFrame: 2 }, { startFrame: 1, endFrame: 3 })).toBe(false);
    expect(sameRange(null, { startFrame: 1, endFrame: 2 })).toBe(false);
  });
});

describe('per-section memory (2.40)', () => {
  const stored = new Map<string, string>();
  beforeEach(() => {
    stored.clear();
    vi.stubGlobal('sessionStorage', {
      getItem: (key: string) => stored.get(key) ?? null,
      setItem: (key: string, value: string) => void stored.set(key, value),
      removeItem: (key: string) => void stored.delete(key),
    });
    forgetSections();
  });
  afterEach(() => vi.unstubAllGlobals());

  it('remembers each section’s open item, draft and view position across a reload', () => {
    remember('mimic', { sessionId: 's1', draftId: id(4), follow: false, viewEnd: 48000 });
    remember('temp', { open: true, find: 'hello' });
    remember('mimic', { viewEnd: 64000 }); // merged, the rest stays
    reloadSections();
    expect(recall('mimic')).toEqual({
      sessionId: 's1',
      draftId: id(4),
      follow: false,
      viewEnd: 64000,
    });
    expect(recall('temp')).toEqual({ open: true, find: 'hello' });
    // A copy: changing it changes nothing stored.
    const copy = recall('temp');
    copy.find = 'other';
    expect(recall('temp').find).toBe('hello');
  });

  it('works without storage (private windows) and with damaged storage', () => {
    vi.stubGlobal('sessionStorage', {
      getItem: () => {
        throw Error('blocked');
      },
      setItem: () => {
        throw Error('blocked');
      },
      removeItem: () => {},
    });
    reloadSections();
    remember('mimic', { draftId: id(1) });
    expect(recall('mimic').draftId).toBe(id(1));
    vi.stubGlobal('sessionStorage', {
      getItem: () => '{not json',
      setItem: () => {},
      removeItem: () => {},
    });
    reloadSections();
    expect(recall('mimic')).toEqual({});
  });
});

describe('storage budgets (2.40)', () => {
  it('formats usage and reads the host’s and a guest’s status', () => {
    expect(formatMb(0)).toBe('0.0 MB');
    expect(formatMb(1572864, 104857600)).toBe('1.5 of 100 MB');
    const usage = {
      id: 'alice',
      name: 'Alice',
      tempBytes: 5,
      tempLimitBytes: 10,
      keptBytes: 9,
      keptLimitBytes: 9,
      paused: true,
      waiting: 'kept',
    };
    const host = hostBudgetSchema.parse({
      settings: {
        guestKeep: true,
        destination: 'D:\\Sounds',
        categoryId: null,
        categoryName: null,
        tempMb: 200,
        keptMb: 100,
        budgetMode: 'total',
        format: 1,
      },
      guests: [usage],
      requests: [{ guestId: 'alice', name: 'Alice', kind: 'kept', neededBytes: 2, at: 1 }],
    });
    expect(host.requests[0].kind).toBe('kept');
    const guest = guestBudgetSchema.parse({ guestKeep: true, own: usage, budgetMode: 'session' });
    expect(guest.own.paused).toBe(true);
    expect(() =>
      guestBudgetSchema.parse({ guestKeep: true, own: { ...usage, waiting: 'x' } }),
    ).toThrow();
  });
});

describe('2.40 commands and permissions', () => {
  const permissions = {
    remotePlayback: true,
    guestCounter: false,
    guestMimic: true,
    guestKeep: true,
  };
  const s = (p: Partial<typeof permissions> = {}) => ({
    ...EMPTY,
    permissions: { ...permissions, ...p },
  });
  const c = (op: string, fields: Record<string, unknown> = {}) =>
    commandSchema.parse({ v: 2, id: crypto.randomUUID(), at: Date.now(), op, ...fields });

  it('accepts the new ops and fields', () => {
    expect(c('draftCreate', { action: 'phrase' }).action).toBe('phrase');
    expect(
      c('draftUpdate', { draftId: id(1), startFrame: 1, endFrame: 2, name: 'x' }).draftId,
    ).toBe(id(1));
    expect(
      c('keepSettings', { tempMb: 10, keptMb: 20, budgetMode: 'total', guestKeep: false })
        .budgetMode,
    ).toBe('total');
    expect(c('budgetGrant', { guestId: 'alice', action: 'allow' }).action).toBe('allow');
    expect(c('tempRecover', { action: 'discard' }).action).toBe('discard');
    expect(() => c('draftUpdate', { draftId: '../etc' })).toThrow();
    expect(() => c('keepSettings', { budgetMode: 'forever' })).toThrow();
    expect(c('permissions', { permissions: { ...permissions } }).permissions?.guestKeep).toBe(true);
  });

  it('lets guests use drafts and preview with Mimic, Keep with guestKeep and never a path', () => {
    for (const op of [
      'draftCreate',
      'draftList',
      'draftUpdate',
      'draftDiscard',
      'draftCopy',
      'draftKeep',
      'mimicPreview',
    ]) {
      expect(isGuestAllowed(c(op), s())).toBe(true);
      expect(isGuestAllowed(c(op), s({ guestMimic: false }))).toBe(false);
    }
    expect(isGuestAllowed(c('draftPlay'), s())).toBe(true);
    expect(isGuestAllowed(c('draftPlay'), s({ remotePlayback: false }))).toBe(false);
    expect(isGuestAllowed(c('keepAudio', { assetId: crypto.randomUUID() }), s())).toBe(true);
    expect(
      isGuestAllowed(c('keepAudio', { assetId: crypto.randomUUID() }), s({ guestKeep: false })),
    ).toBe(false);
    expect(isGuestAllowed(c('keepAudio', { destinationDirectory: 'C:\\Users' }), s())).toBe(false);
    expect(isGuestAllowed(c('keepAudio', { categoryId: 3, categoryName: 'x' }), s())).toBe(false);
    expect(isGuestAllowed(c('budgetStatus'), s({ guestMimic: false, guestKeep: false }))).toBe(
      true,
    );
    for (const op of ['keepSettings', 'budgetGrant', 'tempRecover'])
      expect(isGuestAllowed(c(op), s())).toBe(false);
    // A per-guest override can switch Keep off for one guest only.
    expect(PERMISSION_KEYS).toContain('guestKeep');
    expect(effectivePermissions(s().permissions, { guestKeep: false }).guestKeep).toBe(false);
  });

  it('reads the 2.40 snapshot fields and gates the features', () => {
    const snapshot = parseSnapshot({
      ...EMPTY,
      drafts: 3,
      budgets: 2,
      tempRecovery: 4,
      permissions: { ...permissions },
    });
    expect([
      snapshot.drafts,
      snapshot.budgets,
      snapshot.tempRecovery,
      snapshot.permissions.guestKeep,
    ]).toEqual([3, 2, 4, true]);
    expect(parseSnapshot(EMPTY).tempRecovery).toBeUndefined();
    expect(supports('2.40.0', 'drafts')).toBe(true);
    expect(supports('2.39.0', 'drafts')).toBe(false);
    expect(siteRelaysDrafts('2.40.1')).toBe(true);
    expect(siteRelaysDrafts('2.39.9')).toBe(false);
    expect(siteRelaysDrafts('')).toBe(false);
    expect(missingFeatures('2.39.0').join(' ')).toMatch(/drafts/);
  });
});

describe('the quit prompt (2.40)', () => {
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
      this.sent.push(JSON.parse(text));
    }
    close() {
      this.readyState = 3;
      this.onclose?.();
    }
  }
  afterEach(() => vi.unstubAllGlobals());

  it('says it can show the prompt, passes it on, and answers it', () => {
    vi.stubGlobal('WebSocket', FakeSocket);
    const prompts: unknown[] = [];
    const bridge = new LocalBridge(
      { port: 1, token: 'a'.repeat(64) },
      () => {},
      () => {},
      undefined,
      {
        ownerKey: 'b'.repeat(64),
        role: () => {},
        session: () => {},
        ownerAction: () => {},
        quitPrompt: (p) => prompts.push(p),
      },
    );
    const socket = FakeSocket.last;
    socket.onopen?.();
    expect(socket.sent[0]).toMatchObject({
      protocol: 3,
      quitPrompt: true,
      ownerKey: 'b'.repeat(64),
    });
    socket.onmessage?.({ data: JSON.stringify({ type: 'role', role: 'owner' }) });
    socket.onmessage?.({
      data: JSON.stringify({ type: 'quitPrompt', id: 'c'.repeat(32), clips: 3 }),
    });
    // A malformed one is skipped without dropping the connection.
    socket.onmessage?.({ data: JSON.stringify({ type: 'quitPrompt', id: 'nope', clips: 3 }) });
    expect(prompts).toEqual([{ id: 'c'.repeat(32), clips: 3 }]);
    expect(socket.readyState).toBe(FakeSocket.OPEN);
    expect(
      bridge.post({ type: 'quitAnswer', id: 'c'.repeat(32), action: 'keep', assetIds: ['x'] }),
    ).toBe(true);
    expect(socket.sent.at(-1)).toEqual({
      type: 'quitAnswer',
      id: 'c'.repeat(32),
      action: 'keep',
      assetIds: ['x'],
    });
    bridge.close();
  });
});

describe('guest uploads (2.40 review)', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('send the guest’s participant id, so a reload gives no fresh storage budget', async () => {
    const calls: { url: string; headers: Record<string, string> }[] = [];
    vi.stubGlobal(
      'WebSocket',
      class {
        readyState = 0;
        close() {}
        send() {}
      },
    );
    vi.stubGlobal('fetch', async (url: string, init: { headers: Record<string, string> }) => {
      calls.push({ url, headers: init.headers });
      return new Response(JSON.stringify({ type: 'ack', ok: true, data: { assetId: 'a' } }));
    });
    const bridge = new LocalBridge(
      { port: 1, token: 'a'.repeat(64) },
      () => {},
      () => {},
    );
    await bridge.upload(new Uint8Array([1]), 'a.wav', 'Ann', false, 'peer-1', 'ann-id');
    // Without one (the host's own upload, or an older companion whose CORS check would refuse the header): not sent.
    await bridge.upload(new Uint8Array([1]), 'b.wav', 'Host', true);
    expect(calls[0].headers).toMatchObject({
      'X-Audio-Actor': 'guest',
      'X-Audio-Owner': 'peer-1',
      'X-Audio-Guest': 'ann-id',
    });
    expect(calls[1].headers).not.toHaveProperty('X-Audio-Guest');
    bridge.close();
  });
});
