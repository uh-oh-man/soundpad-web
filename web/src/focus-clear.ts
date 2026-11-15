/**
 * Keyboard focus is never hidden behind the notices, the player bar or the phone sheet's minimized bar
 * (2.41, notice dock 2.43), nor under the phone's sticky header or a dialog's sticky title row (2.43,
 * their scroll-padding-top: --topbar-bottom, --dialog-title-height).
 *
 * CSS does most of the work. On a narrow screen the notice dock floats above the player and publishes
 * its height as --notice-height (Notifications.tsx); the page's scroll-padding and bottom padding, the
 * board's, the editor sheet's and the player's waveform tools' all count it, and the player's
 * --player-height (shell.css, studio.css, playback.css). On a wide screen the dock is in the page's flow
 * under the board and covers nothing. This is the safety net for what scroll-padding can't reach: a
 * control that takes focus where it already is (no scrolling), a scroll area without that padding, a
 * dialog's own notices at its foot, and a notice that appears over the control that has focus.
 *
 * Organize mode (2.44 review) adds two sticky bars: Review changes' footer (Apply, Discard) at the
 * foot of the editor panel, counted for the steps above it, and the Organize bar, counted at the top
 * of the scroll area it sticks in (the board, or the page below the phone's header) while it is stuck.
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

/**
 * How far to scroll down (px, negative) so a control that `visibleTop` covers comes out from under
 * the bar at the top of its scroll area: the phone's sticky header, or a dialog's sticky title row
 * (2.43). 0 when it is clear. It keeps `margin` below the bar unless that would push the control under
 * a bottom overlay; then it settles for just clear of the top.
 */
export function revealShift(
  target: Box,
  overlays: Box[],
  visibleTop: number,
  margin = FOCUS_MARGIN,
) {
  if (target.top >= visibleTop) return 0;
  const shift = target.top - visibleTop - margin;
  const moved = { ...target, top: target.top - shift, bottom: target.bottom - shift };
  return clearShift(moved, overlays, -Infinity, 0) > 0 ? target.top - visibleTop : shift;
}

const box = (e: Element): Box => {
  const r = e.getBoundingClientRect();
  return { top: r.top, bottom: r.bottom, left: r.left, right: r.right };
};

/** A modal dialog (Modal.tsx, the guest name prompt): it floats over the page and holds its own notices. */
const modalOf = (el: Element) => el.closest('[role="dialog"][aria-modal="true"]');

/**
 * What can cover `el`. In a modal dialog: its own notices at its foot. Elsewhere (the board, a section,
 * the editor panel or sheet, which is a non-modal dialog): the page's notice dock, the player bar and
 * the sheet's minimized bar on a phone. Never the overlay `el` is in.
 */
function overlaysFor(el: HTMLElement) {
  const modal = modalOf(el);
  const candidates = modal
    ? modal.querySelectorAll('.notification-stack')
    : document.querySelectorAll(
        ".notification-stack:not(.in-dialog), .player, .editor-panel[data-panel-mode='minimized']",
      );
  // Review changes' sticky footer covers the steps of its own panel.
  const footers = [...document.querySelectorAll('.review-footer')].filter((f) =>
    f.parentElement?.contains(el),
  );
  return [...candidates, ...footers].filter((o) => !o.contains(el)).map(box);
}

/**
 * Where the visible part starts below the Organize bar, when it is stuck at the top of `scroller` (the
 * scroll area it sticks in) and `el` is not in it; `top` otherwise.
 */
function belowOrganizeBar(
  scroller: HTMLElement,
  isRoot: boolean,
  root: Element,
  el: HTMLElement,
  top: number,
) {
  const bar = document.querySelector<HTMLElement>('.organize-bar');
  if (!bar || bar.contains(el)) return top;
  const style = getComputedStyle(bar);
  if (style.position !== 'sticky') return top;
  let owner = bar.parentElement;
  while (owner && owner !== root && !scrollsVertically(owner)) owner = owner.parentElement;
  if ((owner ?? root) !== scroller) return top;
  // Stuck: at its sticky offset inside the scroll area's padding (the board has some), not further down.
  const edge =
    (isRoot ? 0 : box(scroller).top + scroller.clientTop) +
    (parseFloat(getComputedStyle(scroller).paddingTop) || 0) +
    (parseFloat(style.top) || 0);
  const r = bar.getBoundingClientRect();
  return r.top <= edge + 1 ? Math.max(top, r.bottom) : top;
}

