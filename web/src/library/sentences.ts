import { z } from 'zod';
import { effectSchema, paletteRefSchema, templateSchema, type PaletteRef } from '../protocol';

/**
 * Sentences, the speaking palette and performance sets (2.42): the companion's replies, and the pure
 * parts of the builder (selection, keyboard operations on the timeline, snapping, missing-word
 * markers). Every piece is a range of a library recording, a pause, or a missing word that plays as
 * a gap or a bleep; nothing here makes audio.
 */

const id = z.string().regex(/^[a-f0-9]{32}$/);
const pieceId = z.string().regex(/^[a-f0-9]{12}$/);
const ms = z
  .number()
  .min(0)
  .max(4 * 3600 * 1000);

export const partSchema = z.object({
  recordingId: id,
  startMs: ms,
  endMs: ms,
  voiceId: id.nullish(),
  label: z.string().max(60).nullish(),
  recordingMs: ms.nullish(),
  state: z.enum(['ok', 'missing', 'changed']),
});
export const pieceSchema = z.object({
  id: pieceId,
  kind: z.enum(['word', 'named', 'missing', 'pause', 'splice']),
  tokenStart: z.number().int().min(0).nullish(),
  tokenEnd: z.number().int().min(0).nullish(),
  text: z.string().max(400),
  offsetMs: z.number().min(0),
  durationMs: z.number().min(0),
  manual: z.boolean(),
  chosen: z.boolean(),
  fadeInMs: z.number().min(0),
  fadeOutMs: z.number().min(0),
  gain: z.number().min(0).max(8).nullish(),
  missing: z.enum(['gap', 'bleep']).nullish(),
  substitute: z.string().max(100).nullish(),
  namedId: id.nullish(),
  effect: effectSchema.nullish(),
  voiceId: id.nullish(),
  source: partSchema.nullish(),
  parts: z.array(partSchema).max(4).nullish(),
});
export type Piece = z.infer<typeof pieceSchema>;
export const versionSchema = z.object({
  n: z.number().int().positive(),
  at: z.number(),
  byName: z.string().max(40),
  durationMs: z.number().min(0),
  text: z.string().max(4000),
  affected: z
    .array(z.object({ pieceId, text: z.string().max(400), state: z.enum(['missing', 'changed']) }))
    .max(1600),
});
export type Version = z.infer<typeof versionSchema>;
export const sentenceSchema = z.object({
  id,
  title: z.string().max(200),
  text: z.string().max(4000),
  ownerName: z.string().max(40),
  mine: z.boolean(),
  origin: z.enum(['typed', 'performance', 'template']),
  revision: z.number().int().min(0),
  kept: z.boolean(),
  pinned: z.boolean(),
  canEdit: z.boolean(),
  settings: z.object({
    chaos: z.number().int().min(0).max(100),
    timing: z.enum(['fluent', 'original']),
    balance: z.boolean(),
    cleanup: z.boolean(),
    altered: z.boolean(),
    voiceIds: z.array(id).max(256),
    effect: effectSchema.nullish(),
  }),
  tokens: z.array(z.string().max(400)).max(400),
  pieces: z.array(pieceSchema).max(400),
  overlaps: z.array(z.object({ first: pieceId, second: pieceId, ms: z.number() })).max(80000),
  durationMs: z.number().min(0),
  missing: z.number().int().min(0),
  suggestContrast: z.boolean(),
  versions: z.array(versionSchema).max(50),
});
export type Sentence = z.infer<typeof sentenceSchema>;
/** A change's answer: the sentence, or the sentence and what the change found. */
export const changedSchema = z.union([
  z.object({ sentence: sentenceSchema, result: z.record(z.string(), z.unknown()).optional() }),
  sentenceSchema.transform((sentence) => ({ sentence, result: undefined })),
]);
export const listEntrySchema = z.object({
  id,
  title: z.string().max(200),
  text: z.string().max(400),
  ownerName: z.string().max(40),
  mine: z.boolean(),
  kept: z.boolean(),
  pinned: z.boolean(),
  updated: z.number(),
  origin: z.enum(['typed', 'performance', 'template']),
  versions: z.number().int().min(0),
  durationMs: z.number().min(0),
  missing: z.number().int().min(0),
  canEdit: z.boolean(),
});
export type SentenceEntry = z.infer<typeof listEntrySchema>;
export const namedSchema = z.object({
  id,
  name: z.string().max(30),
  recordingId: id,
  startMs: ms,
  endMs: ms,
  byName: z.string().max(40),
});
export const listSchema = z.object({
  revision: z.number().int().min(0),
  canBuild: z.boolean(),
  canKeep: z.boolean(),
  problem: z.string().max(2000).nullish(),
  sentences: z.array(listEntrySchema).max(5000),
  named: z.array(namedSchema).max(5000),
});
export type SentenceList = z.infer<typeof listSchema>;
/**
 * What this person may do with sentences (2.42 review). The list's flags are the session's settings;
 * the host's setting for this guest (library editing or Keep turned Off for them) is in the guest's
 * own permissions, as in Voices (canEditLibrary). A palette word or a sentence rendered from words
 * into the game is a part of a recording, so a guest also needs seeking for it (like a region).
 */
