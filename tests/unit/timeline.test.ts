import { describe, it, expect } from 'vitest';
import { boundedView, zoomAt } from '../../web/src/timeline';
import { hasSound, soundKeys, isGuestAllowed, command, EMPTY } from '../../web/src/protocol';
import { toggleFavorite, blankWorkspace } from '../../web/src/workspace';
describe('timeline viewport', () => {
  it('keeps the cursor time fixed during zoom', () => {
    const total = 600000,
      view = { zoom: 4, offset: 100000 },
      fraction = 0.72;
    const before = view.offset + (total / view.zoom) * fraction;
    const after = zoomAt(total, view, 16, fraction);
    expect(after.offset + (total / after.zoom) * fraction).toBeCloseTo(before);
  });
  it('bounds extreme pan and zoom without invalid offsets', () => {
    expect(boundedView(600000, 1000, 1e12)).toEqual({ zoom: 128, offset: 600000 - 600000 / 128 });
    expect(boundedView(600000, 0, -500)).toEqual({ zoom: 1, offset: 0 });
    expect(boundedView(600000, NaN, NaN)).toEqual({ zoom: 1, offset: 0 });
  });
  it('fits back to the full source at either boundary', () => {
    expect(zoomAt(600000, { zoom: 128, offset: 50000 }, 1, 0.9)).toEqual({ zoom: 1, offset: 0 });
  });
});
describe('managed audio identity and authority', () => {
  const old = 'a'.repeat(64),
    id = 'b'.repeat(64);
  const sound = { id, name: 'Promoted', icon: null, workspaceId: old, aliases: [old, id] };
  it('retains old personal membership after promotion and removes all aliases intentionally', () => {
    const w = { ...blankWorkspace('c'.repeat(64)), favorites: [old, id] };
    expect(hasSound(w.favorites, sound)).toBe(true);
    expect(soundKeys(sound)).toEqual([old, id]);
    expect(toggleFavorite(w, old, soundKeys(sound)).favorites).toEqual([]);
  });
  it('never permits guest promotion/removal or cleanup and honors generation permission', () => {
    for (const op of [
      'keepAudio',
      'removeAudio',
      'retryCleanup',
      'beginSession',
      'revokePeer',
    ] as const)
      expect(isGuestAllowed(command(op), EMPTY)).toBe(false);
    expect(
      isGuestAllowed(command('reverse'), {
        ...EMPTY,
        permissions: { ...EMPTY.permissions, guestTransforms: false },
      }),
    ).toBe(false);
    expect(isGuestAllowed(command('audioAssets'), EMPTY)).toBe(true);
  });
});
