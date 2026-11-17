import { describe, expect, it } from 'vitest';
import { glideData, glideStart, glideStep, glideTarget } from '../../web/src/mimic/glide';

/** Runs frames every `frameMs` from `from` to `to` and returns the view ends seen. */
function frames(g: ReturnType<typeof glideStart>, from: number, to: number, frameMs = 16) {
  const ends: number[] = [];
  for (let t = from; t <= to; t += frameMs) {
    glideStep(g, t);
    ends.push(g.visEnd);
  }
  return ends;
}

describe('live waveform glide (2.47)', () => {
  it('starts a little behind the newest levels and never shows past them', () => {
    const g = glideStart(10_000, 500);
    expect(g.dataEnd - g.visEnd).toBe(glideTarget(g));
    const ends = frames(g, 0, 2000);
    expect(Math.max(...ends)).toBeLessThanOrEqual(10_000);
  });

  it('moves on every frame between polls at about the speed time passes', () => {
    let g = glideStart(10_000, 500);
    glideStep(g, 1000);
    let last = g.visEnd;
    // Polls every 500 ms, each bringing 500 ms of new audio.
    for (let poll = 1; poll <= 20; poll++) {
      const ends = frames(g, 1000 + (poll - 1) * 500 + 16, 1000 + poll * 500);
      for (const end of ends) {
        expect(end).toBeGreaterThanOrEqual(last); // never backwards
        last = end;
      }
      g = glideData(g, 10_000 + poll * 500);
    }
    // Settled: just before the next poll the view is still behind the data, by less than one poll.
    frames(g, 1000 + 20 * 500 + 16, 1000 + 20 * 500 + 480);
    expect(g.dataEnd - g.visEnd).toBeGreaterThan(0);
    expect(g.dataEnd - g.visEnd).toBeLessThan(500);
  });

  it('a new poll does not move the view; it only gives it more to glide into', () => {
    const g = glideStart(10_000, 500);
    glideStep(g, 0);
    glideStep(g, 300);
    const before = g.visEnd;
    const next = glideData(g, 10_500);
    expect(next.visEnd).toBe(before);
  });

  it('learns slower polls (a guest page) and keeps gliding without stalling', () => {
    let g = glideStart(0, 500);
    let t = 0;
    glideStep(g, t);
    let stalls = 0;
    for (let poll = 1; poll <= 40; poll++) {
      for (let i = 0; i < 1000 / 16; i++) {
        t += 16;
        const before = g.visEnd;
        glideStep(g, t);
        if (g.visEnd === before) stalls++;
      }
      g = glideData(g, poll * 1000);
    }
    expect(g.interval).toBeGreaterThan(900);
    // Once the spacing is learned the view does not wait at the end of the data.
    expect(stalls).toBeLessThan(40);
  });

  it('a long pause or a restart re-anchors instead of racing to catch up', () => {
    const g = glideStart(10_000, 500);
    glideStep(g, 0);
    const late = glideData(g, 60_000);
    expect(late.dataEnd - late.visEnd).toBe(glideTarget(late));
    const restarted = glideData(late, 1_000);
    expect(restarted.dataEnd).toBe(1_000);
    expect(restarted.visEnd).toBeLessThan(1_000);
  });
});

describe('the glide held under the pointer (2.47)', () => {
  it('never jumps on while held, and goes back to just behind the newest audio once let go', () => {
    let g = glideStart(10_000, 500);
    glideStep(g, 0);
    const held = g.visEnd;
    for (let poll = 1; poll <= 10; poll++) g = glideData(g, 10_000 + poll * 500, true);
    expect(g.visEnd).toBe(held);
    glideStep(g, 16);
    expect(g.dataEnd - g.visEnd).toBeLessThan(500);
  });
});
