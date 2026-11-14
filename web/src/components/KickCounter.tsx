import { Minus, Plus } from 'lucide-react';
import type { Snapshot } from '../protocol';

/** Runs a companion command; the caller tracks `pending` and shows failures (main.tsx `run`). */
export type KickSend = (op: 'counter', fields: { delta: -1 | 1 }) => Promise<void>;

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
 * The Kick counter in the player (Stage G P0.6: moved out of main.tsx unchanged). The props are the
 * contract between the player and this component; WP-B owns the inside.
 */
export function KickCounter({ state, send, pending, canEdit }: KickCounterProps) {
  return (
    <div className="counter">
      <div className="counter-label">
        KICK COUNTER<small>It happens.</small>
      </div>
      <button
        aria-label="Decrease Kick counter"
        // Busy is aria-disabled so keyboard focus stays on the button while the count updates.
        disabled={!canEdit || state.counter === 0}
        aria-disabled={pending > 0 || undefined}
        onClick={() => pending === 0 && void send('counter', { delta: -1 })}
      >
        <Minus size={16} />
      </button>
      <output aria-label="Kick count">{state.counter}</output>
      <button
        aria-label="Increase Kick counter"
        disabled={!canEdit}
        aria-disabled={pending > 0 || undefined}
        onClick={() => pending === 0 && void send('counter', { delta: 1 })}
      >
        <Plus size={16} />
      </button>
    </div>
  );
}
