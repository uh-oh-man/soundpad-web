import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { LocalBridge, type HostRole } from '../../web/src/network/local';
import { EMPTY, parseSnapshot, type UpdateSnapshot } from '../../web/src/protocol';
import {
  chipShown,
  reasonText,
  shouldOffer,
  updateBodies,
  updateCommand,
  updateCopy,
  UPDATING_STATUS,
} from '../../web/src/updates';

const at = (
  state: UpdateSnapshot['state'],
  more: Partial<UpdateSnapshot> = {},
): UpdateSnapshot => ({
  state,
  current: '2.44.0',
  ...more,
});

describe('update states in words (2.44)', () => {
  it('maps every state to a line, and only offers an install when there is one to take', () => {
    expect(updateCopy(at('idle')).line).toBe('Soundpad Web 2.44.0 is the newest version.');
    expect(updateCopy(at('checking'))).toMatchObject({ busy: true, installable: false });
    expect(updateCopy(at('available', { latest: '2.44.1' }))).toMatchObject({
      line: 'Soundpad Web 2.44.1 is available.',
      installable: true,
      busy: false,
    });
    const downloading = updateCopy(
      at('downloading', { latest: '2.44.1', bytes: 20 * 1048576, total: 80 * 1048576 }),
    );
    expect(downloading).toMatchObject({ busy: true, installable: false });
    expect(downloading.line).toBe('Downloading v2.44.1: 20 MB of 80 MB');
    expect(updateCopy(at('verifying', { latest: '2.44.1' })).line).toContain(
      'hash published with the release',
    );
    expect(updateCopy(at('ready', { latest: '2.44.1' }))).toMatchObject({ installable: true });
    expect(updateCopy(at('installing', { latest: '2.44.1' }))).toMatchObject({
      busy: true,
      installable: false,
    });
    // Failures say why and offer the download by hand; a blocked EXE is not offered again.
    expect(updateCopy(at('failed', { latest: '2.44.1', reason: 'hashMismatch' }))).toMatchObject({
      downloadYourself: true,
      installable: true,
    });
    expect(updateCopy(at('failed', { latest: '2.44.1', reason: 'blocked' })).installable).toBe(
      false,
    );
    expect(updateCopy(at('unsupported', { reason: 'noRepository' })).line).toBe(
      "This build doesn't update itself.",
    );
    expect(updateCopy(at('unsupported', { latest: '2.44.1', reason: 'notNtfs' })).line).toBe(
      'Soundpad Web 2.44.1 is available. Soundpad Web can update itself only from a folder on a local NTFS drive.',
    );
    // Nothing promises how long it takes, and nothing claims a signature.
    for (const state of [
      'idle',
      'checking',
      'available',
      'downloading',
      'verifying',
      'ready',
      'installing',
      'failed',
      'unsupported',
    ] as const)
      expect(updateCopy(at(state, { latest: '2.44.1' })).line).not.toMatch(
        /second|minute|signed|verified publisher/i,
      );
  });

  it('words every reason the companion gives', () => {
    expect(reasonText('rateLimited')).toContain("GitHub's limit");
    expect(reasonText('blocked')).toContain('Windows or an antivirus blocked it');
    expect(reasonText('notWritable')).toContain('cannot write to its own folder');
    expect(reasonText('digestMismatch')).toContain('does not match the hash');
    expect(reasonText('busy', 'The host is restoring the library.')).toBe(
      'The host is restoring the library.',
    );
    expect(reasonText(undefined, 'Something specific.')).toBe('Something specific.');
  });
});

describe('the launch offer (2.44)', () => {
  it('shows once per launch, to the host only, while an update is there to take', () => {
    const offer = at('available', { latest: '2.44.1' });
    expect(shouldOffer(offer, true, false, null)).toBe(true);
    expect(shouldOffer(offer, false, false, null)).toBe(false); // guests
    expect(shouldOffer(offer, true, true, null)).toBe(false); // the example
    expect(shouldOffer({ ...offer, offered: true }, true, false, null)).toBe(false); // Later, anywhere
    expect(shouldOffer(offer, true, false, '2.44.1')).toBe(false); // Later in this tab
    expect(shouldOffer(offer, true, false, '2.44.0')).toBe(true); // a newer one than the one put off
    expect(shouldOffer(at('ready', { latest: '2.44.1' }), true, false, null)).toBe(true);
    for (const state of [
      'idle',
      'checking',
      'downloading',
      'installing',
      'failed',
      'unsupported',
    ] as const)
      expect(shouldOffer(at(state, { latest: '2.44.1' }), true, false, null)).toBe(false);
    expect(shouldOffer(undefined, true, false, null)).toBe(false);
  });

  it('keeps Update in the header after Later', () => {
    const later = at('available', { latest: '2.44.1', offered: true });
    expect(chipShown(later, true, false)).toBe(true);
    expect(chipShown({ ...later, offered: undefined }, true, false)).toBe(false); // the offer shows instead
    expect(chipShown({ ...later, state: 'ready' }, true, false)).toBe(true);
    expect(chipShown({ ...later, state: 'installing' }, true, false)).toBe(false);
    expect(chipShown(later, false, false)).toBe(false);
  });
});

