import { useState } from 'react';
import { Headphones, Play } from 'lucide-react';
import { z } from 'zod';
import { controller } from '../controller';
import type { Command } from '../protocol';
import { playback } from '../playback';
import { usePreviewAudio } from '../preview-audio';
import { initials, playRange, type Voice } from '../library/voices';
import { refreshLibrary } from '../library/voice-store';

// Shared by the Voices window (2.41): avatars, changes with Undo, and the word player.

export const PAGE = 50;
const groupSchema = z.object({ group: z.number().int().nonnegative() });

/** Runs a library change; when it changed something, the notice offers Undo (2.41). */
export async function libraryChange(op: Command['op'], fields: Partial<Command>, done: string) {
  try {
    const { group } = groupSchema.parse(await controller.run(op, fields));
    if (group)
      controller.notify(done, 'success', {
        label: 'Undo',
        run: () =>
          void controller
            .run('libraryUndo', { auditId: group })
            .then(() => {
              controller.notify('Undone.', 'success');
              return refreshLibrary();
            })
            .catch(() => {}),
      });
    await refreshLibrary();
    return true;
  } catch {
    return false; // the controller shows the refusal
  }
}

export function VoiceAvatar({ voice, size = 26 }: { voice: Voice | null; size?: number }) {
  return (
    <span
      className={`voice-avatar${voice ? '' : ' unassigned'}`}
      style={{ width: size, height: size, ...(voice && { background: voice.color }) }}
      aria-hidden="true"
      title={voice?.name ?? 'Unassigned'}
    >
      {voice ? initials(voice.name) : '?'}
    </span>
  );
}

/** Where a word click goes: heard privately here, or played into the game for everyone. */
export type WordMode = 'preview' | 'game';
export function useWordMode() {
  const [mode, setMode] = useState<WordMode>(() => {
    try {
      return localStorage.getItem('soundpad.voices.wordMode') === 'game' ? 'game' : 'preview';
    } catch {
      return 'preview';
    }
  });
  return [
    mode,
    (next: WordMode) => {
      setMode(next);
      try {
        localStorage.setItem('soundpad.voices.wordMode', next);
      } catch {
        /* a convenience only */
      }
    },
  ] as const;
}

/** Plays words: a private preview of exactly that word, or the same exact slice into the game. */
export function useWordPlayer(canPlay: boolean) {
  const preview = usePreviewAudio();
  return {
    previewing: preview.previewing,
    stop: preview.stop,
    play(
      mode: WordMode,
      target: { recordingId?: string; soundId?: string | null },
      first: { s: number; e: number },
      last = first,
    ) {
      const range = playRange(first, last);
      if (mode === 'game' && target.soundId && canPlay)
        void playback.region(target.soundId, range.positionMs, range.endMs);
      else
        void preview.play(
          'libraryPreview',
          target.recordingId
            ? { recordingId: target.recordingId, ...range }
            : { soundId: target.soundId ?? undefined, ...range },
        );
    },
    whole(mode: WordMode, target: { recordingId?: string; soundId?: string | null }) {
      if (mode === 'game' && target.soundId && canPlay) void playback.play(target.soundId);
      else
        void preview.play(
          'libraryPreview',
          target.recordingId
            ? { recordingId: target.recordingId }
            : { soundId: target.soundId ?? undefined },
        );
    },
  };
}

export function WordModeSwitch({
  mode,
  set,
  canPlay,
}: {
  mode: WordMode;
  set: (m: WordMode) => void;
  canPlay: boolean;
}) {
  return (
    <div className="voices-mode" role="radiogroup" aria-label="Word clicks">
      <span>Word clicks:</span>
      <label>
        <input
          type="radio"
          name="voices-mode"
          checked={mode === 'preview'}
          onChange={() => set('preview')}
        />
        <Headphones size={13} aria-hidden="true" /> Preview here
      </label>
      <label title={canPlay ? undefined : 'Playback is not available'}>
        <input
          type="radio"
          name="voices-mode"
          checked={mode === 'game'}
          disabled={!canPlay}
          onChange={() => set('game')}
        />
        <Play size={13} aria-hidden="true" /> Play into game
      </label>
    </div>
  );
}