export function sentenceRights(
  list: Pick<SentenceList, 'canBuild' | 'canKeep'> | null | undefined,
  permissions: { guestLibraryEdit?: boolean; guestKeep?: boolean; guestSeeking?: boolean },
  host: boolean,
) {
  const build = host || permissions.guestLibraryEdit !== false;
  return {
    canBuild: (list?.canBuild ?? host) && build,
    canKeep: (list?.canKeep ?? host) && (host || (build && permissions.guestKeep === true)),
    canPlayParts: host || permissions.guestSeeking !== false,
    /** A sentence's own canEdit, with the host's setting for this guest. */
    canEdit: (canEdit: boolean) => canEdit && build,
  };
}
export const alternativesSchema = z.object({
  pieceId,
  current: z.number().int(),
  takes: z
    .array(
      z.object({
        index: z.number().int().min(0),
        recordingId: id,
        voiceId: id.nullish(),
        text: z.string().max(4000),
        durationMs: z.number(),
        title: z.string().max(200),
        altered: z.boolean(),
        current: z.boolean(),
      }),
    )
    .max(60),
  similar: z.array(z.object({ word: z.string().max(200), count: z.number().int() })).max(50),
  splices: z
    .array(
      z.object({
        index: z.number().int().min(0),
        description: z.string().max(400),
        parts: z
          .array(
            z.object({
              label: z.string().max(60).nullish(),
              voiceId: id.nullish(),
              durationMs: z.number(),
            }),
          )
          .max(4),
      }),
    )
    .max(16),
});
export type Alternatives = z.infer<typeof alternativesSchema>;
export const paletteItemSchema = z.object({
  kind: z.enum(['word', 'phrase', 'named']),
  text: z.string().max(4000),
  namedId: id.nullish(),
  recordingId: id.nullish(),
  startMs: ms,
  endMs: ms,
  voiceId: id.nullish(),
  durationMs: z.number(),
  favorite: z.boolean(),
});
export type PaletteItem = z.infer<typeof paletteItemSchema>;
export const paletteSchema = z.object({
  items: z.array(paletteItemSchema).max(200),
  activeSet: z.string().max(32),
});
export const setSchema = z.object({
  id: z.string().regex(/^(default|[a-f0-9]{32})$/),
  name: z.string().max(40),
  voiceIds: z.array(id).max(256),
  favorites: z.array(paletteRefSchema).max(200),
  templates: z.array(templateSchema).max(100),
});
export type PerformanceSet = z.infer<typeof setSchema>;
export const setsSchema = z.object({
  active: z.string().max(32),
  sets: z.array(setSchema).max(32),
});
export const performanceSchema = z.object({
  recording: z.boolean(),
  taps: z.number().int().min(0),
  sinceMs: z.number().optional(),
});
export const takeSchema = z.object({ sentenceId: id, taps: z.number().int().min(1) });

/** A palette item as the reference the companion keeps (no audio). */
export function itemRef(item: PaletteItem): PaletteRef {
  return item.namedId
    ? { namedId: item.namedId, startMs: 0, endMs: 0, text: item.text.slice(0, 100) }
    : {
        recordingId: item.recordingId ?? null,
        startMs: Math.round(item.startMs),
        endMs: Math.round(item.endMs),
        text: item.text.slice(0, 100),
      };
}
/** Whether two references are the same palette item (for favourites). */
export function sameRef(a: PaletteRef, b: PaletteRef) {
  return a.namedId || b.namedId
    ? a.namedId === b.namedId
    : a.recordingId === b.recordingId && a.startMs === b.startMs && a.endMs === b.endMs;
}

// ---- Timeline ---------------------------------------------------------------------------------

/** Pixels per second on the timeline. */
export const PX_PER_SECOND = 200;
export const pxOf = (msValue: number) => (msValue / 1000) * PX_PER_SECOND;
export const msOfPx = (px: number) => (px / PX_PER_SECOND) * 1000;
/** The grid a snapped move lands on. */
export const SNAP_MS = 50;
/** Keyboard nudges: a small step, and a larger one with Shift. */
export const NUDGE_MS = 10;
export const NUDGE_LARGE_MS = 100;

/**
 * Where a moved piece lands. Free by default (to the millisecond); with the snap modifier it lands on
 * the nearest edge of another piece within 60 ms, otherwise on the 50 ms grid. Never before 0.
 */
export function placeAt(offsetMs: number, snap: boolean, others: { start: number; end: number }[]) {
  const free = Math.max(0, Math.round(offsetMs));
  if (!snap) return free;
  let best: number | null = null;
  for (const o of others)
    for (const edge of [o.start, o.end])
      if (
        Math.abs(edge - free) <= 60 &&
        (best === null || Math.abs(edge - free) < Math.abs(best - free))
      )
        best = edge;
  return Math.max(0, best ?? Math.round(free / SNAP_MS) * SNAP_MS);
}

