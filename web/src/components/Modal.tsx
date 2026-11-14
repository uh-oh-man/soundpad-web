import { useEffect, useRef, useSyncExternalStore, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { X } from 'lucide-react';
import { FOCUSABLE as focusable, currentOpener, returnFocus } from '../focus-return';

/**
 * While any dialog is open (2.43) the page behind it is `inert`: the board, the sidebar and the
 * non-modal editor panel can't be reached by Tab or pointer. Dialogs render into <body>, outside the
 * shell, so they stay usable. Playwright and some assistive technology still list inert content, so the
 * editor panel and the section nav are also hidden from them (aria-hidden): exactly one "Close dialog"
 * and one "Mimic" are exposed at a time.
 */
let openDialogs = 0;
/**
 * The open dialogs, oldest first. Two can be open at once (the companion's Quit prompt over Session &
 * access): only the newest takes Escape, and when an older one closes, the keyboard stays in the newest.
 */
const stack: { current: HTMLElement | null }[] = [];
const HIDDEN_BEHIND = '#editor-panel, .section-nav';
function setShellInert(on: boolean) {
  const shell = document.querySelector('.app-shell');
  if (!shell) return;
  if (on) shell.setAttribute('inert', '');
  else shell.removeAttribute('inert');
  for (const element of shell.querySelectorAll(HIDDEN_BEHIND))
    if (on) element.setAttribute('aria-hidden', 'true');
    else element.removeAttribute('aria-hidden');
}

/**
 * Where notices go while a dialog is open (2.41): inside the newest dialog, at its foot, instead of
 * floating over the page. Floating notices covered the Mimic dialog's close button and Leave example,
 * and Tab could not reach their Undo through the dialog's focus trap.
 */
const slots: HTMLElement[] = [];
const slotListeners = new Set<() => void>();
let topSlot: HTMLElement | null = null;
function slotsChanged() {
  topSlot = slots[slots.length - 1] ?? null;
  for (const listener of slotListeners) listener();
}
export function useNoticeSlot() {
  return useSyncExternalStore(
    (listener) => {
      slotListeners.add(listener);
      return () => {
        slotListeners.delete(listener);
      };
    },
    () => topSlot,
    () => null,
  );
}

/** True while any app dialog is open; used by global shortcuts that must not fire behind one. */
export const dialogOpen = () => !!document.querySelector('[role="dialog"][aria-modal="true"]');

export function Modal({
  label,
  onClose,
  children,
}: {
  label: string;
  onClose: () => void;
  children: ReactNode;
}) {
  const dialog = useRef<HTMLElement>(null);
  const noticeSlot = useRef<HTMLDivElement>(null);
  const close = useRef(onClose);
  const pressedScrim = useRef(false);
  useEffect(() => {
    close.current = onClose;
  });
  useEffect(() => {
    const slot = noticeSlot.current;
    if (!slot) return;
    slots.push(slot);
    slotsChanged();
    return () => {
      slots.splice(slots.indexOf(slot), 1);
      slotsChanged();
    };
  }, []);
  useEffect(() => {
    // The title row sticks with its top padding above the dialog's edge (studio.css). What it covers
    // then is its height less that padding: the dialog keeps that much room at the top for keyboard focus.
    const element = dialog.current;
    const title = element?.querySelector<HTMLElement>(':scope > .dialog-title');
    if (!element || !title) return;
    const resize = new ResizeObserver(() => {
      const covers = title.offsetHeight - parseFloat(getComputedStyle(title).paddingTop || '0');
      element.style.setProperty('--dialog-title-height', `${Math.max(0, covers)}px`);
    });
    resize.observe(title, { box: 'border-box' });
    return () => resize.disconnect();
  }, []);
  useEffect(() => {
    // Return focus to whatever opened the dialog once it closes.
    const opener = currentOpener();
    const me = dialog;
    stack.push(me);
    openDialogs++;
    setShellInert(true);
    dialog.current?.querySelector<HTMLElement>('button')?.focus();
    function escape(event: KeyboardEvent) {
      if (event.key !== 'Escape' || event.defaultPrevented) return;
      // Only the newest dialog closes; an older one's listener (registered first) leaves the key to it.
      if (stack[stack.length - 1] !== me) return;
      // Taken: the editor panel behind must not close on the same press.
      event.preventDefault();
      close.current();
    }
    document.addEventListener('keydown', escape);
    return () => {
      document.removeEventListener('keydown', escape);
      const at = stack.indexOf(me);
      if (at >= 0) stack.splice(at, 1);
      openDialogs = Math.max(0, openDialogs - 1);
      // The page is usable again before focus goes back into it.
      if (!openDialogs) setShellInert(false);
      const top = stack[stack.length - 1]?.current;
      const active = document.activeElement;
      // Another dialog is still open and has the keyboard: leave it there.
      if (top && active && active !== document.body && top.contains(active)) return;
      returnFocus(opener, () => top?.querySelector<HTMLElement>(focusable));
    };
  }, []);
  return createPortal(
    <div
      className="scrim"
      // Only a press that starts and ends on the backdrop closes, so drags out of the dialog don't.
      onPointerDown={(e) => (pressedScrim.current = e.target === e.currentTarget)}
      onClick={(e) => {
        if (pressedScrim.current && e.target === e.currentTarget) onClose();
        pressedScrim.current = false;
      }}
    >
      <section
        ref={dialog}
        role="dialog"
        aria-modal="true"
        aria-label={label}
        className="dialog"
        onKeyDown={(e) => {
          if (e.key !== 'Tab') return;
          const nodes = [...e.currentTarget.querySelectorAll<HTMLElement>(focusable)];
          const first = nodes[0],
            last = nodes[nodes.length - 1];
          if (e.shiftKey && document.activeElement === first) {
            e.preventDefault();
            last?.focus();
          } else if (!e.shiftKey && document.activeElement === last) {
            e.preventDefault();
            first?.focus();
          }
        }}
      >
        <div className="dialog-title">
          <h2>{label}</h2>
          <button className="icon-button" aria-label="Close dialog" onClick={onClose}>
            <X size={19} />
          </button>
        </div>
        {children}
        <div className="dialog-notices" ref={noticeSlot} />
      </section>
    </div>,
    document.body,
  );
}
