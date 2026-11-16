/**
 * 2.45 (WP2): the one Recover previous session / Discard prompt (what it lists, and when it shows), the
 * guest's storage actions op, and O02 (a new host's setup guide starts with guest capture on).
 */
import { describe, expect, it } from 'vitest';
import {
  recoveryPending,
  recoveryRows,
  sessionRecoverySchema,
} from '../../web/src/components/RecoverSession';
import { shouldPreselectGuestCapture } from '../../web/src/components/setup/guide-state';
import { EMPTY, command, isGuestAllowed, type Snapshot } from '../../web/src/protocol';
import { supports } from '../../web/src/version';

const MB = 1024 * 1024;
const withHistory = (recoveredMs: number, tempRecovery?: number): Snapshot =>
  ({
    ...EMPTY,
    companionVersion: '2.45.0',
    tempRecovery,
    mimic: { history: { recoveredMs } },
  }) as unknown as Snapshot;

describe('Recover previous session', () => {
  it('is pending while the snapshot reports recovered Temp or history', () => {
    expect(recoveryPending(withHistory(0))).toBe(false);
    expect(recoveryPending(withHistory(0, 2))).toBe(true);
    expect(recoveryPending(withHistory(90000))).toBe(true);
  });

  it('lists Temp, history and drafts with their sizes in one prompt', () => {
    const info = sessionRecoverySchema.parse({
      pending: true,
      temp: { count: 2, ms: null, bytes: 3 * MB },
      history: { count: 1, ms: 125000, bytes: 4 * MB },
      drafts: { count: 1, ms: 2500, bytes: 80000 },
    });
    expect(recoveryRows(info, withHistory(125000, 2)).map((r) => r.text)).toEqual([
      '2 clips in Temp · 3.0 MB',
      '2 min 5 s of recording history · 4.0 MB',
      'One draft of yours (3 s) · 0.1 MB',
    ]);
  });

  it('shows what the snapshot knows before the companion answers', () => {
    const rows = recoveryRows(null, withHistory(60000, 1)).map((r) => r.key);
    expect(rows).toEqual(['temp', 'history']);
  });

  it('accepts the companion answer with parts left out (null)', () => {
    const info = sessionRecoverySchema.parse({
      pending: false,
      temp: null,
      history: null,
      drafts: null,
    });
    expect(recoveryRows(info, withHistory(0))).toEqual([]);
  });

  it('is gated: older companions keep the separate Temp prompt and history notice', () => {
    expect(supports('2.44.0', 'sessionRecovery')).toBe(false);
    expect(supports('2.45.0', 'sessionRecovery')).toBe(true);
    expect(supports('2.44.0', 'budgetActions')).toBe(false);
  });
});

describe('guest storage actions and recovery rights', () => {
  it('a guest may ask for their storage actions but never decide about the previous session', () => {
    const state = withHistory(0);
    expect(isGuestAllowed(command('budgetActions'), state)).toBe(true);
    expect(isGuestAllowed(command('sessionRecover', { action: 'recover' }), state)).toBe(false);
    expect(isGuestAllowed(command('sessionRecover', { action: 'status' }), state)).toBe(false);
  });
});

describe('O02: guest capture in the first-run setup guide', () => {
  const fresh = {
    setupDone: false,
    permissions: { ...EMPTY.permissions, guestMimic: false },
    companionVersion: '2.45.0',
  } as Pick<Snapshot, 'setupDone' | 'permissions' | 'companionVersion'>;
  const newHost = { existing: false, done: false };

  it('is ticked once for a brand-new host', () => {
    expect(shouldPreselectGuestCapture(newHost, fresh, false)).toBe(true);
    // Once only: a host who unticked it keeps it off.
    expect(shouldPreselectGuestCapture(newHost, fresh, true)).toBe(false);
  });

  it('never changes an existing host, a finished setup or a setting already on', () => {
    expect(shouldPreselectGuestCapture({ existing: true, done: false }, fresh, false)).toBe(false);
    expect(shouldPreselectGuestCapture({ existing: false, done: true }, fresh, false)).toBe(false);
    expect(shouldPreselectGuestCapture(newHost, { ...fresh, setupDone: true }, false)).toBe(false);
    expect(shouldPreselectGuestCapture(newHost, { ...fresh, setupDone: undefined }, false)).toBe(
      false,
    );
    expect(
      shouldPreselectGuestCapture(
        newHost,
        { ...fresh, permissions: { ...fresh.permissions, guestMimic: true } },
        false,
      ),
    ).toBe(false);
  });

  it('keeps the stored default off (a snapshot without the field is not guest capture)', () => {
    expect(EMPTY.permissions.guestMimic ?? false).toBe(false);
  });
});
