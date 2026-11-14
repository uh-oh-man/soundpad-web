import { useEffect, useRef, useSyncExternalStore, type ReactNode } from 'react';
import { X } from 'lucide-react';

const focusable =
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
      lastPressed = e.target instanceof Element ? e.target.closest<HTMLElement>(focusable) : null;
    },
    true,
  );

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
    // Return focus to whatever opened the dialog once it closes.
    const active = document.activeElement;
    const opener = active instanceof HTMLElement && active !== document.body ? active : lastPressed;
    dialog.current?.querySelector<HTMLElement>('button')?.focus();
    function escape(event: KeyboardEvent) {
      if (event.key === 'Escape' && !event.defaultPrevented) close.current();
    }
    document.addEventListener('keydown', escape);
    return () => {
      document.removeEventListener('keydown', escape);
      if (opener?.isConnected) opener.focus();
    };
  }, []);
  return (
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
    </div>
  );
}
