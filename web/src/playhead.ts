import type { Snapshot } from './protocol';

/**
 * Where the playhead is between two playback reports (Stage G, 2.43). The companion reports a position
 * now and then; between reports the page moves the playhead on by the time that passed. These are the
 * pure parts of `usePlayhead` (Waveform.tsx), shared with the header ribbon so both read the same clock.
 */
export type PlaybackReport = Pick<
  Snapshot['playback'],
  'status' | 'positionMs' | 'epoch' | 'positionRevision'
>;

/** A reported position and the moment (`performance.now()`) it was taken as the playhead. */
export type PlayheadBase = {
  position: number;
  at: number;
  epoch: string | null | undefined;
  status: Snapshot['playback']['status'];
  positionRevision: number | undefined;
};

/**
 * A report within this many milliseconds of where the playhead already was is taken as the same clock:
 * the playhead never jumps back by a report's lag. A larger difference is a real jump and is followed.
 */
export const CONTINUITY_MS = 350;

/**
 * The base for a new report. With `previous` (the base before it) a report of the same playback (same
 * epoch and seek revision, still playing) that is within `CONTINUITY_MS` of the projected position
 * keeps the further of the two, so the playhead does not stutter backwards. A new epoch, a seek (new
 * `positionRevision`) or any other status starts from the report as it is.
 */
export function makeBase(
  playback: PlaybackReport,
  now: number,
  previous?: PlayheadBase,
): PlayheadBase {
  const sample = playback.positionMs ?? 0;
  let position = sample;
  if (
    previous &&
    previous.epoch === playback.epoch &&
    previous.positionRevision === playback.positionRevision &&
    playback.status === 'PLAYING'
  ) {
    const projected = previous.position + (previous.status === 'PLAYING' ? now - previous.at : 0);
    if (Math.abs(sample - projected) < CONTINUITY_MS) position = Math.max(sample, projected);
  }
  return {
    position,
    at: now,
    epoch: playback.epoch,
    status: playback.status,
    positionRevision: playback.positionRevision,
  };
}

/**
 * The playhead at `now`: the base moved on by the time since it while playing, never below 0 or past
 * the duration. While the page is hidden (`hidden`) it stays where the base is, as before.
 */
export function project(
  base: PlayheadBase,
  now: number,
  durationMs: number | null | undefined,
  hidden = false,
) {
  const value = Math.min(
    durationMs ?? Infinity,
    base.position + (base.status === 'PLAYING' && !hidden ? now - base.at : 0),
  );
  return Math.max(0, value);
}
