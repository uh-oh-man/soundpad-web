import { describe, it, expect } from 'vitest';
import {
  ago,
  lineSelection,
  nudgeSelection,
  normalizeSelection,
  transcriptFor,
} from '../../web/src/components/MimicPanel';
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

describe('Mimic transcript', () => {
  const lines = [
    { startMs: 20000, endMs: 22000, text: ' ask not what your country ' },
    { startMs: 22100, endMs: 24000, text: 'can do for you' },
    { startMs: 29900, endMs: 30400, text: 'still talking' },
  ];
  it('selects a line with padding, clamped to the window', () => {
    expect(lineSelection(lines[0], 30000)).toEqual({ from: 19850, to: 22150 });
    expect(lineSelection(lines[2], 30000)).toEqual({ from: 29750, to: 30000 });
  });
  it('names a clip after the words inside the selection', () => {
    expect(transcriptFor(lines, { from: 19000, to: 25000 })).toBe(
      'ask not what your country can do for you',
    );
    expect(transcriptFor(lines, { from: 0, to: 1000 })).toBe('');
    const long = [{ startMs: 0, endMs: 1000, text: 'word '.repeat(40) }];
    expect(transcriptFor(long, { from: 0, to: 1000 })).toHaveLength(100);
  });
});

describe('Mimic keyboard selection', () => {
  it('starts with the last second, then moves the end or (with Shift) the start', () => {
    const first = nudgeSelection(null, 'ArrowLeft', false, 30000);
    expect(first).toEqual({ from: 29000, to: 30000 });
    expect(nudgeSelection(first, 'ArrowLeft', false, 30000)).toEqual({ from: 29000, to: 29900 });
    expect(nudgeSelection(first, 'ArrowLeft', true, 30000)).toEqual({ from: 28900, to: 30000 });
    expect(nudgeSelection(first, 'ArrowLeft', true, 30000, 1000)).toEqual({
      from: 28000,
      to: 30000,
    });
  });
  it('stays inside the window and never collapses below the minimum length', () => {
    expect(nudgeSelection({ from: 0, to: 30000 }, 'ArrowRight', false, 30000).to).toBe(30000);
    expect(nudgeSelection({ from: 0, to: 30000 }, 'ArrowLeft', true, 30000).from).toBe(0);
    expect(nudgeSelection({ from: 1000, to: 1200 }, 'ArrowLeft', false, 30000)).toEqual({
      from: 1000,
      to: 1150,
    });
    expect(nudgeSelection({ from: 1000, to: 1200 }, 'ArrowRight', true, 30000)).toEqual({
      from: 1050,
      to: 1200,
    });
  });
});
