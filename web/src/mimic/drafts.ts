import { z } from 'zod';
import { formatClock, framesToMs, type FrameRange } from './history';

/**
 * Drafts (2.40): each person's editable selections on the recording history. The companion keeps them
 * for the session and pins their audio; this module is the pure part of the website's side.
 */
export const draftSchema = z.object({
  id: z.string().regex(/^[a-f0-9]{32}$/),
  startFrame: z.number().int().nonnegative(),
  endFrame: z.number().int().nonnegative(),
  name: z.string().max(200).nullish(),
  createdUtcMs: z.number(),
  updatedUtcMs: z.number(),
  sessionId: z.string().max(64),
  inTemp: z.boolean(),
});
export type Draft = z.infer<typeof draftSchema>;
export const bandSchema = z.object({
  id: z.string().regex(/^[a-f0-9]{32}$/),
  ownerName: z.string().max(80),
  tag: z.string().max(16),
  startFrame: z.number().int().nonnegative(),
  endFrame: z.number().int().nonnegative(),
  name: z.string().max(200).nullish(),
});
export type Band = z.infer<typeof bandSchema>;
export const draftListSchema = z.object({
  drafts: z.array(draftSchema).max(200),
  others: z.array(bandSchema).max(2000),
  sessionId: z.string().max(64),
});
export type DraftList = z.infer<typeof draftListSchema>;
/** What draftKeep and draftPlay answer. */
export const draftClipSchema = z.object({
  assetId: z.string().max(64).nullish(),
  played: z.boolean().optional(),
  draftId: z.string().max(64).optional(),
  closed: z.boolean().optional(),
});

/** Colours for other people's selections; picked by the companion's tag, which names nobody. */
export const BAND_COLORS = ['#f2a65a', '#7fd1ae', '#c792ea', '#f07178', '#82aaff', '#ffcb6b'];
export function bandColor(tag: string) {
  let hash = 0;
  for (const ch of tag) hash = (hash * 31 + ch.charCodeAt(0)) >>> 0;
  return BAND_COLORS[hash % BAND_COLORS.length];
}

/** "Alex's selection", as the band is labelled. */
export const bandLabel = (band: Pick<Band, 'ownerName'>) => `${band.ownerName}’s selection`;

/** How a draft is listed in the draft-history dropdown: its name, else its time and length. */
export function draftLabel(draft: Draft, rate: number) {
  const seconds = (framesToMs(draft.endFrame - draft.startFrame, rate) / 1000).toFixed(1);
  return `${draft.name?.trim() || `Draft at ${formatClock(draft.createdUtcMs)}`} · ${seconds} s`;
}

/** Most recent first, as the companion sends them; kept stable if a reply came out of order. */
export const byRecent = (drafts: Draft[]) =>
  [...drafts].sort((a, b) => b.updatedUtcMs - a.updatedUtcMs);

/** Other people's selections that are new since `seen` (for a quiet notice when someone captures). */
export function newBands(seen: ReadonlySet<string>, bands: Band[]) {
  return bands.filter((band) => !seen.has(band.id));
}

/** A band as a waveform selection in ms of the window that starts at `start` (a frame). */
export const bandSelection = (band: FrameRange, start: number, rate: number) => ({
  from: framesToMs(band.startFrame - start, rate),
  to: framesToMs(band.endFrame - start, rate),
});

/** True when a draft's frames still describe the same audio as `range` (an edge was not moved). */
export const sameRange = (a: FrameRange | null | undefined, b: FrameRange | null | undefined) =>
  !!a && !!b && a.startFrame === b.startFrame && a.endFrame === b.endFrame;

/** Storage figures (budgetStatus, 2.40) as a short text: "1.2 of 100 MB". */
export function formatMb(bytes: number, limit?: number) {
  const mb = (n: number) => {
    const value = n / (1024 * 1024);
    return value >= 10 ? Math.round(value).toString() : value.toFixed(1);
  };
  return limit === undefined ? `${mb(bytes)} MB` : `${mb(bytes)} of ${mb(limit)} MB`;
}

export const usageSchema = z.object({
  id: z.string().max(128),
  name: z.string().max(80),
  tempBytes: z.number(),
  tempLimitBytes: z.number(),
  keptBytes: z.number(),
  keptLimitBytes: z.number(),
  paused: z.boolean(),
  waiting: z.enum(['temp', 'kept']).nullish(),
});
export type Usage = z.infer<typeof usageSchema>;
export const budgetRequestSchema = z.object({
  guestId: z.string().max(128),
  name: z.string().max(80),
  kind: z.enum(['temp', 'kept']),
  neededBytes: z.number(),
  at: z.number(),
});
export type BudgetRequest = z.infer<typeof budgetRequestSchema>;
export const keepSettingsSchema = z.object({
  guestKeep: z.boolean(),
  destination: z.string().max(1024).nullish(),
  categoryId: z.number().int().nullish(),
  categoryName: z.string().max(256).nullish(),
  tempMb: z.number().int(),
  keptMb: z.number().int(),
  budgetMode: z.enum(['session', 'total']),
});
export type KeepSettings = z.infer<typeof keepSettingsSchema>;
/** The host's budgetStatus answer. */
export const hostBudgetSchema = z.object({
  settings: keepSettingsSchema,
  guests: z.array(usageSchema).max(1024),
  requests: z.array(budgetRequestSchema).max(1024),
});
/** A guest's budgetStatus answer: only their own figures. */
export const guestBudgetSchema = z.object({
  guestKeep: z.boolean(),
  own: usageSchema,
  budgetMode: z.enum(['session', 'total']),
});

/**
 * What the Mimic window shows of the drafts (2.40): this person's drafts for the history on screen
 * (most recent first), the other people's selections, the open draft, and whether the open id points
 * at a draft that is gone (kept and closed, discarded, or from a history that was discarded).
 */
export function draftState(
  list: DraftList | null,
  sessionId: string | null,
  openId: string | null,
  enabled = true,
) {
  const listed = enabled && !!list && !!sessionId && list.sessionId === sessionId;
  const mine = listed ? byRecent(list.drafts) : [];
  const others = listed ? list.others : [];
  const open = openId ? (mine.find((d) => d.id === openId) ?? null) : null;
  return { listed, mine, others, open, gone: !!openId && listed && !open };
}
