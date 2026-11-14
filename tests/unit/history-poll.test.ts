import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { historyPoller } from '../../web/src/mimic/history-poll';

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
