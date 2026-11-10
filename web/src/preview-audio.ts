import { useCallback, useEffect, useRef, useState } from 'react';
import { z } from 'zod';
import { controller } from './controller';
import type { Command } from './protocol';

type Request = { op: Command['op']; fields: Partial<Command>; loop: boolean };

/**
 * Host-only private preview (Mimic 2.12, Edit copy 2.20): the companion hands audio to this tab over
 * loopback and the browser plays it on this PC's output. It never goes through Soundpad or to guests.
 *
 * `previewing` is true from the press until the sound ends or is stopped, including while the companion
 * prepares it, so Stop also cancels a preview still on its way. A newer preview replaces an older one
 * (2.23: Loop restarts it whenever the cut changes). The companion makes one copy at a time, so while
 * one preview is being made a newer request waits, and only the latest one runs (2.29.3; before, it
 * collided with the one in progress and was refused).
 */
export function usePreviewAudio() {
  const audio = useRef<HTMLAudioElement | null>(null);
  const generation = useRef(0);
  const busy = useRef(false);
  const waiting = useRef<Request | null>(null);
  const [previewing, setPreviewing] = useState(false);
  const release = useCallback(() => {
    if (!audio.current) return;
    audio.current.pause();
    URL.revokeObjectURL(audio.current.src);
    audio.current = null;
  }, []);
  const stop = useCallback(() => {
    generation.current++;
    waiting.current = null;
    release();
    setPreviewing(false);
  }, [release]);
  useEffect(() => stop, [stop]);
  const fetchAndPlay = useCallback(
    async ({ op, fields, loop }: Request, mine: number) => {
      busy.current = true;
      try {
        const result = z
          .object({ wav: z.string().max(8_000_000) })
          .parse(await controller.run(op, fields));
        if (mine !== generation.current) return; // stopped or replaced meanwhile
        release();
        const bytes = Uint8Array.from(atob(result.wav), (c) => c.charCodeAt(0));
        const next = new Audio(URL.createObjectURL(new Blob([bytes], { type: 'audio/wav' })));
        next.loop = loop;
        next.onended = () => {
          if (mine === generation.current) stop();
        };
        audio.current = next;
        try {
          await next.play();
        } catch {
          // The browser refused to play (no output device, or it decided the click was too long ago).
          if (mine !== generation.current) return;
          stop();
          controller.notify(
            'This browser blocked the preview sound. Press Preview again.',
            'error',
          );
        }
      } catch {
        if (mine === generation.current) stop(); // the controller already shows command errors
      } finally {
        busy.current = false;
        const next = waiting.current;
        waiting.current = null;
        if (next) void fetchAndPlay(next, generation.current);
      }
    },
    [release, stop],
  );
  const play = useCallback(
    async (op: Command['op'], fields: Partial<Command>, { loop = false } = {}) => {
      const mine = ++generation.current;
      setPreviewing(true);
      // One at a time: the newest request waits for the one being made, older waiting ones are dropped.
      if (busy.current) waiting.current = { op, fields, loop };
      else await fetchAndPlay({ op, fields, loop }, mine);
    },
    [fetchAndPlay],
  );
  /** Turns repeating on or off for the sound already playing (Loop ticked mid-preview). */
  const setLooping = useCallback((on: boolean) => {
    if (audio.current) audio.current.loop = on;
  }, []);
  return { previewing, play, stop, setLooping };
}
