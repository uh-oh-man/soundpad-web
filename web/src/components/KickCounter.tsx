import { useEffect, useRef, useState, useSyncExternalStore } from 'react';
import { ChevronUp, Minus, Plus, RotateCcw } from 'lucide-react';
import type { Snapshot } from '../protocol';
import { controller } from '../controller';
import { supports } from '../version';

/**
 * Runs a companion command; the caller tracks `pending` and shows failures (main.tsx `run`). Since
 * WP-B (2.43) Kick also sends Reset from its details, so the operation may be `reset` (no fields);
 * main.tsx's `run` takes any operation, so the call site is unchanged.
 */
export type KickSend = (op: 'counter' | 'reset', fields?: { delta: -1 | 1 }) => Promise<void>;

export type KickCounterProps = {
  state: Snapshot;
  /** main.tsx's `run`: sends the command and counts it as pending while it runs. */
  send: KickSend;
  /** Commands still running; presses are ignored meanwhile (aria-disabled keeps focus). */
  pending: number;
  /** The host, or a guest allowed to change the counter, while connected. */
  canEdit: boolean;
};

/**
 * The Kick counter in the player (Stage G spec 5.6). Collapsed it shows the count, a visible "+" (one
 * click for the commonest live action), and with companion 2.43 the presses of this session. Decrease
 * and Reset are behind "Kick counter details", a popover above the player (never a dialog, and never
 * over Stop or Panic). With a Kick control focused, "+" (or "=") and "-" change the count.
 */
export function KickCounter({ state, send, pending, canEdit }: KickCounterProps) {
  const [open, setOpen] = useState(false);
  const toggle = useRef<HTMLButtonElement>(null);
  const details = useRef<HTMLDivElement>(null);
  const host =
    useSyncExternalStore(controller.subscribe, () => controller.getSnapshot().mode) === 'host';
  const session =
    supports(state.companionVersion, 'kickSession') && state.counterSession !== undefined
      ? state.counterSession
      : null;
  const busy = pending > 0;
  const canDecrease = canEdit && state.counter > 0;

  function change(delta: -1 | 1) {
    if (busy || !canEdit || (delta < 0 && state.counter === 0)) return;
    void send('counter', { delta });
  }
  function close(refocus: boolean) {
    setOpen(false);
    if (refocus) toggle.current?.focus();
  }
  // An outside press closes the details; focus inside them goes back to the disclosure button.
  useEffect(() => {
    if (!open) return;
    const outside = (e: PointerEvent) => {
      const target = e.target as Node | null;
      if (target && (details.current?.contains(target) || toggle.current?.contains(target))) return;
      close(!!details.current?.contains(document.activeElement));
    };
    document.addEventListener('pointerdown', outside, true);
    return () => document.removeEventListener('pointerdown', outside, true);
  }, [open]);

  return (
    <div
      className={`counter kick ${open ? 'is-open' : ''}`}
      onKeyDown={(e) => {
        if (e.ctrlKey || e.altKey || e.metaKey) return;
        if (e.key === 'Escape' && open) {
          e.preventDefault();
          e.stopPropagation();
          close(true);
        } else if (e.key === '+' || e.key === '=') {
          e.preventDefault();
          change(1);
        } else if (e.key === '-') {
          e.preventDefault();
          change(-1);
        }
      }}
    >
      <div className="counter-label">KICK</div>
      <div className="kick-count">
        <output aria-label="Kick count">{state.counter}</output>
        {session !== null && (
          <small className="kick-session tabular">+{session} this session</small>
        )}
      </div>
      <button
        className="kick-increase"
        aria-label="Increase Kick counter"
        disabled={!canEdit}
        // Busy is aria-disabled so keyboard focus stays on the button while the count updates.
        aria-disabled={busy || undefined}
        onClick={() => change(1)}
      >
        <Plus size={16} aria-hidden="true" />
      </button>
      <button
        ref={toggle}
        className="kick-more"
        aria-label="Kick counter details"
        aria-expanded={open}
        aria-controls="kick-details"
        aria-keyshortcuts="+ -"
        title="Decrease, reset and totals · + and - change the count"
        onClick={() => (open ? close(true) : setOpen(true))}
      >
        <ChevronUp size={16} aria-hidden="true" />
      </button>
      {open && (
        <div
          ref={details}
          id="kick-details"
          className="popover kick-details"
          role="group"
          aria-label="Kick counter"
        >
          <dl>
            <div>
              <dt>Lifetime</dt>
              <dd className="tabular">{state.counter}</dd>
            </div>
            {session !== null && (
              <div>
                <dt>This session</dt>
                <dd className="tabular">{session}</dd>
              </div>
            )}
          </dl>
          <div className="kick-detail-actions">
            <button
              aria-label="Decrease Kick counter"
              disabled={!canDecrease}
              aria-disabled={busy || undefined}
              onClick={() => change(-1)}
            >
              <Minus size={15} aria-hidden="true" /> <span aria-hidden="true">1</span>
            </button>
            {host && (
              <button
                className="danger-link"
                aria-label="Reset Kick counter"
                disabled={!canEdit}
                aria-disabled={busy || undefined}
                onClick={() => {
                  if (busy) return;
                  if (confirm('Reset the Kick counter to zero?')) void send('reset');
                }}
              >
                <RotateCcw size={14} aria-hidden="true" /> <span aria-hidden="true">Reset</span>
              </button>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