/**
 * Where the visible part of a scroll area starts. The page: below the phone's sticky header
 * (shell.css, 1000 px and below). A dialog: below its sticky title row (studio.css). Not for a control
 * inside that bar itself.
 */
function visibleTopOf(scroller: HTMLElement, isRoot: boolean, el: HTMLElement) {
  if (isRoot) {
    const bar = document.querySelector('.app-shell > .topbar');
    if (!bar || bar.contains(el) || getComputedStyle(bar).position !== 'sticky') return 0;
    return Math.max(0, bar.getBoundingClientRect().bottom);
  }
  const top = box(scroller).top + scroller.clientTop;
  const title = [...scroller.children].find((child) => child.classList.contains('dialog-title'));
  if (!title || title.contains(el) || getComputedStyle(title).position !== 'sticky') return top;
  return Math.max(top, title.getBoundingClientRect().bottom);
}

function scrollsVertically(e: Element) {
  const overflow = getComputedStyle(e).overflowY;
  return (overflow === 'auto' || overflow === 'scroll') && e.scrollHeight > e.clientHeight;
}

/**
 * Scrolls the focused control clear of what covers it, at the bottom (notices, player, minimized bar)
 * or at the top (the phone's header, a dialog's title row): its nearest scroll area first, then outer
 * ones.
 */
export function keepClear(el: Element | null) {
  if (!(el instanceof HTMLElement) || !el.isConnected) return;
  // Keyboard focus only: a click already landed where the pointer is, and scrolling then (for example
  // mid-drag on a waveform under a dialog's notices) would move things under the pointer.
  if (!el.matches(':focus-visible') || getComputedStyle(el).position === 'fixed') return;
  const root = document.scrollingElement ?? document.documentElement;
  const modal = modalOf(el);
  for (let scroller = el.parentElement; scroller; scroller = scroller.parentElement) {
    const isRoot = scroller === root;
    if (isRoot || scrollsVertically(scroller)) {
      const overlays = overlaysFor(el);
      const visibleTop = belowOrganizeBar(
        scroller,
        isRoot,
        root,
        el,
        visibleTopOf(scroller, isRoot, el),
      );
      const target = box(el);
      const shift =
        clearShift(target, overlays, visibleTop) || revealShift(target, overlays, visibleTop);
      if (!shift) return;
      // Instant, like the browser's own focus scrolling, so it adds no motion.
      if (isRoot) window.scrollBy({ top: shift, behavior: 'instant' });
      else scroller.scrollBy({ top: shift, behavior: 'instant' });
    }
    // A modal floats over the page, and so does anything fixed (the player and its waveform tools on a
    // phone, the editor sheet): scrolling what is behind it would not move the control, only jump the page.
    if (isRoot || scroller === modal || getComputedStyle(scroller).position === 'fixed') return;
  }
}

/**
 * Watches focus and the notices. Returns a function that stops. `holders` are the elements the notice
 * cards are in: the page's dock, and while a modal is open the slot at its foot that they move into. A
 * notice that appears over the focused control moves it clear.
 */
export function watchFocusClearance(holders: (HTMLElement | null)[]) {
  // Checked when focus arrives (a control that needs no scrolling) and again a frame later, after the
  // browser's own focus scrolling has run.
  let frame = 0;
  const focused = (event: FocusEvent) => {
    keepClear(event.target as Element);
    cancelAnimationFrame(frame);
    frame = requestAnimationFrame(() => keepClear(document.activeElement));
  };
  document.addEventListener('focusin', focused);
  const heights = new Map<Element, number>();
  const resize = new ResizeObserver((entries) => {
    let grew = false;
    for (const entry of entries) {
      const next = entry.target.getBoundingClientRect().height;
      if (next > (heights.get(entry.target) ?? 0)) grew = true;
      heights.set(entry.target, next);
    }
    if (grew) keepClear(document.activeElement);
  });
  for (const holder of holders) if (holder) resize.observe(holder);
  return () => {
    document.removeEventListener('focusin', focused);
    cancelAnimationFrame(frame);
    resize.disconnect();
  };
}
