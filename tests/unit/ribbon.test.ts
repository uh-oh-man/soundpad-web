import { describe, it, expect } from 'vitest';
import {
  ATTACK_MS,
  RELEASE_MS,
  RIPPLE_GAP_MS,
  clipPeak,
  detectRipple,
  envelopeAt,
  follow,
  freshFollow,
  pickLevel,
  ribbonKey,
  ribbonTarget,
  usablePeaks,
  type PeakEntry,
  type PeakLevel,
} from '../../web/src/ribbon/envelope';
import { RibbonMachine } from '../../web/src/ribbon/machine';
import { drawRibbon, strandPoints, type RibbonContext } from '../../web/src/ribbon/draw';
import type { Snapshot } from '../../web/src/protocol';

const HOST = 'a'.repeat(64);
const E1 = '11111111-1111-4111-8111-111111111111';
const E2 = '22222222-2222-4222-8222-222222222222';
const play = (extra: Partial<Snapshot['playback']> = {}): Snapshot['playback'] => ({
  status: 'PLAYING',
  positionMs: 0,
  durationMs: 4000,
  soundId: 'abc',
  epoch: E1,
  identitySource: 'web',
  positionRevision: 0,
  ...extra,
});
/** A level whose peaks are `value` everywhere except a loud hit in bin `hit`. */
function level(bins: number, value = 8000, hit = -1, loud = 32000): PeakLevel {
  const peaks = new Int16Array(bins * 2);
  for (let i = 0; i < bins; i++) {
    const v = i === hit ? loud : value;
    peaks[2 * i] = -v;
    peaks[2 * i + 1] = v;
  }
  return { bins, peaks };
}
const ready = (levels: PeakLevel[], durationMs = 4000): PeakEntry => ({
  status: 'ready',
  durationMs,
  levels,
});

describe('ribbon identity guard', () => {
  it('follows only an accepted web play that is playing and not held', () => {
    expect(ribbonKey(play(), HOST, false)).toBe(`${HOST}:abc:${E1}`);
    expect(ribbonKey(play({ status: 'PAUSED' }), HOST, false)).toBeNull();
    expect(ribbonKey(play({ identitySource: 'unknown' }), HOST, false)).toBeNull();
    expect(ribbonKey(play({ epoch: null }), HOST, false)).toBeNull();
    expect(ribbonKey(play({ soundId: null }), HOST, false)).toBeNull();
    expect(ribbonKey(play(), HOST, true)).toBeNull();
    expect(ribbonKey(play(), null, false)).toBeNull();
  });
  it('waits for a sentence, a palette word, Soundpad playing on its own, or peaks it cannot trust', () => {
    const peaks = ready([level(512)]);
    expect(ribbonTarget(play({ soundId: 'sentence:x' }), HOST, false, peaks, 0).mode).toBe(
      'waiting',
    );
    expect(ribbonTarget(play({ soundId: 'palette:x' }), HOST, false, peaks, 0).mode).toBe(
      'waiting',
    );
    expect(ribbonTarget(play({ identitySource: 'unknown' }), HOST, false, peaks, 0)).toEqual({
      mode: 'waiting',
      key: null,
    });
    expect(ribbonTarget(play(), HOST, false, undefined, 0).mode).toBe('waiting');
    expect(ribbonTarget(play(), HOST, false, { status: 'unavailable', levels: null }, 0).mode).toBe(
      'waiting',
    );
    expect(ribbonTarget(play(), HOST, false, peaks, 0).mode).toBe('live');
    expect(ribbonTarget(play({ status: 'STOPPED' }), HOST, false, peaks, 0).mode).toBe('idle');
    expect(ribbonTarget(play(), HOST, true, peaks, 0).mode).toBe('idle');
  });
  it('uses partial peaks only inside what was analysed, and maps them over that span', () => {
    const loading: PeakEntry = {
      status: 'loading',
      durationMs: 4000,
      analyzedMs: 1000,
      levels: [level(512)],
    };
    expect(usablePeaks(loading, 900)?.spanMs).toBe(1000);
    expect(usablePeaks(loading, 1001)).toBeNull();
    expect(ribbonTarget(play(), HOST, false, loading, 1500).mode).toBe('waiting');
    expect(usablePeaks({ status: 'loading', analyzedMs: 0, levels: [level(4)] }, 0)).toBeNull();
  });
});

