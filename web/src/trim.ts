/** Pure helpers for dragging a start/end range on Edit copy's waveform (2.19.0). Times in seconds. */

export type TrimGrab = 'from' | 'to' | 'new';

/** Which part a press at `t` grabs: an edge within `tolerance`, otherwise a new range. */
export function trimGrab(t: number, from: number, to: number, tolerance: number): TrimGrab {
  const dFrom = Math.abs(t - from),
    dTo = Math.abs(t - to);
  if (Math.min(dFrom, dTo) > tolerance) return 'new';
  // On a tiny range both edges are near: take the closer one, and the end when pressed past it.
  if (dFrom === dTo) return t > to ? 'to' : 'from';
  return dFrom < dTo ? 'from' : 'to';
}

const round = (s: number) => Math.round(s * 100) / 100;
const clamp = (s: number, length: number) => Math.min(Math.max(0, s), length);

/**
 * The range after dragging to `t`. A new range runs between where the press began (`anchor`) and
 * `t`, in either direction; an edge moves on its own and may cross the other one, which swaps them.
 */
export function trimDrag(
  grab: TrimGrab,
  t: number,
  range: { from: number; to: number },
  anchor: number,
  length: number,
) {
  const at = clamp(t, length);
  const fixed = grab === 'new' ? clamp(anchor, length) : grab === 'from' ? range.to : range.from;
  return { from: round(Math.min(fixed, at)), to: round(Math.max(fixed, at)) };
}
