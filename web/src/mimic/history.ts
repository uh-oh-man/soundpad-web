// Pure helpers for the Mimic recording history (2.39): timeline math, the window the waveform shows, gaps, and
// word selection in the transcript. No React, so unit tests use them directly. Frames are on the companion's
// absolute history timeline (the capture rate); gaps take no frames.
import { z } from 'zod';
import { MIN_MS, type Selection } from './selection';

const range = z.object({
  startFrame: z.number().int().nonnegative(),
  endFrame: z.number().int().nonnegative(),
});
export type FrameRange = { startFrame: number; endFrame: number };

export const timelineSchema = z.object({
  sessionId: z.string().max(64).nullable(),
  rate: z.number().int().positive(),
  liveFrame: z.number().int().nonnegative(),
  liveWallUtcMs: z.number().nullable(),
  nowUtcMs: z.number(),
  recording: z.boolean(),
  windowSeconds: z.number().int().positive(),
  available: z.array(range).max(2048),
  spans: z
    .array(
      z.object({
        startFrame: z.number().int().nonnegative(),
        endFrame: z.number().int().nonnegative(),
        wallStartUtcMs: z.number(),
      }),
    )
    .max(4096),
  gaps: z
    .array(
      z.object({
        atFrame: z.number().int().nonnegative(),
        wallFromUtcMs: z.number(),
        wallToUtcMs: z.number().nullable(),
        reason: z.string().max(32),
      }),
    )
    .max(4096),
  fromFrame: z.number().int().nonnegative().nullable(),
  shared: z.array(range).max(64),
  transcribing: z.boolean(),
});
export type Timeline = z.infer<typeof timelineSchema>;

export const levelsSchema = z.object({
  start: z.number().int().nonnegative(),
  end: z.number().int().nonnegative(),
  bins: z.number().int().positive(),
  levels: z.array(z.number().int().min(-32768).max(32767)).max(4096),
});

export const wordSchema = z.object({
  id: z.number().int().positive(),
  start: z.number().int().nonnegative(),
  end: z.number().int().nonnegative(),
  text: z.string().max(200),
  probability: z.number(),
  stretchMs: z.number().int(),
  pauseBefore: z.boolean(),
  pauseAfter: z.boolean(),
  status: z.enum(['done', 'uncertain', 'pending']),
  phraseId: z.number().int(),
  revision: z.number().int(),
  corrected: z.boolean(),
  // 2.41: the speaker group of its phrase ("Group speakers (experimental)"); absent or null when Unassigned.
  speaker: z.number().int().positive().nullish(),
});
export type Word = z.infer<typeof wordSchema>;
export const transcriptSchema = z.object({
  words: z.array(wordSchema).max(20000),
  pending: z.array(range).max(2048),
  transcribing: z.boolean(),
});

/** The window behind the 30 s waveform: it always spans 30 s, the zoom shows the newest part of it. */
export const WINDOW_MS = 30000;
/** Zoom choices; the history view starts at the newest 10 s. */
export const HISTORY_VIEW_SECONDS = [30, 10, 5];
export const DEFAULT_HISTORY_VIEW = 10;
/** Longest clip the companion makes. */
export const MAX_CLIP_MS = 30000;
/** Padding applied when a word becomes a cut (word-timing evaluation): 20 ms before, 10 ms after. */
export const PAD_BEFORE_MS = 20;
export const PAD_AFTER_MS = 10;

export const msToFrames = (ms: number, rate: number) => Math.round((ms * rate) / 1000);
export const framesToMs = (frames: number, rate: number) => (frames * 1000) / rate;

/** Peak 0–1000 per bin from interleaved (min, max) pairs in ±32767, the shape the waveform draws. */
export function toPeaks(pairs: number[]) {
  const peaks: number[] = [];
  for (let i = 0; i + 1 < pairs.length; i += 2)
    peaks.push(
      Math.min(
        1000,
        Math.round((Math.max(Math.abs(pairs[i]), Math.abs(pairs[i + 1])) * 1000) / 32767),
      ),
    );
  return peaks;
}

