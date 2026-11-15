import { useState } from 'react';
import { RotateCcw } from 'lucide-react';
import {
  SHORTCUTS,
  chordOf,
  formatChord,
  hostClash,
  refusal,
  resetShortcut,
  useShortcuts,
  writeShortcut,
  type ShortcutId,
} from '../../shortcuts';
import { ShortcutRecorder } from './ShortcutRecorder';

/** Keys that work only in one place, listed so nobody rebinds over them by surprise (read-only). */
export const CONTEXTUAL_KEYS: [string, string][] = [
  ['3 · 5 · 0', 'Mimic: keep the last 3, 5 or 10 seconds'],
  [
    'Enter · Shift+Enter',
    'Mimic: keep the selection as a clip or draft (Shift+Enter also plays it)',
  ],
  ['Escape', 'Mimic, dialogs and the editor panel: cancel or close'],
  ['Review keys', 'Voices → Review: the keys shown next to its buttons'],
  ['+ · -', 'Kick counter: add or take away a kick'],
  ['Arrows · Home · End', 'Waveforms and the timeline: move the playhead or the selection'],
];

/**
 * This browser's shortcuts (2.44), each with Record, Turn off and Reset. A refused key (another
 * shortcut has it, the browser needs it, a part of the page uses it) is explained and nothing changes.
 * `hostKeys` are the companion's host-wide hotkeys, which this page never receives while it runs.
 */
export function ShortcutList({
  host,
  panic,
  hostKeys = [],
}: {
  host: boolean;
  /** Whether Panic exists here (the host, on a companion with intents). */
  panic: boolean;
  hostKeys?: (string | null)[];
}) {
  const bindings = useShortcuts();
  const [notes, setNotes] = useState<Partial<Record<ShortcutId, { text: string; error: boolean }>>>(
    {},
  );
  const note = (id: ShortcutId, text: string, error: boolean) =>
    setNotes((n) => ({ ...n, [id]: { text, error } }));
  const shown = SHORTCUTS.filter((s) => s.scope === 'all' || (host && panic && s.id === 'panic'));
  return (
    <>
      <ul className="shortcut-list" aria-label="Keyboard shortcuts">
        {shown.map((s) => {
          const bound = bindings[s.id];
          const changed = JSON.stringify(bound) !== JSON.stringify(s.default);
          const message = notes[s.id];
          return (
            <li key={s.id} className="shortcut-row" data-shortcut={s.id}>
              <div className="shortcut-name">
                <strong id={`shortcut-${s.id}`}>{s.label}</strong>
                <small>{s.description}</small>
              </div>
              <span className="shortcut-key" aria-label={`${s.label} key`}>
                {bound ? <kbd className="kbd">{formatChord(bound)}</kbd> : <em>Off</em>}
              </span>
              <span className="shortcut-actions">
                <ShortcutRecorder
                  label={`Record a key for ${s.label}`}
                  onKey={(e) => {
                    const next = chordOf(e);
                    if (!next) return;
                    const refused = refusal(s.id, next, bindings);
                    if (refused) return note(s.id, refused, true);
                    const stored = writeShortcut(s.id, next);
                    const clash = hostClash(next, hostKeys);
                    note(
                      s.id,
                      clash ??
                        (stored
                          ? `${s.label} is now ${formatChord(next)}.`
                          : `${s.label} is ${formatChord(next)} in this tab only: browser storage is unavailable.`),
                      !!clash,
                    );
                  }}
                />
                <button
                  type="button"
                  aria-label={`Turn off ${s.label}`}
                  aria-disabled={!bound || undefined}
                  onClick={() => {
                    // aria-disabled, not disabled: a pressed button that disables itself drops focus.
                    if (!bound) return;
                    writeShortcut(s.id, null);
                    note(s.id, `${s.label} is off.`, false);
                  }}
                >
                  Turn off
                </button>
                <button
                  type="button"
                  aria-label={`Reset ${s.label}`}
                  aria-disabled={!changed || undefined}
                  onClick={() => {
                    if (!changed) return;
                    resetShortcut(s.id);
                    note(
                      s.id,
                      s.default
                        ? `${s.label} is ${formatChord(s.default)} again.`
                        : `${s.label} is off again.`,
                      false,
                    );
                  }}
                >
                  <RotateCcw size={14} aria-hidden="true" /> Reset
                </button>
              </span>
              {message && (
                <p
                  className={message.error ? 'inline-error shortcut-note' : 'shortcut-note'}
                  role={message.error ? 'alert' : 'status'}
                >
                  {message.text}
                </p>
              )}
            </li>
          );
        })}
      </ul>
      <details className="shortcut-contextual">
        <summary>Keys that work in one place</summary>
        <dl>
          {CONTEXTUAL_KEYS.map(([keys, where]) => (
            <div key={keys}>
              <dt>
                <kbd className="kbd">{keys}</kbd>
              </dt>
              <dd>{where}</dd>
            </div>
          ))}
        </dl>
      </details>
    </>
  );
}
