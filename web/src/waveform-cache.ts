import { waveformSchema, type Waveform } from './protocol';
import { controller } from './controller';
const cache = new Map<string, { at: number; data: Waveform }>();
const pending = new Map<string, Promise<Waveform>>();
const queue: (() => void)[] = [];
let active = 0,
  lastStart = 0;
function runQueue() {
  if (active >= 2 || !queue.length) return;
  const delay = Math.max(0, 200 - (performance.now() - lastStart));
  if (delay) {
    setTimeout(runQueue, delay);
    return;
  }
  lastStart = performance.now();
  active++;
  queue.shift()!();
}
export function clearWaveforms() {
  cache.clear();
}
export function requestWaveform(hostId: string, id: string, signal?: AbortSignal) {
  const key = hostId + ':' + id;
  const hit = cache.get(key);
  if (hit && Date.now() - hit.at < 30000) return Promise.resolve(hit.data);
  if (pending.has(key)) return pending.get(key)!;
  if (queue.length > 32)
    return Promise.reject(Error('Waveform requests are busy. Try again after scrolling stops.'));
  const promise = new Promise<Waveform>((resolve, reject) => {
    queue.push(() => {
      void (async () => {
        try {
          if (signal?.aborted) throw Error('Cancelled');
          const raw = await controller.run('waveform', { soundId: id });
          const data = waveformSchema.parse(raw);
          if (data.soundId !== id) throw Error('Waveform belongs to another sound.');
          if (cache.size >= 64) cache.delete(cache.keys().next().value!);
          cache.set(key, { at: Date.now(), data });
          resolve(data);
        } catch (e) {
          reject(e);
        } finally {
          pending.delete(key);
          active--;
          runQueue();
        }
      })();
    });
    runQueue();
  });
  pending.set(key, promise);
  return promise;
}
