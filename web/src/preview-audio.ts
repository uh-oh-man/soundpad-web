import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from 'react';
import { z } from 'zod';
import { controller } from './controller';
import type { Command } from './protocol';

type PreviewTransport = { stop: () => void; toggle: () => void; paused: boolean };
let active: PreviewTransport | null = null;
const listeners = new Set<() => void>();
const publish = (next: PreviewTransport | null) => {
  active = next;
  for (const listener of listeners) listener();
};
const subscribe = (listener: () => void) => {
  listeners.add(listener);
  return () => listeners.delete(listener);
};
export const currentPreview = () => active;
export const usePreviewTransport = () => useSyncExternalStore(subscribe, currentPreview);

type Request = { op: Command['op']; fields: Partial<Command>; loop: boolean; keep?: boolean };

/**
 * Host-only private preview (Mimic 2.12, Edit copy 2.20): the companion hands audio to this tab over
 * loopback and the browser plays it on this PC's output. It never goes through Soundpad or to guests.
 *
 * `previewing` is true from the press until the sound ends or is stopped, including while the companion
 * prepares it, so Stop also cancels a preview still on its way. A newer preview replaces an older one
 * (2.23: Loop restarts it whenever the cut changes). The companion makes one copy at a time, so while
 * one preview is being made a newer request waits, and only the latest one runs (2.29.3; before, it
 * collided with the one in progress and was refused). A request made with `keep` (a palette tap while a
 * performance records, 2.43) is never dropped: it waits its turn behind the others, because the
 * companion notes each tap when it arrives. `settled()` resolves once nothing is being made or waiting.
 */
export function usePreviewAudio() {
  const audio = useRef<HTMLAudioElement | null>(null);
  const generation = useRef(0);
  const audioGeneration = useRef(0);
  const busy = useRef(false);
  const waiting = useRef<Request[]>([]);
  const idle = useRef<(() => void)[]>([]);
  const wake = useCallback(() => {
    if (busy.current || waiting.current.length) return;
    const resolve = idle.current;
    idle.current = [];
    for (const r of resolve) r();
  }, []);
  const [previewing, setPreviewing] = useState(false);
  const [paused, setPaused] = useState(false);
  const pausedRef = useRef(false);
  const release = useCallback(() => {
    if (!audio.current) return;
    audio.current.pause();
    URL.revokeObjectURL(audio.current.src);
    audio.current = null;
  }, []);
  const stop = useCallback(() => {
    generation.current++;
    // Kept requests (palette taps while a performance records) still go: the companion notes them.
    waiting.current = waiting.current.filter((w) => w.keep);
    release();
    setPreviewing(false);
    setPaused(false);
    pausedRef.current = false;
    if (active?.stop === stop) publish(null);
    if (!busy.current) wake();
  }, [release, wake]);
  useEffect(() => stop, [stop]);
  const toggle = useCallback(() => {
    if (active?.stop !== stop) return;
    const next = !pausedRef.current;
    pausedRef.current = next;
    setPaused(next);
    publish({ ...active, paused: next });
    const sound = audio.current;
    if (!sound || audioGeneration.current !== generation.current) return;
    if (next) sound.pause();
    else
      void sound.play().catch(() => {
        if (audio.current === sound && !pausedRef.current) stop();
      });
  }, [stop]);
  const fetchAndPlay = useCallback(
    async ({ op, fields, loop }: Request, mine: number) => {
      busy.current = true;
      // The companion is free again once it has answered: the next waiting request goes then, while this
      // one starts to play.
      let done = false;
      const next = () => {
        if (done) return;
        done = true;
        busy.current = false;
        const waiting1 = waiting.current.shift();
        if (waiting1) void fetchAndPlay(waiting1, generation.current);
        else wake();
      };
      try {
        let answer: unknown;
        try {
          answer = await controller.run(op, fields);
        } finally {
          next();
        }
        const result = z.object({ wav: z.string().max(8_000_000) }).parse(answer);
        if (mine !== generation.current || active?.stop !== stop) return; // stopped or replaced meanwhile
        release();
        const bytes = Uint8Array.from(atob(result.wav), (c) => c.charCodeAt(0));
        const sound = new Audio(URL.createObjectURL(new Blob([bytes], { type: 'audio/wav' })));
        sound.loop = loop;
        sound.onended = () => {
          if (mine === generation.current) stop();
        };
        audio.current = sound;
        audioGeneration.current = mine;
        try {
          if (!pausedRef.current) await sound.play();
        } catch {
          // The browser refused to play (no output device, or it decided the click was too long ago).
          if (mine !== generation.current || pausedRef.current) return;
          stop();
          controller.notify(
            'This browser blocked the preview sound. Press Preview again.',
            'error',
          );
        }
      } catch {
        if (mine === generation.current) stop(); // the controller already shows command errors
      }
    },
    [release, stop, wake],
  );
  const play = useCallback(
    async (
      op: Command['op'],
      fields: Partial<Command>,
      { loop = false, keep = false }: { loop?: boolean; keep?: boolean } = {},
    ) => {
      if (active && active.stop !== stop) active.stop();
      const mine = ++generation.current;
      release();
      pausedRef.current = false;
      setPaused(false);
      publish({ stop, toggle, paused: false });
      setPreviewing(true);
      // One at a time: the newest request waits for the one being made. Older waiting ones are dropped,
      // except those made with keep, which each still go in their turn.
      if (busy.current) {
        waiting.current = [...waiting.current.filter((w) => w.keep), { op, fields, loop, keep }];
      } else await fetchAndPlay({ op, fields, loop, keep }, mine);
    },
    [fetchAndPlay, release, stop, toggle],
  );
  /** Resolves once no preview is being made and none waits (each has been answered). */
  const settled = useCallback(
    () =>
      new Promise<void>((resolve) => {
        if (!busy.current && !waiting.current.length) resolve();
        else idle.current.push(resolve);
      }),
    [],
  );
  /** Turns repeating on or off for the sound already playing (Loop ticked mid-preview). */
  const setLooping = useCallback((on: boolean) => {
    if (audio.current) audio.current.loop = on;
  }, []);
  /**
   * How far the preview sound has played (ms), or null while none is playing yet (still being made, or
   * the browser has not started it). Mimic's history lights the word being heard with it (2.45).
   */
  const elapsedMs = useCallback(() => {
    const sound = audio.current;
    return sound && audioGeneration.current === generation.current
      ? sound.currentTime * 1000
      : null;
  }, []);
  return { previewing, paused, toggle, play, stop, setLooping, settled, elapsedMs };
}
