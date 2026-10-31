// Pure Mimic helpers: selection math, transcript naming and source choice. No React, so unit tests use them directly.

export type Selection = { from: number; to: number };
export type TranscriptLine = { startMs: number; endMs: number; text: string };
export type Source = { id: string; name: string; kind: string };

/** Shortest selection the companion accepts. */
export const MIN_MS = 150;
/** Keyboard step for adjusting a selection edge: 100 ms, or 1 s with Ctrl. */
export const NUDGE_MS = 100;
export const QUICK_SECONDS = [3, 5, 10];
/** Keys that select the last N seconds, like Voice Replay Studio's F6 "save the last few seconds". */
export const QUICK_KEYS: Record<string, number> = { '3': 3, '5': 5, '0': 10 };

/** Zoom choices for the waveform, like Voice Replay Studio's 5/10/30 s Live Buffer views. */
export const VIEW_SECONDS = [30, 10, 5];

/**
 * The part of the window the waveform shows: the last `viewMs`, widened to the whole window when the
 * selection starts earlier, so a selection (say, from an older transcript line) is never off-screen.
 */
export function visibleRange(windowMs: number, viewMs: number, selection: Selection | null) {
  const start = Math.max(0, windowMs - viewMs);
  return { start: selection && selection.from < start ? 0 : start, end: windowMs };
}

/** Seconds before "now" for a position in the window, e.g. "−4.2 s". */
export const ago = (ms: number, windowMs: number) =>
  windowMs - ms < 50 ? 'now' : `−${((windowMs - ms) / 1000).toFixed(1)} s`;

/** Clamp and order a dragged range so it stays inside the window and meets the minimum length. */
export function normalizeSelection(a: number, b: number, windowMs: number): Selection | null {
  const from = Math.max(0, Math.min(a, b));
  const to = Math.min(windowMs, Math.max(a, b));
  return to - from >= MIN_MS ? { from: Math.round(from), to: Math.round(to) } : null;
}

/** The last `seconds` of the window. */
export const lastSeconds = (seconds: number, windowMs: number): Selection => ({
  from: Math.max(0, windowMs - seconds * 1000),
  to: windowMs,
});

/**
 * Arrow keys on the waveform: Left/Right move the end, Shift+Left/Right move the start.
 * With no selection, the first press selects the last second.
 */
export function nudgeSelection(
  selection: Selection | null,
  key: 'ArrowLeft' | 'ArrowRight',
  moveStart: boolean,
  windowMs: number,
  step = NUDGE_MS,
): Selection {
  if (!selection) return { from: windowMs - 1000, to: windowMs };
  const delta = key === 'ArrowLeft' ? -step : step;
  if (moveStart)
    return {
      ...selection,
      from: Math.min(Math.max(0, selection.from + delta), selection.to - MIN_MS),
    };
  return {
    ...selection,
    to: Math.max(Math.min(windowMs, selection.to + delta), selection.from + MIN_MS),
  };
}

/** Selection covering a transcript line, with a little padding so words aren't clipped. */
export function lineSelection(line: TranscriptLine, windowMs: number): Selection {
  return {
    from: Math.max(0, line.startMs - 150),
    to: Math.min(windowMs, Math.max(line.endMs + 150, line.startMs + MIN_MS)),
  };
}

/** True when a transcript line overlaps the selection. */
export const overlaps = (line: TranscriptLine, selection: Selection) =>
  Math.min(line.endMs, selection.to) - Math.max(line.startMs, selection.from) > 0;

/** Words spoken inside a selection, usable as the clip's default name. */
export function transcriptFor(lines: TranscriptLine[], selection: Selection) {
  const text = lines
    .filter((l) => overlaps(l, selection))
    .map((l) => l.text.trim())
    .join(' ')
    .replace(/\s+/g, ' ')
    .trim();
  return text.length > 100 ? text.slice(0, 99).trimEnd() + '…' : text;
}

/** A source's stable name: "App: Discord [1234]" -> "App: Discord" (process ids change on every launch). */
export const sourceKey = (name: string) => name.replace(/\s*\[\d+\]$/, '');

/**
 * Which source to preselect: the one still chosen, else the last one used (by stable name), else Discord,
 * the usual reason to use Mimic, else the first.
 */
export function preferredSource(sources: Source[], current: string, remembered: string | null) {
  if (sources.some((s) => s.id === current)) return current;
  const match =
    (remembered && sources.find((s) => sourceKey(s.name) === remembered)) ||
    sources.find((s) => s.kind === 'app' && /discord/i.test(s.name));
  return (match ?? sources[0])?.id ?? '';
}
