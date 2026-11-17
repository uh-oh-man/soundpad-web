/**
 * The live waveform's glide (2.47). The page polls the newest audio levels every half second (a second on a
 * guest's page), so the live view used to jump once per poll. Between polls the view now scrolls on at the
 * speed time passes, a little behind the newest audio it has, and each poll re-anchors it without a jump.
 *
 * Times are in ms of the recording. `dataEnd` is the end of the newest levels drawn; `visEnd` is where the view
 * ends now. The view keeps about three quarters of a poll behind the data, so it never runs out of audio
 * before the next poll arrives; a small speed correction keeps it there when polls come early or late.
 * Pure, so the drawing loop only asks for the shift (`dataEnd - visEnd`) once per frame.
 */
export type Glide = {
  dataEnd: number;
  visEnd: number;
  /** The last frame time (performance.now()); 0 before the first frame. */
  last: number;
  /** How far apart new levels arrive, measured (ms of the recording). */
  interval: number;
};

/** The longest frame step counted (a hidden tab pauses frames; it then catches up by snapping). */
const MAX_STEP_MS = 100;

/** How far behind the newest audio the view aims to be. */
export const glideTarget = (g: Glide) => g.interval * 0.75;

export function glideStart(endMs: number, intervalMs: number): Glide {
  const g: Glide = { dataEnd: endMs, visEnd: endMs, last: 0, interval: intervalMs };
  g.visEnd = endMs - glideTarget(g);
  return g;
}

/**
 * New levels arrived, ending at `endMs`: the view keeps where it is, and the poll spacing is learned. `held`
 * (the pointer or keyboard is on the waveform): the view never jumps on, however far behind it falls.
 */
export function glideData(g: Glide, endMs: number, held = false): Glide {
  const advance = endMs - g.dataEnd;
  if (advance <= 0) return advance < 0 ? glideStart(endMs, g.interval) : g;
  // A long pause (a hidden tab, a stalled poll) is not a poll interval: it only re-anchors.
  const interval = advance < g.interval * 4 ? g.interval * 0.8 + advance * 0.2 : g.interval;
  const next = { ...g, dataEnd: endMs, interval };
  // Far behind (frames stopped for a while): start again just behind the new data.
  if (!held && endMs - g.visEnd > interval * 4) next.visEnd = endMs - glideTarget(next);
  return next;
}

/** One animation frame at `now`: moves the view on and returns how far it is behind the data (ms). */
export function glideStep(g: Glide, now: number): number {
  const step = g.last ? Math.min(MAX_STEP_MS, Math.max(0, now - g.last)) : 0;
  g.last = now;
  const target = glideTarget(g);
  // Let go after being held a long while: back to just behind the newest audio at once.
  if (g.dataEnd - g.visEnd > g.interval * 6) g.visEnd = g.dataEnd - target;
  const lag = g.dataEnd - g.visEnd;
  // Slightly faster when further behind than aimed for, slightly slower when closer.
  const speed = Math.min(1.5, Math.max(0.6, 1 + (lag - target) / (g.interval * 3)));
  g.visEnd = Math.min(g.dataEnd, g.visEnd + step * speed);
  return g.dataEnd - g.visEnd;
}
