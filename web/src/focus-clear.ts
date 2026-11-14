/**
 * Keyboard focus is never hidden behind the notices or the player bar (2.41).
 *
 * Notices sit at the bottom, above the player (2.41), so a control that Tab scrolls to the bottom
 * edge of the screen can land under them. CSS does most of the work: the notices publish their height
 * as --notices-clearance, which the page's scroll-padding and the board's end spacer include. This is
 * the safety net for what scroll-padding can't reach: a control that takes focus where it already is
 * (no scrolling), a scroll area without that padding, a dialog's own notices at its foot, and a notice
 * that appears over the control that has focus.
 */
export type Box = { top: number; bottom: number; left: number; right: number };

/** Room kept between a focused control and the notice or bar it was moved clear of. */
export const FOCUS_MARGIN = 12;

/**
 * How far to scroll up (px) so `target` clears every overlay that covers it. Overlays are anchored at
 * the bottom, so clearing one can put the target under the next one up; this keeps going until it is
 * clear. It never scrolls the target's top above `visibleTop`, the top of the area it scrolls in.
 */
export function clearShift(
  target: Box,
  overlays: Box[],
  visibleTop: number,
  margin = FOCUS_MARGIN,
) {
  const shown = overlays.filter((o) => o.bottom > o.top && o.right > o.left);
  let shift = 0;
  for (let pass = 0; pass <= shown.length; pass++) {
    const top = target.top - shift,
      bottom = target.bottom - shift;
    let more = 0;
    for (const o of shown) {
      const covers =
        target.left < o.right && target.right > o.left && bottom > o.top && top < o.bottom;
      if (covers) more = Math.max(more, bottom - o.top + margin);
    }
    if (!more) break;
    shift += more;
  }
  return Math.max(0, Math.min(shift, target.top - visibleTop));
}

const box = (e: Element): Box => {
  const r = e.getBoundingClientRect();
  return { top: r.top, bottom: r.bottom, left: r.left, right: r.right };
};

/** What can cover `el`: in a dialog, its own notices; on the page, the notices and the player bar. */
function overlaysFor(el: HTMLElement) {
  const dialog = el.closest('[role="dialog"]');
  const candidates = dialog
    ? dialog.querySelectorAll('.notification-stack')
    : document.querySelectorAll('.notification-stack:not(.in-dialog), .player');
  return [...candidates].filter((o) => !o.contains(el)).map(box);
}

function scrollsVertically(e: Element) {
  const overflow = getComputedStyle(e).overflowY;
  return (overflow === 'auto' || overflow === 'scroll') && e.scrollHeight > e.clientHeight;
}

/** Scrolls the focused control clear of what covers it: its nearest scroll area first, then outer ones. */
export function keepClear(el: Element | null) {
  if (!(el instanceof HTMLElement) || !el.isConnected) return;
  // Keyboard focus only: a click already landed where the pointer is, and scrolling then (for example
  // mid-drag on a waveform under a dialog's notices) would move things under the pointer.
  if (!el.matches(':focus-visible') || getComputedStyle(el).position === 'fixed') return;
  const root = document.scrollingElement ?? document.documentElement;
  const dialog = el.closest('[role="dialog"]');
  for (let scroller = el.parentElement; scroller; scroller = scroller.parentElement) {
    const isRoot = scroller === root;
    if (isRoot || scrollsVertically(scroller)) {
      const overlays = overlaysFor(el);
      if (!overlays.length) return;
      const shift = clearShift(box(el), overlays, isRoot ? 0 : box(scroller).top);
      if (!shift) return;
      // Instant, like the browser's own focus scrolling, so it adds no motion.
      if (isRoot) window.scrollBy({ top: shift, behavior: 'instant' });
      else scroller.scrollBy({ top: shift, behavior: 'instant' });
    }
    // A dialog floats over the page, and so does anything fixed (the player and its waveform tools on a
    // phone): scrolling what is behind it would not move the control, only jump the page.
    if (isRoot || scroller === dialog || getComputedStyle(scroller).position === 'fixed') return;
  }
}

/**
 * Watches focus and the notices. Returns a function that stops. `stack` is the notices' element, or
 * null; while it is the page's own (not inside a dialog) its height is published for the CSS.
 */
export function watchFocusClearance(stack: HTMLElement | null, inDialog: boolean) {
  const style = document.documentElement.style;
  // Checked when focus arrives (a control that needs no scrolling) and again a frame later, after the
  // browser's own focus scrolling has run.
  let frame = 0;
  const focused = (event: FocusEvent) => {
    keepClear(event.target as Element);
    cancelAnimationFrame(frame);
    frame = requestAnimationFrame(() => keepClear(document.activeElement));
  };
  document.addEventListener('focusin', focused);
  let height = 0;
  let resize: ResizeObserver | null = null;
  if (stack) {
    resize = new ResizeObserver(() => {
      const next = stack.getBoundingClientRect().height;
      if (!inDialog) {
        // The stack sits 16 px above the player; the rest is room to spare above it.
        if (next > 0) style.setProperty('--notices-clearance', `${next + 16 + FOCUS_MARGIN}px`);
        else style.removeProperty('--notices-clearance');
      }
      // A notice that appears over the focused control moves it clear.
      if (next > height) keepClear(document.activeElement);
      height = next;
    });
    resize.observe(stack);
  }
  return () => {
    document.removeEventListener('focusin', focused);
    cancelAnimationFrame(frame);
    resize?.disconnect();
    if (!inDialog) style.removeProperty('--notices-clearance');
  };
}
