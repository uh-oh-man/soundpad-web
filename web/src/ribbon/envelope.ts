import type { Snapshot } from '../protocol';

/**
 * The header ribbon's signal (Stage G spec 6.3 and 6.4): which playback it may follow, and how loud
 * the playing clip is around the playhead, read from the waveform peaks the page already caches. Pure:
 * no clock, no DOM, no audio. Never fed from a private preview, Mimic's capture or a microphone.
 */

/** One level of cached peaks: `bins` min/max pairs over the analysed span. */
export type PeakLevel = { bins: number; peaks: ArrayLike<number> };
/** What `peekWaveform` returns, as far as the ribbon reads it. */
export type PeakEntry = {
  status: 'ready' | 'loading' | 'unavailable';
  durationMs?: number | null;
  analyzedMs?: number | null;
  levels?: PeakLevel[] | null;
};

/** Ids of things that play from a rendered slot and have no cached peaks of their own. */
export const isComposed = (soundId: string) =>
  soundId.startsWith('sentence:') || soundId.startsWith('palette:');

/**
 * The identity the ribbon follows, `host:sound:epoch`, or null. Only a web play the companion
 * accepted (it carries an epoch), that is playing and not held by the host's Panic, has one, so a
 * replaced or stale play can never animate the ribbon.
 */
export function ribbonKey(
  playback: Snapshot['playback'],
  hostId: string | null | undefined,
  held: boolean,
): string | null {
  if (
    held ||
    !hostId ||
    playback.status !== 'PLAYING' ||
    playback.identitySource !== 'web' ||
    !playback.soundId ||
    !playback.epoch
  )
    return null;
  return `${hostId}:${playback.soundId}:${playback.epoch}`;
}

/**
 * The peaks to sample at `positionMs` and the span they cover, or null when there are none to trust:
 * ready peaks cover the whole sound; while the host is still analysing, the partial peaks cover only
 * what was analysed (`analyzedMs`) and are used only while the playhead is inside that part.
 */
export function usablePeaks(entry: PeakEntry | undefined, positionMs: number) {
  if (!entry?.levels?.length) return null;
  if (entry.status === 'ready' && entry.durationMs)
    return { levels: entry.levels, spanMs: entry.durationMs };
  if (entry.status === 'loading' && entry.analyzedMs && positionMs <= entry.analyzedMs)
    return { levels: entry.levels, spanMs: entry.analyzedMs };
  return null;
}

export type RibbonMode = 'idle' | 'waiting' | 'live';
export type RibbonTarget =
  | { mode: 'idle'; key: null }
  | { mode: 'waiting'; key: string | null }
  | { mode: 'live'; key: string; level: PeakLevel; spanMs: number };

/**
 * What the ribbon should show now. `live` follows the peaks of the identified clip; `waiting` is a
 * play whose shape is unknown (a sentence or palette word, peaks not analysed yet or past what was
 * analysed, or Soundpad playing something the page did not start, which has no key); `idle` otherwise.
 */
export function ribbonTarget(
  playback: Snapshot['playback'],
  hostId: string | null | undefined,
  held: boolean,
  entry: PeakEntry | undefined,
  positionMs: number,
): RibbonTarget {
  const key = ribbonKey(playback, hostId, held);
  if (!key)
    return playback.status === 'PLAYING' && !held
      ? { mode: 'waiting', key: null }
      : { mode: 'idle', key: null };
  if (isComposed(playback.soundId!)) return { mode: 'waiting', key };
  const peaks = usablePeaks(entry, positionMs);
  if (!peaks) return { mode: 'waiting', key };
  return { mode: 'live', key, level: pickLevel(peaks.levels, peaks.spanMs), spanMs: peaks.spanMs };
}

/** The coarsest level whose bins are at most 10 ms long, else the finest one there is. */
export function pickLevel(levels: PeakLevel[], spanMs: number): PeakLevel {
  const sorted = [...levels].sort((a, b) => a.bins - b.bins);
  return sorted.find((l) => spanMs / l.bins <= 10) ?? sorted[sorted.length - 1];
}

const peakCache = new WeakMap<object, number>();
/** The loudest peak of the whole level (0..1), at least 0.25, so a quiet clip still moves the ribbon. */
export function clipPeak(level: PeakLevel) {
  const key = level.peaks as unknown as object;
  const known = peakCache.get(key);
  if (known !== undefined) return known;
  let max = 0;
  for (let i = 0; i < level.peaks.length; i++) max = Math.max(max, Math.abs(level.peaks[i]));
  const value = Math.max(0.25, max / 32767);
  peakCache.set(key, value);
  return value;
}

/**
 * The envelope around `positionMs` (±`halfWindowMs`): the loudest peak there, relative to the clip's
 * own loudest, with a square-root lift so quiet passages still show. 0..1.
 */
export function envelopeAt(
  level: PeakLevel,
  spanMs: number,
  positionMs: number,
  halfWindowMs: number,
) {
  const binMs = spanMs / level.bins;
  if (!(binMs > 0)) return 0;
  const i0 = Math.max(0, Math.floor((positionMs - halfWindowMs) / binMs));
  const i1 = Math.min(level.bins - 1, Math.ceil((positionMs + halfWindowMs) / binMs));
  let m = 0;
  for (let i = i0; i <= i1; i++)
    m = Math.max(m, Math.abs(level.peaks[2 * i]), Math.abs(level.peaks[2 * i + 1]));
  return Math.sqrt(Math.min(1, m / 32767 / clipPeak(level)));
}

/** The fast envelope (±20 ms, hits) and the slow one (±300 ms, the passage) at `positionMs`. */
export function envelopes(level: PeakLevel, spanMs: number, positionMs: number) {
  const fast = envelopeAt(level, spanMs, positionMs, 20);
  let sum = 0;
  for (let k = -6; k <= 6; k++) sum += envelopeAt(level, spanMs, positionMs + k * 50, 25);
  return { fast, slow: sum / 13 };
}

export type Ripple = { t0: number; amp: number };
/** The smoothed level, the ripples still travelling, and when the last one started (ms). */
export type FollowState = { level: number; ripples: Ripple[]; lastRipple: number };

export const ATTACK_MS = 30;
export const RELEASE_MS = 350;
/** At most one ripple per this long: no more than 3 a second (WCAG 2.3.1, three flashes). */
export const RIPPLE_GAP_MS = 350;
export const RIPPLE_LIFE_MS = 1200;
const RIPPLE_THRESHOLD = 0.22;

export const freshFollow = (): FollowState => ({ level: 0, ripples: [], lastRipple: -Infinity });

/** Moves the level toward `target`: quickly up (30 ms), slowly down (350 ms). */
export function follow(state: FollowState, target: number, dtMs: number) {
  const tau = target > state.level ? ATTACK_MS : RELEASE_MS;
  state.level += (target - state.level) * (1 - Math.exp(-Math.max(0, dtMs) / tau));
  return state.level;
}

/** Starts a ripple when a hit stands out from its passage, at most one per 350 ms; drops old ones. */
export function detectRipple(state: FollowState, fast: number, slow: number, now: number) {
  if (fast - slow > RIPPLE_THRESHOLD && now - state.lastRipple >= RIPPLE_GAP_MS) {
    state.ripples.push({ t0: now, amp: Math.min(0.6, (fast - slow) * 1.5) });
    state.lastRipple = now;
  }
  state.ripples = state.ripples.filter((r) => now - r.t0 < RIPPLE_LIFE_MS);
  return state.ripples;
}
