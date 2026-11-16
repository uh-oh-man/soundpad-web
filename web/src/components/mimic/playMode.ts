import {
  continuousParts,
  msToFrames,
  type FrameRange,
  type Timeline,
  type Word,
} from '../../mimic/history';
import { MIN_MS } from '../../mimic/selection';

// Mimic history playback (2.45): where a click on a word or on the waveform plays, and which words are playing.
// Pure helpers, so the unit tests use them directly; MimicHistory.tsx draws the switch and sends the commands.

/** Where a click plays: heard privately in this browser, or played into the game through Soundpad. */
export type PlayMode = 'preview' | 'game';
/** Remembered per person (this browser), like the zoom; Preview until they choose otherwise. */
export const PLAY_MODE_KEY = 'soundpad-web:mimic-play-mode';

type Store = Pick<Storage, 'getItem' | 'setItem'>;
const storage = (): Store | null => {
  try {
    return typeof localStorage === 'undefined' ? null : localStorage;
  } catch {
    return null;
  }
};

/** The remembered choice; Preview when nothing (or something unknown) is stored, or storage is unavailable. */
export function readPlayMode(store: Store | null = storage()): PlayMode {
  try {
    return store?.getItem(PLAY_MODE_KEY) === 'game' ? 'game' : 'preview';
  } catch {
    return 'preview';
  }
}
export function writePlayMode(mode: PlayMode, store: Store | null = storage()) {
  try {
    store?.setItem(PLAY_MODE_KEY, mode);
  } catch {
    // A convenience only: the choice still applies until the window closes.
  }
}

/**
 * Why this person cannot play into the game from Mimic now, or null when they can. Playing needs the host's
 * remote playback (`canPlay` already holds it) and, for a guest, guest Mimic; the companion and the host page
 * check both again.
 */
export function gameBlocked(p: {
  host: boolean;
  canPlay: boolean;
  guestMimic: boolean;
  held: boolean;
}): string | null {
  if (!p.canPlay)
    return p.host ? 'Playback is not available' : 'The host has not allowed guests to play';
  if (!p.host && !p.guestMimic) return 'The host has not allowed guest Mimic';
  if (p.held) return 'The host paused guest playback';
  return null;
}
/** What a click does now: the remembered mode, or Preview while playing into the game is not allowed. */
export const effectiveMode = (mode: PlayMode, blocked: string | null): PlayMode =>
  blocked ? 'preview' : mode;

/** A waveform click plays from that point to the end of the audio there, at most this long. */
export const PLAY_FROM_MAX_MS = 10000;

/**
 * A waveform click as a range to play: from the clicked frame to the end of the continuous audio this person may
 * hear there (never across a gap or into what they may not see), at most `maxMs`. A point before a guest joined,
 * in a gap or not recorded yet gives a reason instead, as `wordCut` does for words.
 */
export function playFrom(
  frame: number,
  t: Pick<Timeline, 'available' | 'gaps' | 'rate' | 'liveFrame' | 'fromFrame'>,
  maxMs = PLAY_FROM_MAX_MS,
): { range: FrameRange } | { reason: string } {
  const at = Math.max(0, Math.round(frame));
  const part = continuousParts(t).find((p) => at >= p.startFrame && at < p.endFrame);
  if (!part) {
    if (t.fromFrame !== null && at < t.fromFrame)
      return { reason: 'That part was recorded before you joined. Ask the host to share it.' };
    if (at >= t.liveFrame) return { reason: 'Nothing has been recorded there yet.' };
    return { reason: 'There is no recording there: it is a gap, or no longer in the history.' };
  }
  const end = Math.min(part.endFrame, at + msToFrames(maxMs, t.rate));
  if (end - at < msToFrames(MIN_MS, t.rate))
    return { reason: `Too little audio is left after that point to play (at least ${MIN_MS} ms).` };
  return { range: { startFrame: at, endFrame: end } };
}

/** How far (CSS pixels) a pointer may move between press and release and still be a click, not a drag. */
export const CLICK_PX = 4;
/** A press and release on the waveform is a click (it plays) unless it moved or started on a selection edge. */
export const isClick = (dx: number, dy: number, onEdge: boolean) =>
  !onEdge && Math.hypot(dx, dy) < CLICK_PX;

/** Where in the range the audio is, `elapsedMs` after it began; never past the range's end. */
export const playheadFrame = (range: FrameRange, elapsedMs: number, rate: number) =>
  Math.min(range.endFrame, range.startFrame + msToFrames(Math.max(0, elapsedMs), rate));

/**
 * The words carrying the `playing` class. Nothing until the audio has started (`frame` null). Normally it is the
 * word being said: the last word of the range that has begun by `frame` (the first while the padding before it
 * plays, and the previous one through a pause), so a word stays lit for the whole of its own play. With reduced
 * motion every word of the range is lit, without moving, until the play ends.
 */
export function playingWordIds(
  words: Pick<Word, 'id' | 'start' | 'end'>[],
  range: FrameRange | null,
  frame: number | null,
  reduced: boolean,
): Set<number> {
  const ids = new Set<number>();
  if (!range || frame === null) return ids;
  const inside = words
    .filter(
      (w) => (w.start + w.end) / 2 >= range.startFrame && (w.start + w.end) / 2 <= range.endFrame,
    )
    .sort((a, b) => a.start - b.start);
  if (!inside.length) return ids;
  if (reduced) {
    for (const w of inside) ids.add(w.id);
    return ids;
  }
  let current = inside[0];
  for (const w of inside) if (w.start <= frame) current = w;
  ids.add(current.id);
  return ids;
}

/** How long after a game play's expected end its highlight is kept, for Soundpad's own start delay. */
export const END_SLACK_MS = 400;

/**
 * Whether a play into the game has ended, from the page's latest playback report: it was seen playing (its
 * epoch) and now something else plays or it stopped (Stop, Panic, the end), Panic holds playback, or it has
 * run past its length. `seen` is carried from one check to the next, because the report naming the new play
 * can arrive after the command's answer.
 */
export function gamePlayEnded(
  play: { epoch: string; startedAt: number; lengthMs: number },
  report: { epoch?: string | null; status: string },
  suspended: boolean,
  seen: boolean,
  now: number,
): { ended: boolean; seen: boolean } {
  const mine = report.epoch === play.epoch;
  const saw = seen || mine;
  if (suspended) return { ended: true, seen: saw };
  if (saw && (!mine || report.status === 'STOPPED')) return { ended: true, seen: saw };
  return { ended: now - play.startedAt > play.lengthMs + END_SLACK_MS, seen: saw };
}

/** Clips this page made for a range into the game, by recording and frames, so a repeat click reuses its clip. */
export const clipKey = (sessionId: string, r: FrameRange) =>
  `${sessionId}:${Math.round(r.startFrame)}-${Math.round(r.endFrame)}`;
/** True when that clip is still in Temp and ready, so it can be played again instead of making another. */
export function reusableClip(
  assets: { id: string; hasTemporary: boolean; normal: { ready: boolean } | null }[] | undefined,
  assetId: string | undefined,
) {
  if (!assets || !assetId) return false;
  const asset = assets.find((a) => a.id === assetId);
  return !!asset && asset.hasTemporary && asset.normal?.ready === true;
}