/** First frame of the 30 s window ending at `end` (it can be negative at the start of a session). */
export const windowStart = (end: number, rate: number) => end - msToFrames(WINDOW_MS, rate);

/** The oldest frame still held, or the live frame when nothing is. */
export const oldestFrame = (t: Timeline) =>
  t.available.length ? t.available[0].startFrame : t.liveFrame;

/**
 * Where the view may end: never past live, and never so early that the 30 s window would hold nothing (its end
 * stays at least a second after the oldest audio held).
 */
export function clampViewEnd(t: Timeline, end: number) {
  const lowest = Math.min(t.liveFrame, oldestFrame(t) + t.rate);
  return Math.round(Math.min(t.liveFrame, Math.max(lowest, end)));
}

/** Back/Forward: the view end moved by a number of seconds (negative = back), clamped; `live` when it reached live. */
export function jump(t: Timeline, end: number, seconds: number) {
  const next = clampViewEnd(t, end + msToFrames(seconds * 1000, t.rate));
  return { end: next, live: next >= t.liveFrame };
}

/** UTC milliseconds at which a frame was heard, from the spans this person may see; null when unknown. */
export function wallOf(t: Timeline, frame: number): number | null {
  for (let i = t.spans.length - 1; i >= 0; i--) {
    const s = t.spans[i];
    if (frame >= s.startFrame && frame <= s.endFrame)
      return s.wallStartUtcMs + framesToMs(frame - s.startFrame, t.rate);
  }
  return null;
}

/** How far behind live a view ending at `end` is, in milliseconds of wall time (gaps included) when known. */
export function behindLiveMs(t: Timeline, end: number) {
  const at = wallOf(t, end);
  const live = t.liveWallUtcMs ?? wallOf(t, t.liveFrame);
  if (at !== null && live !== null && live >= at) return live - at;
  return Math.max(0, framesToMs(t.liveFrame - end, t.rate));
}

export const formatClock = (utcMs: number) =>
  new Date(utcMs).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });

/** "12 s", "3 min 5 s", "1 h 2 min". */
export function formatSpan(ms: number) {
  const s = Math.round(ms / 1000);
  if (s < 60) return `${s} s`;
  if (s < 3600) return `${Math.floor(s / 60)} min${s % 60 ? ` ${s % 60} s` : ''}`;
  return `${Math.floor(s / 3600)} h${Math.floor((s % 3600) / 60) ? ` ${Math.floor((s % 3600) / 60)} min` : ''}`;
}

const GAP_TEXT: Record<string, string> = {
  stopped: 'Stopped',
  'device-lost': 'Output lost',
  update: 'Update',
  overrun: 'Audio lost (disk too slow)',
  crash: 'Companion closed',
};
/** A gap as a mark inside the window: where it is (ms from the window start) and what it says. */
export function gapMarks(t: Timeline, start: number) {
  return t.gaps
    .filter((g) => g.atFrame > start && g.atFrame <= start + msToFrames(WINDOW_MS, t.rate))
    .map((g) => ({
      ms: framesToMs(g.atFrame - start, t.rate),
      label:
        (GAP_TEXT[g.reason] ?? 'Gap') +
        (g.wallToUtcMs !== null ? ` · ${formatSpan(g.wallToUtcMs - g.wallFromUtcMs)} missing` : ''),
    }));
}

/** Parts of the window (ms from its start) this person cannot see: not recorded yet, expired, or before they joined. */
export function hiddenParts(t: Timeline, start: number) {
  const end = start + msToFrames(WINDOW_MS, t.rate);
  const parts: Selection[] = [];
  let at = start;
  for (const r of t.available) {
    if (r.endFrame <= at) continue;
    if (r.startFrame >= end) break;
    if (r.startFrame > at)
      parts.push({
        from: framesToMs(at - start, t.rate),
        to: framesToMs(r.startFrame - start, t.rate),
      });
    at = Math.max(at, r.endFrame);
  }
  if (at < Math.min(end, t.liveFrame))
    parts.push({
      from: framesToMs(at - start, t.rate),
      to: framesToMs(Math.min(end, t.liveFrame) - start, t.rate),
    });
  return parts.filter((p) => p.to - p.from > 0.5);
}

