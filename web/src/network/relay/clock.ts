/**
 * A clock for measuring intervals that never runs backwards, built on any millisecond clock.
 *
 * `Date.now()` steps when the computer's time is corrected. If timeouts and keep-alives used it
 * directly, a step backwards would silence the keep-alives (the peer would then report us lost) and
 * stretch every pause. This clock starts at 0 and only adds the forward movement of the clock it
 * wraps. A step forwards still counts as time passed, which at worst ends connections early; they
 * reconnect by themselves.
 */
export function steadyClock(now: () => number) {
  let last = now();
  let elapsed = 0;
  return () => {
    const current = now();
    if (current > last) elapsed += current - last;
    last = current;
    return elapsed;
  };
}
