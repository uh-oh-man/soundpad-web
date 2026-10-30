import { useEffect, useState, useSyncExternalStore } from 'react';
import { waveformSchema, type Waveform } from './protocol';
import { controller } from './controller';
export type CachedWaveform = Omit<Waveform, 'levels'> & {
  levels?: { bins: number; peaks: Int16Array }[];
};
const cache = new Map<string, CachedWaveform>();
const pending = new Map<string, Promise<CachedWaveform>>();
const queue: (() => void)[] = [];
const listeners = new Set<() => void>();
const invalidations = new Map<string, number>();
let resetEpoch = 0;
let queueTimer: ReturnType<typeof setTimeout> | undefined;
let active = 0,
  lastStart = 0,
  generation = 0;
function runQueue() {
  if (active >= 2 || !queue.length) return;
  const delay = Math.max(0, 300 - (performance.now() - lastStart));
  if (delay) {
    queueTimer ??= setTimeout(() => {
      queueTimer = undefined;
      runQueue();
    }, delay);
    return;
  }
  lastStart = performance.now();
  active++;
  queue.shift()!();
}
export function acceptWaveform(hostId: string | null | undefined, raw: unknown) {
  if (!hostId) return;
  const data = waveformSchema.parse(raw),
    key = hostId + ':' + data.soundId;
  const previous = cache.get(key);
  if (
    previous?.version === data.version &&
    previous.status === 'ready' &&
    data.status === 'loading'
  )
    return;
  cache.set(key, {
    ...data,
    levels: data.levels?.map((l) => ({ bins: l.bins, peaks: Int16Array.from(l.peaks) })),
  });
  generation++;
  for (const notify of listeners) notify();
}
export function clearWaveforms(hostId?: string, id?: string) {
  if (hostId && id) {
    const key = hostId + ':' + id;
    cache.delete(key);
    pending.delete(key);
    invalidations.set(key, (invalidations.get(key) ?? 0) + 1);
  } else {
    cache.clear();
    pending.clear();
    invalidations.clear();
    resetEpoch++;
  }
  generation++;
  for (const notify of listeners) notify();
}
export function requestWaveform(hostId: string, id: string) {
  const key = hostId + ':' + id;
  const hit = cache.get(key);
  if (hit) return Promise.resolve(hit);
  if (pending.has(key)) return pending.get(key)!;
  const startedEpoch = resetEpoch,
    startedInvalidation = invalidations.get(key) ?? 0;
  const promise = new Promise<CachedWaveform>((resolve, reject) => {
    queue.push(() => {
      void (async () => {
        try {
          const session = controller.getSnapshot?.();
          if (session && (session.mode === 'landing' || !session.state.available))
            throw Error('Host unavailable; cached waveforms are retained.');
          const data = waveformSchema.parse(await controller.run('waveform', { soundId: id }));
          if (data.soundId !== id) throw Error('Waveform belongs to another sound.');
          const stored = {
            ...data,
            levels: data.levels?.map((l) => ({ bins: l.bins, peaks: Int16Array.from(l.peaks) })),
          };
          if (
            startedEpoch === resetEpoch &&
            startedInvalidation === (invalidations.get(key) ?? 0)
          ) {
            const previous = cache.get(key);
            if (!(
              previous &&
              (!stored.version || previous.version === stored.version) &&
              previous.status === 'ready' &&
              stored.status === 'loading'
            ))
              cache.set(key, stored);
          }
          resolve(cache.get(key) ?? stored);
        } catch (e) {
          reject(e);
        } finally {
          if (pending.get(key) === promise) pending.delete(key);
          active--;
          runQueue();
        }
      })();
    });
  });
  pending.set(key, promise);
  runQueue();
  return promise;
}
const subscribe = (notify: () => void) => {
  listeners.add(notify);
  return () => {
    listeners.delete(notify);
  };
};
export function useWaveformData(hostId: string | null | undefined, id: string, enabled: boolean) {
  const version = useSyncExternalStore(subscribe, () => generation);
  const key = (hostId ?? '') + ':' + id;
  const [entry, setEntry] = useState<{ key: string; data?: CachedWaveform; error?: string }>(
    () => ({ key, data: cache.get(key) }),
  );
  useEffect(() => {
    if (!hostId || !enabled) return;
    let live = true;
    setEntry({ key, data: cache.get(key) });
    void requestWaveform(hostId, id)
      .then((data) => {
        if (live) setEntry({ key, data });
      })
      .catch((e) => {
        if (live) setEntry({ key, error: String(e.message) });
      });
    return () => {
      live = false;
    };
  }, [hostId, id, key, enabled, version]);
  return entry.key === key ? entry : { key, data: cache.get(key) };
}
