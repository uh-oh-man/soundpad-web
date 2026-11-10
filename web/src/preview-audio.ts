import { useCallback, useEffect, useRef, useState } from 'react';
import { z } from 'zod';
import { controller } from './controller';
import type { Command } from './protocol';

/**
 * Host-only private preview (Mimic 2.12, Edit copy 2.20): the companion hands audio to this tab over
 * loopback and the browser plays it on this PC's output. It never goes through Soundpad or to guests.
 */
export function usePreviewAudio() {
  const audio = useRef<HTMLAudioElement | null>(null);
  const [previewing, setPreviewing] = useState(false);
  const stop = useCallback(() => {
    if (!audio.current) return;
    audio.current.pause();
    URL.revokeObjectURL(audio.current.src);
    audio.current = null;
    setPreviewing(false);
  }, []);
  useEffect(() => stop, [stop]);
  const play = useCallback(
    async (op: Command['op'], fields: Partial<Command>) => {
      stop();
      try {
        const result = z
          .object({ wav: z.string().max(8_000_000) })
          .parse(await controller.run(op, fields));
        const bytes = Uint8Array.from(atob(result.wav), (c) => c.charCodeAt(0));
        const next = new Audio(URL.createObjectURL(new Blob([bytes], { type: 'audio/wav' })));
        next.onended = stop;
        audio.current = next;
        setPreviewing(true);
        try {
          await next.play();
        } catch {
          // The browser refused to play (no output device, or it decided the click was too long ago).
          stop();
          controller.notify(
            'This browser blocked the preview sound. Press Preview again.',
            'error',
          );
        }
      } catch {
        stop(); // the controller already shows command errors
      }
    },
    [stop],
  );
  return { previewing, play, stop };
}