describe('update bodies (2.44)', () => {
  it('checks each body before it is sent', () => {
    expect(updateCommand('updateInstall', { version: '2.44.1', confirm: true }).body).toEqual({
      version: '2.44.1',
      confirm: true,
    });
    expect(() =>
      updateCommand('updateInstall', { version: '2.44.1', confirm: false as unknown as true }),
    ).toThrow();
    expect(() => updateCommand('updateDownload', { version: 'v2.44.1' })).toThrow();
    expect(updateBodies.updateSettings.safeParse({ checkAtLaunch: true, extra: 1 }).success).toBe(
      false,
    );
    expect(updateBodies.updateStatus.safeParse({}).success).toBe(true);
  });

  it('reads the snapshot field and leaves it out for an older companion', () => {
    const state = parseSnapshot({
      ...EMPTY,
      update: { state: 'available', current: '2.44.0', latest: '2.44.1', offered: true },
    });
    expect(state.update).toEqual({
      state: 'available',
      current: '2.44.0',
      latest: '2.44.1',
      offered: true,
    });
    expect(parseSnapshot({ ...EMPTY }).update).toBeUndefined();
  });
});

/** The companion's socket, answered by hand (as local.test.ts). No real socket is opened. */
class FakeSocket {
  static OPEN = 1;
  static all: FakeSocket[] = [];
  readyState = FakeSocket.OPEN;
  sent: Record<string, unknown>[] = [];
  onopen?: () => void;
  onmessage?: (e: { data: string }) => void;
  onerror?: () => void;
  onclose?: () => void;
  constructor(public url: string) {
    FakeSocket.all.push(this);
  }
  send(text: string) {
    this.sent.push(JSON.parse(text));
  }
  close() {
    this.readyState = 3;
    this.onclose?.();
  }
  message(value: unknown) {
    this.onmessage?.({ data: JSON.stringify(value) });
  }
}

describe('the host page while the companion updates (2.44)', () => {
  let statuses: string[];
  let states: number[];
  let roles: HostRole[];
  let bridge: LocalBridge;
  beforeEach(() => {
    vi.useFakeTimers();
    FakeSocket.all = [];
    vi.stubGlobal('WebSocket', FakeSocket);
    statuses = [];
    states = [];
    roles = [];
    bridge = new LocalBridge(
      { port: 4321, token: 'a'.repeat(64) },
      (s) => states.push(s.revision),
      (s) => statuses.push(s),
      undefined,
      { role: (r) => roles.push(r), session: () => {}, ownerAction: () => {} },
    );
    FakeSocket.all[0].onopen?.();
  });
  afterEach(() => {
    bridge.close();
    vi.unstubAllGlobals();
    vi.useRealTimers();
  });

  it('says it understands "updating", and then keeps trying the same port and token sooner', () => {
    const first = FakeSocket.all[0];
    expect(first.sent[0]).toMatchObject({ token: 'a'.repeat(64), protocol: 3, updates: true });
    first.message({ type: 'role', role: 'owner', identity: null });
    first.message({ type: 'state', state: { ...EMPTY, revision: 900 } });
    first.message({ type: 'updating', version: '2.44.1' });
    expect(statuses.at(-1)).toBe(UPDATING_STATUS);
    first.close();
    expect(statuses.at(-1)).toBe(UPDATING_STATUS);
    vi.advanceTimersByTime(1000);
    const second = FakeSocket.all[1];
    expect(second.url).toBe('ws://127.0.0.1:4321/bridge');
    second.onopen?.();
    expect(second.sent[0]).toMatchObject({ token: 'a'.repeat(64) });
    // The new process counts its revisions from the start again: its states are taken.
    second.message({ type: 'role', role: 'owner', identity: null, ended: true });
    second.message({ type: 'state', state: { ...EMPTY, revision: 3 } });
    expect(states).toEqual([900, 3]);
    expect(roles.at(-1)).toMatchObject({ role: 'owner', ended: true });
    // Back to the usual wording for a later, ordinary disconnect.
    vi.advanceTimersByTime(181000);
    second.close();
    expect(statuses.at(-1)).toBe('Companion disconnected. Reconnecting…');
  });

  it('still treats an unknown message as a mismatch, as before', () => {
    const first = FakeSocket.all[0];
    first.message({ type: 'state', state: EMPTY });
    first.message({ type: 'surprise' });
    expect(statuses).toContain('Companion protocol mismatch. Update both components.');
  });

  it('ignores a malformed "updating" message without dropping the connection', () => {
    const first = FakeSocket.all[0];
    first.message({ type: 'state', state: EMPTY });
    first.message({ type: 'updating', version: { not: 'a string' } });
    expect(statuses).not.toContain(UPDATING_STATUS);
    expect(first.readyState).toBe(FakeSocket.OPEN);
  });
});
