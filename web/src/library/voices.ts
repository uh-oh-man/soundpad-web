/**
 * The voice library (2.41), the pure parts: what the companion answers (checked here), voice
 * avatars, the play range of a word, search filters, the combined search across Sounds, Voice clips
 * and Sentences, the review queue's keyboard, and multi-selection. See VoicesPanel.tsx.
 */
import { z } from 'zod';
import type { Command, Sound } from '../protocol';

const id = z.string().regex(/^[a-f0-9]{32}$/);
export const voiceSchema = z.object({
  id,
  name: z.string().max(40),
  color: z.string().regex(/^#[0-9a-f]{6}$/),
  count: z.number().int().nonnegative(),
});
export type Voice = z.infer<typeof voiceSchema>;
export const wantedSchema = z.object({
  id,
  voiceId: id,
  text: z.string().max(100),
  byName: z.string().max(40).optional(),
  available: z.boolean(),
  count: z.number().int().nonnegative(),
});
export const noticeSchema = z.object({
  id: z.number().int().positive(),
  kind: z.enum(['history', 'recording']),
  voiceId: id,
  voiceName: z.string().max(40),
  text: z.string().max(100),
  recordingId: id.nullish(),
  sessionId: z.string().max(64).nullish(),
  startFrame: z.number().int().nonnegative().nullish(),
  endFrame: z.number().int().nonnegative().nullish(),
  at: z.number(),
});
export type LibraryNotice = z.infer<typeof noticeSchema>;
export const indexProgressSchema = z.object({
  total: z.number().int().nonnegative(),
  indexed: z.number().int().nonnegative(),
  failed: z.number().int().nonnegative(),
  paused: z.boolean(),
  current: z.string().max(4096).nullish(),
  active: z.boolean(),
});
export const statusSchema = z.object({
  revision: z.number().int().nonnegative(),
  blocked: z.boolean(),
  problem: z.string().max(2000).nullish(),
  count: z.number().int().nonnegative(),
  unreviewed: z.number().int().nonnegative(),
  pending: z.number().int().nonnegative(),
  voices: z.array(voiceSchema).max(256),
  unassigned: z.number().int().nonnegative(),
  tags: z.array(z.string().max(40)).max(500),
  wanted: z.array(wantedSchema).max(60000),
  canEdit: z.boolean(),
  settings: z.object({
    guestLibraryEdit: z.boolean(),
    groupSpeakers: z.boolean(),
    indexCategories: z.array(z.string().max(256)).max(256).nullish(),
    indexPaused: z.boolean(),
  }),
  index: indexProgressSchema,
  worker: z.string().max(600),
  notices: z.array(noticeSchema).max(100),
});
export type LibraryStatus = z.infer<typeof statusSchema>;
export const recordingSchema = z.object({
  id,
  title: z.string().max(200),
  voiceId: id.nullish(),
  tags: z.array(z.string().max(40)).max(32),
  favorite: z.boolean(),
  correctedText: z.string().max(4000).nullish(),
  reviewed: z.boolean(),
  wordsState: z.enum(['ready', 'pending', 'failed']),
  wordsError: z.string().max(600).nullish(),
  durationMs: z.number().int().nonnegative(),
  created: z.number(),
  updated: z.number(),
  source: z.string().max(20),
  sessionId: z.string().max(64).nullish(),
  byName: z.string().max(40),
  soundId: z.string().max(64).nullish(),
  text: z.string().max(400),
  wordCount: z.number().int().nonnegative(),
});
export type Recording = z.infer<typeof recordingSchema>;
export const listSchema = z.object({
  total: z.number().int().nonnegative(),
  offset: z.number().int().nonnegative(),
  items: z.array(recordingSchema).max(200),
});
export const libraryWordSchema = z.object({
  s: z.number().int().nonnegative(),
  e: z.number().int().nonnegative(),
  t: z.string().max(200),
  u: z.boolean(),
});
export type LibraryWord = z.infer<typeof libraryWordSchema>;
export const auditSchema = z.object({
  id: z.number().int().positive(),
  group: z.number().int().positive(),
  at: z.number(),
  byName: z.string().max(40),
  target: z.string().max(20),
  targetId: z.string().max(64),
  field: z.string().max(40),
  before: z.string().max(100000).nullish(),
  after: z.string().max(100000).nullish(),
  undoOf: z.number().int().nullish(),
});
export type AuditEntry = z.infer<typeof auditSchema>;
export const getSchema = z.object({
  recording: recordingSchema,
  words: z.array(libraryWordSchema).max(20000),
  audit: z.array(auditSchema).max(500),
});
export const contextWordSchema = z.object({
  i: z.number().int().nonnegative(),
  s: z.number().int().nonnegative(),
  e: z.number().int().nonnegative(),
  t: z.string().max(200),
  hit: z.boolean(),
  u: z.boolean(),
});
export type ContextWord = z.infer<typeof contextWordSchema>;
export const searchResultSchema = z.object({
  type: z.enum(['clip', 'sound']),
  id: z.string().max(64),
  soundId: z.string().max(64).nullish(),
  title: z.string().max(4096),
  voiceId: id.nullish(),
  durationMs: z.number().int().nonnegative(),
  matches: z.number().int().nonnegative(),
  match: z.number().int(),
  length: z.number().int().nonnegative(),
  context: z.array(contextWordSchema).max(64),
});
export type SearchResult = z.infer<typeof searchResultSchema>;
export const searchSchema = z.object({
  total: z.number().int().nonnegative(),
  offset: z.number().int().nonnegative(),
  results: z.array(searchResultSchema).max(200),
  sentences: z.array(z.unknown()).max(0),
});
export const vocabularySchema = z.object({
  total: z.number().int().nonnegative(),
  offset: z.number().int().nonnegative(),
  words: z
    .array(z.object({ text: z.string().max(200), count: z.number().int().positive() }))
    .max(500),
  wanted: z
    .array(
      z.object({
        id,
        text: z.string().max(100),
        available: z.boolean(),
        count: z.number().int().nonnegative(),
      }),
    )
    .max(200),
});
export const historySearchSchema = z.object({
  sessionId: z.string().max(64),
  matches: z
    .array(
      z.object({
        startFrame: z.number().int().nonnegative(),
        endFrame: z.number().int().nonnegative(),
        text: z.string().max(4000),
        context: z.string().max(8000),
      }),
    )
    .max(50),
});
export const speakerGroupsSchema = z.object({
  enabled: z.boolean(),
  groups: z
    .array(
      z.object({
        id: z.number().int().positive(),
        label: z.string().max(60),
        name: z.string().max(40).nullish(),
        voiceId: id.nullish(),
        color: z.string().regex(/^#[0-9a-f]{6}$/),
        phrases: z.number().int().nonnegative(),
      }),
    )
    .max(64),
  unassigned: z.number().int().nonnegative(),
});
export type SpeakerGroups = z.infer<typeof speakerGroupsSchema>;

/** Up to two letters for a voice's avatar: the first letters of its first two words, or its first two letters. */
export function initials(name: string) {
  const words = name.trim().split(/\s+/).filter(Boolean);
  if (words.length === 0) return '?';
  const letters =
    words.length > 1
      ? [...words[0]].slice(0, 1).concat([...words[1]].slice(0, 1))
      : [...words[0]].slice(0, 2);
  return letters.join('').toLocaleUpperCase();
}

/** The voice a recording belongs to, or null (Unassigned). */
export const voiceOf = (voices: Voice[], voiceId: string | null | undefined) =>
  voices.find((v) => v.id === voiceId) ?? null;

/** Padding around a single word when it is cut (as the companion stores boundaries unpadded): 20 ms before, 10 after. */
export const PAD_BEFORE_MS = 20;
export const PAD_AFTER_MS = 10;
/** The range that plays one word (or several, first to last), padded, in whole milliseconds. */
export function playRange(first: { s: number; e: number }, last: { s: number; e: number } = first) {
  return {
    positionMs: Math.max(0, Math.round(first.s - PAD_BEFORE_MS)),
    endMs: Math.round(Math.max(last.e, first.s + 1) + PAD_AFTER_MS),
  };
}

/** The search box and filters of the library. voice: '' all, 'none' unassigned, or a voice id. */
export type Filters = { voice: string; tag: string; favorite: boolean; query: string };
export const NO_FILTERS: Filters = { voice: '', tag: '', favorite: false, query: '' };
/** The command fields that ask for those filters (none of them when not set). */
export function filterFields(f: Filters): Partial<Command> {
  return {
    ...(f.voice === 'none' ? { unassigned: true } : f.voice ? { voiceId: f.voice } : {}),
    ...(f.tag && { tag: f.tag }),
    ...(f.favorite && { favorite: true }),
  };
}
export const hasFilters = (f: Filters) => !!(f.voice || f.tag || f.favorite);

/** What a combined search shows, by type: Soundpad sounds, voice-library clips and sentences (Stage F2). */
export type ResultType = 'sound' | 'clip' | 'sentence';
export const TYPE_LABELS: Record<ResultType, string> = {
  sound: 'Sound',
  clip: 'Voice clip',
  sentence: 'Sentence',
};
export type Combined =
  | { type: 'sound'; key: string; soundId: string; title: string; words: SearchResult | null }
  | { type: 'clip'; key: string; result: SearchResult }
  | { type: 'sentence'; key: string; title: string };

/**
 * One list across the types (2.41): Soundpad sounds whose name contains the text, the indexed sounds
 * whose words say it (one entry per sound, with the words), voice clips by their words or title, and
 * sentences (none until Stage F2). `types` filters it; the order is sounds by name, then clips as the
 * companion sorted them (newest first).
 */
export function combineResults(
  query: string,
  sounds: Sound[],
  results: SearchResult[],
  types: ReadonlySet<ResultType>,
  limit = 60,
): Combined[] {
  const text = query.trim().toLocaleLowerCase();
  const out: Combined[] = [];
  if (types.has('sound')) {
    const byWords = new Map(results.filter((r) => r.type === 'sound').map((r) => [r.id, r]));
    const named = text
      ? sounds.filter((s) => !s.internalVariant && s.name.toLocaleLowerCase().includes(text))
      : [];
    const seen = new Set<string>();
    for (const s of [...named].sort((a, b) => a.name.localeCompare(b.name))) {
      seen.add(s.id);
      out.push({
        type: 'sound',
        key: 'sound:' + s.id,
        soundId: s.id,
        title: s.name,
        words: byWords.get(s.id) ?? null,
      });
    }
    for (const r of byWords.values())
      if (!seen.has(r.id))
        out.push({ type: 'sound', key: 'sound:' + r.id, soundId: r.id, title: r.title, words: r });
  }
  if (types.has('clip'))
    for (const r of results)
      if (r.type === 'clip') out.push({ type: 'clip', key: 'clip:' + r.id, result: r });
  return out.slice(0, limit);
}

/** How many results of each type (for the filter chips). */
export function countTypes(list: Combined[]) {
  const counts: Record<ResultType, number> = { sound: 0, clip: 0, sentence: 0 };
  for (const item of list) counts[item.type]++;
  return counts;
}

/**
 * The review queue's keys: Space previews, Enter keeps (marks reviewed) and moves on, S or → skips,
 * ← goes back, F toggles favourite, 1–9 give the voice of that number, Escape leaves. Typing in a
 * field keeps its keys (null).
 */
export type ReviewAction =
  | { kind: 'preview' }
  | { kind: 'keep' }
  | { kind: 'skip' }
  | { kind: 'back' }
  | { kind: 'favorite' }
  | { kind: 'voice'; index: number }
  | { kind: 'leave' };
export function reviewAction(
  key: string,
  { typing = false, ctrl = false }: { typing?: boolean; ctrl?: boolean } = {},
): ReviewAction | null {
  if (ctrl) return null;
  if (typing) return key === 'Escape' ? { kind: 'leave' } : null;
  if (key === ' ' || key === 'p' || key === 'P') return { kind: 'preview' };
  if (key === 'Enter') return { kind: 'keep' };
  if (key === 's' || key === 'S' || key === 'ArrowRight') return { kind: 'skip' };
  if (key === 'ArrowLeft') return { kind: 'back' };
  if (key === 'f' || key === 'F') return { kind: 'favorite' };
  if (/^[1-9]$/.test(key)) return { kind: 'voice', index: Number(key) - 1 };
  if (key === 'Escape') return { kind: 'leave' };
  return null;
}

/** The review queue: recordings to look at, where we are, and which were kept or skipped. */
export type Queue = { ids: string[]; at: number; kept: string[]; skipped: string[] };
export const startQueue = (ids: string[]): Queue => ({ ids, at: 0, kept: [], skipped: [] });
export function stepQueue(q: Queue, action: 'keep' | 'skip' | 'back'): Queue {
  const current = q.ids[q.at];
  if (action === 'back') return { ...q, at: Math.max(0, q.at - 1) };
  if (current === undefined) return q;
  const next = { ...q, at: Math.min(q.ids.length, q.at + 1) };
  if (action === 'keep')
    return {
      ...next,
      kept: [...q.kept.filter((x) => x !== current), current],
      skipped: q.skipped.filter((x) => x !== current),
    };
  return { ...next, skipped: [...q.skipped.filter((x) => x !== current), current] };
}
/** New unreviewed recordings join the end of the queue; ones already in it keep their place. */
export function extendQueue(q: Queue, ids: string[]): Queue {
  const known = new Set(q.ids);
  const added = ids.filter((x) => !known.has(x));
  return added.length ? { ...q, ids: [...q.ids, ...added] } : q;
}
export const queueDone = (q: Queue) => q.at >= q.ids.length;

/** Multi-selection for bulk edits: a click toggles; Shift-click selects everything from the last one clicked. */
export type Picked = { ids: string[]; anchor: string | null };
export const NOTHING_PICKED: Picked = { ids: [], anchor: null };
export function pick(p: Picked, order: string[], target: string, shift: boolean): Picked {
  if (shift && p.anchor && order.includes(p.anchor) && order.includes(target)) {
    const [a, b] = [order.indexOf(p.anchor), order.indexOf(target)].sort((x, y) => x - y);
    const range = order.slice(a, b + 1);
    return { ids: [...new Set([...p.ids, ...range])], anchor: target };
  }
  return {
    ids: p.ids.includes(target) ? p.ids.filter((x) => x !== target) : [...p.ids, target],
    anchor: target,
  };
}

/** A tag typed by a person: trimmed, at most 40 characters, no control characters ('' when nothing is left). */
export const cleanTag = (value: string) =>
  Array.from(value.replace(/[\p{Cc}\p{Cf}]/gu, '').trim())
    .slice(0, 40)
    .join('');

/** What an audit entry says, in words ("Alice set the voice", "Bob added tags"). */
export function describeAudit(e: AuditEntry, voices: Voice[]) {
  const value = (json: string | null | undefined) => {
    if (json == null) return null;
    try {
      return JSON.parse(json) as unknown;
    } catch {
      return null;
    }
  };
  const after = value(e.after);
  const undo = e.undoOf ? ' (Undo)' : '';
  switch (`${e.target}.${e.field}`) {
    case 'recording.title':
      return `${e.byName} renamed it “${String(after)}”${undo}`;
    case 'recording.voice':
      return after
        ? `${e.byName} set the voice to ${voiceOf(voices, String(after))?.name ?? 'a voice'}${undo}`
        : `${e.byName} cleared the voice${undo}`;
    case 'recording.tags':
      return `${e.byName} set the tags: ${(Array.isArray(after) ? after : []).join(', ') || 'none'}${undo}`;
    case 'recording.favorite':
      return `${e.byName} ${after ? 'made it a favourite' : 'took it out of favourites'}${undo}`;
    case 'recording.correctedText':
      return `${e.byName} ${after ? 'corrected the text' : 'cleared the corrected text'}${undo}`;
    case 'recording.reviewed':
      return `${e.byName} ${after ? 'reviewed it' : 'marked it for review'}${undo}`;
    case 'recording.record':
      return `${e.byName} ${e.after ? 'added it to the library' : 'took it out of the library'}${undo}`;
    default:
      return `${e.byName} changed ${e.field}${undo}`;
  }
}

/** "1.2 s", "45 s", "2:05". */
export function formatDuration(ms: number) {
  if (ms < 10000) return `${(ms / 1000).toFixed(1)} s`;
  if (ms < 60000) return `${Math.round(ms / 1000)} s`;
  const s = Math.round(ms / 1000);
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
}
