import { useEffect, useId, useRef, useState, type RefObject } from 'react';
import { recall, remember } from '../sections';
import { Headphones, Pause, Play, Square } from 'lucide-react';
import { z } from 'zod';
import { controller } from '../controller';
import type { Command } from '../protocol';
import { playback } from '../playback';
import { usePreviewAudio, usePreviewTransport } from '../preview-audio';
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

/**
 * Plays words: a private preview of exactly that word, or the same exact slice into the game. A
 * slice is a region play, which a guest also needs seeking for (`canRegion`, 2.41 review); without it
 * a word is previewed here instead, while whole sounds still play into the game.
 */
export function useWordPlayer(canPlay: boolean, canRegion = canPlay) {
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
      if (mode === 'game' && target.soundId && canPlay && canRegion)
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

export function VoicePreviewControls() {
  const preview = usePreviewTransport();
  if (!preview) return null;
  return (
    <div className="voices-preview-controls" role="group" aria-label="Voice preview">
      <button type="button" data-playback-trigger onClick={preview.toggle}>
        {preview.paused ? <Play size={14} /> : <Pause size={14} />}
        {preview.paused ? 'Resume preview' : 'Pause preview'}
      </button>
      <button type="button" onClick={preview.stop}>
        <Square size={13} /> Stop preview
      </button>
    </div>
  );
}

export function WordModeSwitch({
  mode,
  set,
  canPlay,
  canRegion = canPlay,
}: {
  mode: WordMode;
  set: (m: WordMode) => void;
  canPlay: boolean;
  /** Single words into the game (a region play); see useWordPlayer. */
  canRegion?: boolean;
}) {
  const group = useId();
  return (
    <div className="voices-mode" role="radiogroup" aria-label="Word clicks">
      <span>Playback</span>
      <label>
        <input
          type="radio"
          name={group}
          checked={mode === 'preview'}
          onChange={() => set('preview')}
        />
        <Headphones size={13} aria-hidden="true" /> Preview here
      </label>
      <label title={canPlay ? undefined : 'Playback is not available'}>
        <input
          type="radio"
          name={group}
          checked={mode === 'game'}
          disabled={!canPlay}
          onChange={() => set('game')}
        />
        <Play size={13} aria-hidden="true" /> Play into game
      </label>
      {mode === 'game' && canPlay && !canRegion && (
        <span className="muted">Single words play here only: the host has turned off seeking.</span>
      )}
    </div>
  );
}

/**
 * Section memory (Stage G): keeps the scroll position of the Voices or Sentences section in `sections.ts`,
 * and puts it back once when `ready` first becomes true. The section scrolls inside its dialog today and
 * inside the main view (#board) after the shell change, so the scroller is looked up from the panel.
 */
export function useSectionScroll(
  section: 'voices' | 'sentences',
  panel: RefObject<HTMLElement | null>,
  ready: boolean,
  restore = true,
) {
  const restored = useRef(!restore);
  useEffect(() => {
    const scroller = panel.current?.closest<HTMLElement>('.dialog, #board');
    if (!scroller) return;
    if (ready && !restored.current) {
      restored.current = true;
      const top = recall(section).scrollTop;
      if (top) requestAnimationFrame(() => (scroller.scrollTop = top));
    }
    let timer: ReturnType<typeof setTimeout> | undefined;
    const save = () => {
      clearTimeout(timer);
      timer = setTimeout(() => remember(section, { scrollTop: scroller.scrollTop }), 150);
    };
    scroller.addEventListener('scroll', save, { passive: true });
    return () => {
      clearTimeout(timer);
      scroller.removeEventListener('scroll', save);
    };
  }, [section, panel, ready]);
}