/** A selection in milliseconds of the window, as frames of the history. */
export const selectionFrames = (s: Selection, start: number, rate: number) => ({
  startFrame: Math.max(0, start + msToFrames(s.from, rate)),
  endFrame: Math.max(0, start + msToFrames(s.to, rate)),
});
/** Frames of the history as a selection in milliseconds of the window starting at `start`. */
export const selectionMs = (r: FrameRange, start: number, rate: number): Selection => ({
  from: framesToMs(r.startFrame - start, rate),
  to: framesToMs(r.endFrame - start, rate),
});

// ---- Words -------------------------------------------------------------------------------------------------

/** The cut for one word: its stored edges with the padding added (stored edges are never padded). */
export function wordRange(w: Pick<Word, 'start' | 'end'>, rate: number): FrameRange {
  return {
    startFrame: Math.max(0, w.start - msToFrames(PAD_BEFORE_MS, rate)),
    endFrame: w.end + msToFrames(PAD_AFTER_MS, rate),
  };
}

/**
 * Click and Shift-click in the transcript: a click selects that word; Shift-click selects from the anchor word to
 * the clicked one (either order). The result is never shorter than the companion's minimum or longer than a clip.
 */
export function wordSelection(
  words: Word[],
  anchorId: number | null,
  clickedId: number,
  shift: boolean,
  rate: number,
): FrameRange | null {
  const clicked = words.find((w) => w.id === clickedId);
  if (!clicked) return null;
  const anchor = shift && anchorId !== null ? words.find((w) => w.id === anchorId) : undefined;
  const first = anchor && anchor.start < clicked.start ? anchor : clicked;
  const last = anchor && anchor.start > clicked.start ? anchor : clicked;
  const r = {
    startFrame: wordRange(first, rate).startFrame,
    endFrame: wordRange(last, rate).endFrame,
  };
  const min = msToFrames(MIN_MS, rate),
    max = msToFrames(MAX_CLIP_MS, rate);
  if (r.endFrame - r.startFrame < min) r.endFrame = r.startFrame + min;
  if (r.endFrame - r.startFrame > max) r.endFrame = r.startFrame + max;
  return r;
}

/** True when the word lies inside the selected frames (by its middle, so padding never decides it). */
export const wordSelected = (w: Pick<Word, 'start' | 'end'>, r: FrameRange | null) =>
  !!r && (w.start + w.end) / 2 >= r.startFrame && (w.start + w.end) / 2 <= r.endFrame;

/** Sentences for the transcript: words grouped by phrase, split after . ? ! so each reads as a sentence. */
export function sentences(words: Word[]) {
  const sorted = [...words].sort((a, b) => a.start - b.start);
  const result: Word[][] = [];
  let current: Word[] = [];
  for (const w of sorted) {
    if (current.length && current[current.length - 1].phraseId !== w.phraseId) {
      result.push(current);
      current = [];
    }
    current.push(w);
    if (/[.?!]["')\]]*$/.test(w.text)) {
      result.push(current);
      current = [];
    }
  }
  if (current.length) result.push(current);
  return result;
}

/** The words a selection holds, as the clip's suggested name (at most 100 characters). */
export function wordsText(words: Word[], r: FrameRange | null) {
  const text = words
    .filter((w) => wordSelected(w, r))
    .sort((a, b) => a.start - b.start)
    .map((w) => w.text.trim())
    .join(' ')
    .replace(/\s+/g, ' ')
    .trim();
  return text.length > 100 ? text.slice(0, 99).trimEnd() + '…' : text;
}

/** Whether a range is wholly inside what this person may see (the companion refuses anything else). */
export const visible = (t: Timeline, r: FrameRange) =>
  t.available.some((a) => r.startFrame >= a.startFrame && r.endFrame <= a.endFrame);
