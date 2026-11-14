import { describe, it, expect } from 'vitest';
import { CONTINUITY_MS, makeBase, project } from '../../web/src/playhead';

const EPOCH = '11111111-1111-4111-8111-111111111111';
const OTHER = '22222222-2222-4222-8222-222222222222';
const playing = (positionMs: number, extra: Record<string, unknown> = {}) => ({
  status: 'PLAYING' as const,
  positionMs,
  epoch: EPOCH,
  positionRevision: 0,
  ...extra,
});

describe('playhead (the extrapolation usePlayhead and the header ribbon share)', () => {
  it('moves on by the time since the report while playing, and stops at the duration', () => {
    const base = makeBase(playing(1000), 50);
    expect(project(base, 50, 5000)).toBe(1000);
    expect(project(base, 550, 5000)).toBe(1500);
    expect(project(base, 99999, 5000)).toBe(5000);
    expect(project(base, 550, null)).toBe(1500);
  });
  it('stays where it is while paused, stopped or hidden, and never goes below 0', () => {
    expect(project(makeBase({ ...playing(800), status: 'PAUSED' }, 0), 900, 5000)).toBe(800);
    expect(project(makeBase({ ...playing(800), status: 'STOPPED' }, 0), 900, 5000)).toBe(800);
    expect(project(makeBase(playing(800), 0), 900, 5000, true)).toBe(800);
    expect(project(makeBase({ ...playing(0), positionMs: null }, 0), 0, 5000)).toBe(0);
    expect(project({ ...makeBase(playing(0), 0), position: -40 }, 0, 5000)).toBe(0);
  });
  it('keeps the further position when a lagging report of the same playback arrives', () => {
    const first = makeBase(playing(1000), 0);
    // 300 ms later the playhead is at 1300; a report that says 1200 is within the continuity window.
    const next = makeBase(playing(1200), 300, first);
    expect(next.position).toBe(1300);
    // A report ahead of the projection is taken as it is.
    expect(makeBase(playing(1400), 300, first).position).toBe(1400);
  });
  it('follows a real jump, a new epoch, a seek revision and a status change at once', () => {
    const first = makeBase(playing(1000), 0);
    expect(makeBase(playing(1300 - CONTINUITY_MS - 1), 300, first).position).toBe(
      1300 - CONTINUITY_MS - 1,
    );
    expect(makeBase(playing(1200, { epoch: OTHER }), 300, first).position).toBe(1200);
    expect(makeBase(playing(1200, { positionRevision: 1 }), 300, first).position).toBe(1200);
    expect(makeBase({ ...playing(1200), status: 'PAUSED' }, 300, first).position).toBe(1200);
  });
  it('projects a paused base from where it paused when playback resumes', () => {
    const paused = makeBase({ ...playing(2000), status: 'PAUSED' }, 0);
    // Resumed 5 s later at the same position: the paused base does not advance, so nothing jumps.
    const resumed = makeBase(playing(2010), 5000, paused);
    expect(resumed.position).toBe(2010);
    expect(project(resumed, 5100, 9000)).toBe(2110);
  });
  it('records what it was made from', () => {
    const base = makeBase(playing(10, { positionRevision: 3 }), 42);
    expect(base).toEqual({
      position: 10,
      at: 42,
      epoch: EPOCH,
      status: 'PLAYING',
      positionRevision: 3,
    });
  });
});
