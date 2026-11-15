/**
 * When a control takes itself away (Remove, Discard, Later, Done organizing), keyboard focus would fall to
 * the page, where Space pauses what plays and Escape closes the panel (2.44 review). `followFocus` moves it
 * to `next()` once `from` has gone, waiting while the change still waits on the companion (up to about
 * `ms`). Nothing happens when focus went somewhere else on its own meanwhile.
 */
export function followFocus(
  from: Element | null,
  next: () => HTMLElement | null | undefined,
  ms = 4000,
) {
  if (typeof document === 'undefined') return;
  const until = performance.now() + ms;
  const tick = () => {
    const active = document.activeElement;
    const dropped = !active || active === document.body || !active.isConnected;
    if (!dropped) {
      // Still on the control that will go: wait for it. Anywhere else: the person moved on.
      if (active === from && performance.now() < until) requestAnimationFrame(tick);
      return;
    }
    const target = next();
    if (target?.isConnected) target.focus();
    else if (performance.now() < until) requestAnimationFrame(tick);
  };
  requestAnimationFrame(tick);
}

/** The element focus was on, for `followFocus` (null when it is the page itself). */
export const focusedControl = () =>
  typeof document !== 'undefined' && document.activeElement !== document.body
    ? document.activeElement
    : null;
