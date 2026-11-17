import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { historyPoller, levelsToDraw, viewStillWanted } from '../../web/src/mimic/history-poll';

/**
 * Review finding (2.41): dragging the recording overview restarted the Mimic fetch loop on every pointer move,
 * each restart sending new requests while the old ones were still on their way.
 */
describe('Mimic history fetch loop', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  /** A fake companion: each request answers after `latency` ms; counts what is in flight. */
  function companion(latency: number) {
    let inFlight = 0;
    const seen = { most: 0, requests: 0, views: [] as number[] };
    const request = async (view: number) => {
      inFlight++;
      seen.requests++;
      seen.most = Math.max(seen.most, inFlight);
      await new Promise((resolve) => setTimeout(resolve, latency));
      inFlight--;
      return view;
    };
    return { seen, request };
  }

  it('a drag of 200 pointer moves keeps one request in flight and always fetches the newest view', async () => {
    const { seen, request } = companion(40);
    let wanted = 0;
    const shown: number[] = [];
    const loop = historyPoller(
      async ({ alive }) => {
        // A pass reads the wanted view when it starts, like MimicHistory's viewRef.
        const got = await request(wanted);
        if (alive()) shown.push(got);
      },
      { intervalMs: 500, minGapMs: 100 },
    );
    loop.start();
    await vi.advanceTimersByTimeAsync(50);
    // 200 moves, one every 8 ms (a 120 Hz pointer), 1.6 s in all.
    for (let i = 1; i <= 200; i++) {
      wanted = i;
      loop.nudge();
      await vi.advanceTimersByTimeAsync(8);
    }
    await vi.advanceTimersByTimeAsync(300);
    expect(seen.most).toBe(1);
    // About one pass per 100 ms while dragging, not one per move.
    expect(seen.requests).toBeLessThanOrEqual(1 + Math.ceil(1600 / 100) + 2);
    // What ends up shown is the view where the drag stopped.
    expect(shown.at(-1)).toBe(200);
    loop.stop();
  });

  it('a nudge while idle fetches at once; stop drops the answer of the pass on its way and ends the loop', async () => {
    const { seen, request } = companion(30);
    const passes: { nudged: boolean; tick: number }[] = [];
    const shown: number[] = [];
    const loop = historyPoller(
      async ({ nudged, tick, alive }) => {
        passes.push({ nudged, tick });
        const got = await request(passes.length);
        if (alive()) shown.push(got);
      },
      { intervalMs: 500, minGapMs: 100 },
    );
    loop.start();
    await vi.advanceTimersByTimeAsync(200);
    loop.nudge();
    await vi.advanceTimersByTimeAsync(1);
    expect(passes.at(-1)).toEqual({ nudged: true, tick: 1 });
    await vi.advanceTimersByTimeAsync(100);
    // Ordinary passes go on every 500 ms; a share asks for a full one soon.
    loop.refresh();
    await vi.advanceTimersByTimeAsync(150);
    expect(passes.at(-1)?.nudged).toBe(false);
    const before = seen.requests;
    loop.nudge();
    await vi.advanceTimersByTimeAsync(1);
    expect(loop.busy()).toBe(true);
    loop.stop();
    const shownBefore = shown.length;
    await vi.advanceTimersByTimeAsync(2000);
    expect(shown.length).toBe(shownBefore);
    expect(seen.requests).toBe(before + 1);
  });

  it('a pass that fails does not stop the loop', async () => {
    let calls = 0;
    const loop = historyPoller(
      async () => {
        calls++;
        if (calls === 1) throw new Error('offline');
      },
      { intervalMs: 500 },
    );
    loop.start();
    await vi.advanceTimersByTimeAsync(1100);
    expect(calls).toBe(3);
    loop.stop();
  });
});

/**
 * 2.45 (the WebKit Mimic flake): a waveform asked for while following live arrived after a drag had held the view
 * still, moved the window, and the next press missed the selection's edge handle.
 */
describe('a waveform reply fits the view wanted now', () => {
  const live = { follow: true, viewEnd: 0 };
  const held = (viewEnd: number) => ({ follow: false, viewEnd });
  it('keeps a live reply while still following', () => {
    expect(viewStillWanted(live, { follow: true, viewEnd: 123 })).toBe(true);
  });
  it('drops a live reply once a drag held the view still', () => {
    expect(viewStillWanted(live, held(48000))).toBe(false);
  });
  it('keeps a held reply only for the same frame', () => {
    expect(viewStillWanted(held(48000), held(48000))).toBe(true);
    expect(viewStillWanted(held(48000), held(96000))).toBe(false);
  });
  it('drops a held reply once the view follows live again', () => {
    expect(viewStillWanted(held(48000), live)).toBe(false);
  });
});

/**
 * WebKit e2e flake (2.47): a live reply accepted while following was drawn in the same render as the press or
 * quick select that held the view still, so the held view lost the live glide's lag and "Last 10 s" widened
 * the 10 s view to 30 s (or a selection's edge slid from under the pointer).
 */
describe('a held view keeps the window it was held on', () => {
  const at = (end: number) => ({ end, peaks: [end] });
  const drawn = at(341908);
  const newer = at(351142);
  it('keeps drawing the held window when a live reply lands with the hold', () => {
    expect(levelsToDraw(newer, drawn, { follow: false, viewEnd: drawn.end })).toBe(drawn);
  });
  it('draws the held window once it is fetched again', () => {
    const again = at(drawn.end);
    expect(levelsToDraw(again, drawn, { follow: false, viewEnd: drawn.end })).toBe(again);
  });
  it('follows live, and a moved view, with the newest levels as before', () => {
    expect(levelsToDraw(newer, drawn, { follow: true, viewEnd: 0 })).toBe(newer);
    // Moved elsewhere: the old window stays until the new one arrives, then the new one.
    expect(levelsToDraw(drawn, drawn, { follow: false, viewEnd: 200000 })).toBe(drawn);
    expect(levelsToDraw(at(200000), drawn, { follow: false, viewEnd: 200000 })).toEqual(at(200000));
    expect(levelsToDraw(newer, null, { follow: false, viewEnd: drawn.end })).toBe(newer);
  });
});
