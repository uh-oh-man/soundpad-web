import { describe, it, expect } from 'vitest';
import { ago, normalizeSelection } from '../../web/src/components/MimicPanel';
import {
  command,
  commandSchema,
  EMPTY,
  isGuestAllowed,
  snapshotSchema,
} from '../../web/src/protocol';

describe('Mimic selection', () => {
  it('orders, clamps and rejects too-short ranges', () => {
    expect(normalizeSelection(9000.4, 2000.6, 30000)).toEqual({ from: 2001, to: 9000 });
    expect(normalizeSelection(-50, 40000, 30000)).toEqual({ from: 0, to: 30000 });
    expect(normalizeSelection(1000, 1100, 30000)).toBeNull();
  });
  it('labels positions as seconds before now', () => {
    expect(ago(25800, 30000)).toBe('−4.2 s');
    expect(ago(30000, 30000)).toBe('now');
  });
});

describe('Mimic protocol', () => {
  it('only lets guests peek and clip when the host enabled guest Mimic', () => {
    const off = EMPTY;
    const on = { ...EMPTY, permissions: { ...EMPTY.permissions, guestMimic: true } };
    for (const op of ['mimicPeek', 'mimicClip'] as const) {
      expect(isGuestAllowed(command(op), off)).toBe(false);
      expect(isGuestAllowed(command(op), on)).toBe(true);
    }
    for (const op of ['mimicSources', 'mimicStart', 'mimicStop'] as const)
      expect(isGuestAllowed(command(op), on)).toBe(false);
  });
  it('validates clip fields and accepts snapshots with or without Mimic state', () => {
    expect(
      commandSchema.safeParse(
        command('mimicClip', { mimicEnd: 960000, positionMs: 100, endMs: 2000, name: 'Said it' }),
      ).success,
    ).toBe(true);
    expect(commandSchema.safeParse(command('mimicClip', { mimicEnd: -1 })).success).toBe(false);
    expect(snapshotSchema.safeParse(EMPTY).success).toBe(true);
    const mimic = {
      active: true,
      source: 'App: Discord [1]',
      status: 'Listening',
      windowMs: 30000,
    };
    expect(snapshotSchema.parse({ ...EMPTY, mimic }).mimic).toEqual(mimic);
  });
});
