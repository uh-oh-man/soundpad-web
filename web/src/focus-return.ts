/**
 * Where focus goes back to when a dialog or the editor panel closes (2.43; moved out of Modal.tsx so
 * both share it).
 */
export const FOCUSABLE =
  'button:not(:disabled),input:not(:disabled),select:not(:disabled),textarea:not(:disabled),a[href],[tabindex]:not([tabindex="-1"])';

/**
 * The control last pressed with a pointer. Safari doesn't focus buttons on click, so when a dialog
 * opens from a click, focus is still on the page body; this lets the dialog return focus to the
 * button that opened it anyway.
 */
let lastPressed: HTMLElement | null = null;
if (typeof document !== 'undefined')
  document.addEventListener(
    'pointerdown',
    (e) => {
      lastPressed = e.target instanceof Element ? e.target.closest<HTMLElement>(FOCUSABLE) : null;
    },
    true,
  );

/** What opened something right now: the focused control, or the one last pressed. */
export function currentOpener(): HTMLElement | null {
  const active = document.activeElement;
  return active instanceof HTMLElement && active !== document.body ? active : lastPressed;
}

/**
 * Focuses `opener` if it is still on the page and usable, otherwise `fallback()`. Returns the
 * element that got focus, if any.
 */
export function returnFocus(
  opener: HTMLElement | null | undefined,
  fallback?: () => HTMLElement | null | undefined,
) {
  const usable = (el: HTMLElement | null | undefined): el is HTMLElement =>
    !!el && el.isConnected && !el.closest('[inert]');
  const target = usable(opener) ? opener : fallback?.();
  if (usable(target)) {
    target.focus();
    return target;
  }
  return null;
}