describe('ribbon envelope', () => {
  it('picks the coarsest level with bins of at most 10 ms, else the finest', () => {
    const levels = [level(32768), level(512), level(4096)];
    expect(pickLevel(levels, 5120).bins).toBe(512); // exactly 10 ms per bin
    expect(pickLevel(levels, 5121).bins).toBe(4096);
    expect(pickLevel(levels, 40960).bins).toBe(4096);
    expect(pickLevel(levels, 40961).bins).toBe(32768);
    expect(pickLevel(levels, 10 * 60 * 1000).bins).toBe(32768);
  });
  it('normalises to the clip, never below a quarter, and lifts quiet parts', () => {
    expect(clipPeak(level(64, 3000))).toBe(0.25);
    const l = level(400, 8000, 200, 32000);
    expect(clipPeak(l)).toBeCloseTo(32000 / 32767);
    // 400 bins over 4 s: 10 ms each. The hit is at 2000-2010 ms.
    expect(envelopeAt(l, 4000, 2005, 20)).toBeCloseTo(1, 5);
    expect(envelopeAt(l, 4000, 500, 20)).toBeCloseTo(Math.sqrt(8000 / 32000), 2);
    expect(envelopeAt(l, 4000, -5000, 20)).toBe(0);
  });
  it('rises in about 30 ms and falls in about 350 ms', () => {
    const up = freshFollow();
    follow(up, 1, ATTACK_MS);
    expect(up.level).toBeCloseTo(1 - Math.exp(-1), 5);
    const down = { ...freshFollow(), level: 1 };
    follow(down, 0, RELEASE_MS);
    expect(down.level).toBeCloseTo(Math.exp(-1), 5);
    follow(down, 0, -50);
    expect(down.level).toBeCloseTo(Math.exp(-1), 5);
  });
  it('starts at most one ripple per 350 ms (never more than 3 a second) and forgets old ones', () => {
    const state = freshFollow();
    let started = 0;
    for (let now = 0; now <= 1000; now += 10) {
      const before = state.ripples.length;
      detectRipple(state, 1, 0.1, now);
      if (state.ripples.length > before || state.lastRipple === now) started++;
    }
    expect(started).toBeLessThanOrEqual(3);
    expect(RIPPLE_GAP_MS).toBeGreaterThanOrEqual(1000 / 3);
    detectRipple(state, 0.2, 0.19, 5000);
    expect(state.ripples).toEqual([]);
    detectRipple(state, 0.5, 0.4, 6000);
    expect(state.ripples).toEqual([]);
  });
});

describe('ribbon machine', () => {
  const entry = ready([level(400, 20000)]);
  it('starts every new play (new epoch) from silence', () => {
    const m = new RibbonMachine();
    m.report(play(), HOST, false, 0);
    for (let t = 16; t < 400; t += 16) m.frame(entry, t, 16);
    expect(m.follow.level).toBeGreaterThan(0.5);
    m.report(play({ epoch: E2 }), HOST, false, 400);
    expect(m.key).toBe(`${HOST}:abc:${E2}`);
    expect(m.follow.level).toBe(0);
    expect(m.follow.ripples).toEqual([]);
  });
  it('resets only the base on a seek, with no ripple from the jump', () => {
    const quietThenLoud: PeakLevel = level(400, 1000, 300, 32000);
    const m = new RibbonMachine();
    m.report(play(), HOST, false, 0);
    for (let t = 16; t < 200; t += 16) m.frame(ready([quietThenLoud]), t, 16);
    const before = m.follow.level;
    // Seek right onto the loud hit.
    m.report(play({ positionMs: 3000, positionRevision: 1 }), HOST, false, 200);
    expect(m.key).toBe(`${HOST}:abc:${E1}`);
    expect(m.position(200)).toBe(3000);
    expect(m.follow.level).toBe(before);
    m.frame(ready([quietThenLoud]), 216, 16);
    expect(m.follow.ripples).toEqual([]);
  });
  it('settles after Stop, then is idle; waits on a sentence', () => {
    const m = new RibbonMachine();
    m.report(play(), HOST, false, 0);
    for (let t = 16; t < 300; t += 16) expect(m.frame(entry, t, 16).mode).toBe('live');
    m.report(play({ status: 'STOPPED' }), HOST, false, 300);
    expect(m.frame(entry, 316, 16).mode).toBe('settling');
    let mode = 'settling';
    for (let t = 332; t < 5000 && mode === 'settling'; t += 33) mode = m.frame(entry, t, 33).mode;
    expect(mode).toBe('idle');
    m.report(play({ soundId: 'sentence:s', epoch: E2 }), HOST, false, 6000);
    expect(m.frame(undefined, 6016, 16).mode).toBe('waiting');
  });
});

describe('ribbon drawing', () => {
  function stub() {
    const sets: string[] = [];
    const calls: string[] = [];
    const target: Record<string, unknown> = {
      createLinearGradient: () => ({ addColorStop: () => {} }),
    };
    const ctx = new Proxy(target, {
      get(t, name: string) {
        if (name in t) return t[name];
        return (...args: unknown[]) => calls.push(`${name}(${args.length})`);
      },
      set(t, name: string, value) {
        sets.push(name);
        t[name] = value;
        return true;
      },
    }) as unknown as RibbonContext;
    return { ctx, sets, calls };
  }
  const palette = { deep: '#12306b', strand: '#3d8bff', crest: '#d6f0ff' };
  it('never uses shadows or filters', () => {
    const { ctx, sets, calls } = stub();
    drawRibbon(
      ctx,
      { width: 800, height: 120, strands: 14 },
      { t: 1.3, level: 1, ripples: [{ t0: 1, amp: 0.6 }], alpha: 1, palette },
    );
    expect(sets).not.toContain('shadowBlur');
    expect(sets).not.toContain('shadowColor');
    expect(sets).not.toContain('filter');
    expect(calls.filter((c) => c.startsWith('stroke(')).length).toBe(15); // 13 strands + crest twice
  });
  it('keeps every point within 0.42 of the height from the centre line, at any level and ripple', () => {
    const geom = { width: 640, height: 120, strands: 14 };
    for (const level of [0, 0.5, 1, 5])
      for (const t of [0, 0.4, 2.7])
        for (let i = 0; i < 14; i++)
          for (const p of strandPoints(
            geom,
            {
              t,
              level,
              ripples: [
                { t0: t - 0.1, amp: 0.6 },
                { t0: t, amp: 0.6 },
              ],
              alpha: 1,
              palette,
            },
            i,
          ))
            expect(Math.abs(p.y - 0.55 * 120)).toBeLessThanOrEqual(0.42 * 120 + 1e-9);
  });
});
