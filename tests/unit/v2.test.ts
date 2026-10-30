import { describe, it, expect } from 'vitest';
import {
  blankWorkspace,
  parseWorkspace,
  workspaceSchema,
  moveSound,
  toggleFavorite,
} from '../../web/src/workspace';
import { wavePath } from '../../web/src/components/Waveform';
import { cropGeometry } from '../../web/src/components/IconEditor';
import {
  command,
  commandSchema,
  cleanName,
  EMPTY,
  isGuestAllowed,
  parseSnapshot,
  waveformSchema,
} from '../../web/src/protocol';
const host = 'a'.repeat(64),
  sound = 'b'.repeat(64),
  other = 'c'.repeat(64);
describe('personal workspace boundaries', () => {
  it('validates round trips without session credentials', () => {
    const w = { ...blankWorkspace(host), favorites: [sound] };
    const json = JSON.stringify(w);
    expect(parseWorkspace(json, host)).toEqual(w);
    expect(json).not.toMatch(/token|secret|privateKey/);
    expect(() => parseWorkspace(json, other)).toThrow('different host');
    expect(() => parseWorkspace(JSON.stringify({ ...w, token: 'admin' }), host)).toThrow();
  });
  it('rejects cycles and broken folder parents', () => {
    const id = crypto.randomUUID();
    expect(
      workspaceSchema.safeParse({
        ...blankWorkspace(host),
        folders: [{ id, name: 'Cycle', parent: id, soundIds: [] }],
      }).success,
    ).toBe(false);
  });
  it('keeps nested folders contiguous and removes duplicate memberships on import', () => {
    const parent = crypto.randomUUID(),
      child = crypto.randomUUID();
    const result = workspaceSchema.parse({
      ...blankWorkspace(host),
      favorites: [sound, sound],
      folders: [
        { id: child, name: 'Child', parent, soundIds: [sound, sound] },
        { id: parent, name: 'Parent', parent: null, soundIds: [] },
      ],
    });
    expect(result.folders.map((f) => f.name)).toEqual(['Parent', 'Child']);
    expect(result.favorites).toEqual([sound]);
    expect(result.folders[1].soundIds).toEqual([sound]);
  });
  it('reorders personal sounds without touching the host', () => {
    const baseline = [sound, other];
    const w = { ...blankWorkspace(host), order: [...baseline] };
    expect(moveSound(w, other, undefined, sound).order).toEqual([other, sound]);
    expect(baseline).toEqual([sound, other]);
    expect(toggleFavorite(w, sound).favorites).toEqual([sound]);
    expect(w.favorites).toEqual([]);
  });
  it('retains missing sound references for later recovery', () => {
    const w = { ...blankWorkspace(host), favorites: [other] };
    expect(parseWorkspace(JSON.stringify(w), host).favorites).toEqual([other]);
  });
  it('rejects nonfinite cue and reversed region boundaries', () => {
    const w = blankWorkspace(host);
    expect(
      workspaceSchema.safeParse({
        ...w,
        cues: { [sound]: [{ id: crypto.randomUUID(), name: 'Bad', at: NaN }] },
      }).success,
    ).toBe(false);
    expect(
      workspaceSchema.safeParse({
        ...w,
        regions: {
          [sound]: [{ id: crypto.randomUUID(), name: 'Bad', start: 100, end: 50, repeat: false }],
        },
      }).success,
    ).toBe(false);
  });
});
describe('waveform geometry and validation', () => {
  it('renders genuine min/max peaks and silence without NaN or decorative data', () => {
    const wave = {
      soundId: sound,
      version: 'one',
      status: 'ready' as const,
      durationMs: 1000,
      levels: [
        { bins: 4, peaks: [0, 0, -16384, 16384, -32767, 32767, 0, 0] },
        { bins: 4, peaks: [0, 0, -16384, 16384, -32767, 32767, 0, 0] },
        { bins: 4, peaks: [0, 0, -16384, 16384, -32767, 32767, 0, 0] },
      ],
    };
    expect(waveformSchema.safeParse(wave).success).toBe(true);
    const fit = wavePath(wave, 4, 1, 0);
    expect(fit).toContain('M0,32V32');
    expect(fit).toContain('M2,3V61');
    expect(wavePath(wave, 4, 2, 500)).not.toEqual(fit);
    expect(fit).not.toMatch(/NaN|Infinity/);
  });
  it('rejects mismatched peak arrays and invalid amplitudes', () => {
    expect(
      waveformSchema.safeParse({
        soundId: sound,
        version: 'one',
        status: 'ready',
        durationMs: 1000,
        levels: [{ bins: 1, peaks: [99999] }],
      }).success,
    ).toBe(false);
  });
  it('crops landscape and portrait images to a covering square', () => {
    const a = cropGeometry(1000, 500, 1, 0, 0),
      b = cropGeometry(500, 1000, 1, 0, 0);
    expect(a).toEqual({ width: 512, height: 256, x: -128, y: 0 });
    expect(b.width).toBe(256);
    expect(b.height).toBe(512);
  });
});
describe('extended protocol', () => {
  it('requires bounded seeks and host-controlled permissions', () => {
    expect(commandSchema.safeParse(command('seek', { positionMs: Infinity })).success).toBe(false);
    expect(commandSchema.safeParse(command('seek', { positionMs: -1 })).success).toBe(false);
    expect(
      isGuestAllowed(command('seek'), {
        ...EMPTY,
        permissions: { remotePlayback: true, guestCounter: false, guestSeeking: false },
      }),
    ).toBe(false);
    expect(isGuestAllowed(command('waveform'), EMPTY)).toBe(true);
    expect(isGuestAllowed(command('clearStatistics'), EMPTY)).toBe(false);
    expect(isGuestAllowed(command('spotify'), EMPTY)).toBe(false);
  });
  it('does not clone or revalidate trusted libraries for lightweight ticks', () => {
    const light = { ...EMPTY, library: null };
    expect(parseSnapshot(light, EMPTY.library).library).toBe(EMPTY.library);
  });
  it('bounds display names and strips control characters without HTML interpretation', () => {
    expect(cleanName('  Alice\n\u0000  ')).toBe('Alice');
    expect(cleanName('<script>')).toBe('<script>');
    expect(cleanName('x'.repeat(100))).toHaveLength(40);
  });
});
