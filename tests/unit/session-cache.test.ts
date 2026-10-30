import { beforeEach, afterEach, expect, it, vi } from 'vitest';
vi.mock('../../web/src/controller', () => ({ controller: { run: vi.fn() } }));
import { controller } from '../../web/src/controller';
import { clearWaveforms, requestWaveform } from '../../web/src/waveform-cache';
import { SeekQueue } from '../../web/src/seek';
import { randomGamerName } from '../../web/src/components/GuestName';
const wave = (id: string) => ({
  soundId: id,
  version: 'fixed',
  status: 'ready',
  durationMs: 1000,
  levels: [512, 2048, 8192].map(() => ({ bins: 2, peaks: [-100, 100, -200, 200] })),
});
beforeEach(() => {
  vi.useFakeTimers();
  clearWaveforms();
  vi.mocked(controller.run).mockReset();
});
afterEach(() => {
  vi.useRealTimers();
});
it('retains more than 64 decoded waveforms beyond 30 seconds and shares compact session data', async () => {
  vi.mocked(controller.run).mockImplementation(async (_op, fields) => wave(fields!.soundId!));
  const ids = Array.from({ length: 70 }, (_, i) => i.toString(16).padStart(64, '0'));
  let first;
  for (const id of ids) {
    const request = requestWaveform('a'.repeat(64), id);
    await vi.advanceTimersByTimeAsync(301);
    const result = await request;
    if (!first) first = result;
  }
  await vi.advanceTimersByTimeAsync(60000);
  expect(await requestWaveform('a'.repeat(64), ids[0])).toBe(first);
  expect(first!.levels![0].peaks).toBeInstanceOf(Int16Array);
  expect(controller.run).toHaveBeenCalledTimes(70);
  clearWaveforms('a'.repeat(64), ids[0]);
  const refresh = requestWaveform('a'.repeat(64), ids[0]);
  await vi.advanceTimersByTimeAsync(301);
  await refresh;
  expect(controller.run).toHaveBeenCalledTimes(71);
});
it('coalesces rapid seeking to the newest position and keeps epochs separate', async () => {
  const sent: [string, string, number][] = [];
  const complete: (() => void)[] = [];
  const queue = new SeekQueue((id, epoch, at) => {
    sent.push([id, epoch, at]);
    return new Promise<void>((resolve) => complete.push(resolve));
  });
  const first = queue.seek('sound', 'epoch', 100);
  const second = queue.seek('sound', 'epoch', 200);
  const third = queue.seek('sound', 'epoch', 900);
  expect(sent).toEqual([['sound', 'epoch', 100]]);
  complete.shift()!();
  await vi.advanceTimersByTimeAsync(150);
  expect(sent).toEqual([
    ['sound', 'epoch', 100],
    ['sound', 'epoch', 900],
  ]);
  complete.shift()!();
  await Promise.all([first, second, third]);
  const different = queue.seek('sound', 'new-epoch', 300);
  expect(sent.at(-1)).toEqual(['sound', 'new-epoch', 300]);
  complete.shift()!();
  await different;
});
it('generates bounded gamer-name combinations without a network service', () => {
  const names = new Set(Array.from({ length: 20 }, () => randomGamerName()));
  expect(names.size).toBeGreaterThan(1);
  for (const name of names) {
    expect(name).toMatch(/^[A-Za-z]+\d{3}$/);
    expect(name.length).toBeLessThanOrEqual(40);
  }
});