export type Selection = readonly string[];
/** Click selects one piece; Ctrl/Cmd-click adds or removes it; clicking the only selected piece keeps it. */
export function select(current: Selection, pieceId: string, additive: boolean): Selection {
  if (!additive) return [pieceId];
  return current.includes(pieceId) ? current.filter((x) => x !== pieceId) : [...current, pieceId];
}
/** Pieces gone from the sentence leave the selection. */
export function prune(current: Selection, pieces: { id: string }[]): Selection {
  const ids = new Set(pieces.map((p) => p.id));
  const next = current.filter((x) => ids.has(x));
  return next.length === current.length ? current : next;
}

export type TimelineAction =
  | { kind: 'move'; deltaMs: number }
  | { kind: 'focus'; step: -1 | 1 }
  | { kind: 'delete' }
  | { kind: 'preview' }
  | { kind: 'select'; add: boolean }
  | { kind: 'clear' }
  | null;
/**
 * The timeline's keyboard (every pointer action has one): Alt+←/→ moves the selected piece by 10 ms
 * (Shift: 100 ms), ←/→ moves focus between pieces, Delete or Backspace deletes (leaving a pause),
 * Space selects the piece like a click (Shift+Space adds it to the selection, like Ctrl+click; 2.42
 * review: a piece with no neighbour could not be selected), Enter previews it privately, Escape
 * clears the selection.
 */
export function timelineKey(e: {
  key: string;
  shiftKey?: boolean;
  altKey?: boolean;
  ctrlKey?: boolean;
  metaKey?: boolean;
}): TimelineAction {
  if (e.ctrlKey || e.metaKey) return null;
  switch (e.key) {
    case 'ArrowLeft':
    case 'ArrowRight': {
      const sign = e.key === 'ArrowLeft' ? -1 : 1;
      if (e.altKey)
        return { kind: 'move', deltaMs: sign * (e.shiftKey ? NUDGE_LARGE_MS : NUDGE_MS) };
      return { kind: 'focus', step: sign };
    }
    case 'Delete':
    case 'Backspace':
      return { kind: 'delete' };
    case 'Enter':
      return { kind: 'preview' };
    case ' ':
      return { kind: 'select', add: !!e.shiftKey };
    case 'Escape':
      return { kind: 'clear' };
    default:
      return null;
  }
}

/** Pieces that overlap one another (the later-starting one cuts the earlier one off in the render). */
export function overlapping(sentence: Pick<Sentence, 'overlaps'>) {
  const ids = new Set<string>();
  for (const o of sentence.overlaps) {
    ids.add(o.first);
    ids.add(o.second);
  }
  return ids;
}

/**
 * How a missing word is marked: pulsing normally, a static marker (a dashed outline and a "!" badge,
 * no animation) when the person asked for reduced motion; and in words for screen readers either way.
 */
export function missingMarker(piece: Pick<Piece, 'text' | 'missing'>, reducedMotion: boolean) {
  return {
    className: reducedMotion ? 'missing-static' : 'missing-pulse',
    label: `Missing word “${piece.text}”: not in the voice library, plays as ${piece.missing === 'bleep' ? 'a bleep' : 'a gap'}`,
  };
}
export function prefersReducedMotion() {
  try {
    return window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  } catch {
    return false;
  }
}

/** What a piece is, in words (its accessible name on the timeline). */
export function pieceLabel(p: Piece) {
  switch (p.kind) {
    case 'pause':
      return `Pause, ${Math.round(p.durationMs)} ms`;
    case 'missing':
      return `Missing “${p.text}” (${p.missing === 'bleep' ? 'bleep' : 'gap'})`;
    case 'splice':
      return `Splice for “${p.text}”`;
    case 'named':
      return `${p.text} (nonverbal)`;
    default:
      return p.substitute ? `“${p.substitute}” for “${p.text}”` : `“${p.text}”`;
  }
}

/** Where a source has a problem (its recording left the library, or its file changed). */
export function sourceProblem(p: Piece) {
  const states = [p.source?.state, ...(p.parts ?? []).map((x) => x.state)];
  return states.includes('missing') ? 'missing' : states.includes('changed') ? 'changed' : null;
}

/** The text with each word marked by what plays it: a voice's piece, a missing word, a pause. */
export function wordMarks(sentence: Pick<Sentence, 'tokens' | 'pieces'>) {
  return sentence.tokens.map((word, i) => {
    const piece = sentence.pieces.find(
      (p) => p.tokenStart != null && p.tokenEnd != null && i >= p.tokenStart && i < p.tokenEnd,
    );
    return { word, index: i, piece: piece ?? null };
  });
}

/** Text with slots ({name}) of a template, so a person can fill them in. */
export function templateSlots(text: string) {
  return [...new Set([...text.matchAll(/\{([^{}]{1,40})\}/g)].map((m) => m[1]))];
}
export function fillTemplate(text: string, values: Record<string, string>) {
  return text.replace(/\{([^{}]{1,40})\}/g, (whole, name: string) => values[name]?.trim() || whole);
}

export const formatMs = (value: number) =>
  value >= 1000 ? `${(value / 1000).toFixed(value >= 10000 ? 0 : 1)} s` : `${Math.round(value)} ms`;
