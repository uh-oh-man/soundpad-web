import { useEffect, useRef, useState } from 'react';
import { Keyboard } from 'lucide-react';
import { setRecording } from '../../shortcuts';

const MODIFIER_KEYS = new Set(['Shift', 'Control', 'Alt', 'Meta', 'AltGraph', 'OS']);

/**
 * "Record" for a key (2.44): the next key press (with its modifiers) is taken for `onKey` instead of
 * doing anything on the page; no shortcut acts on it meanwhile. Plain Escape cancels, and Tab is never
 * taken: it moves on and ends the recording. Listens on the window before every other handler.
 */
export function ShortcutRecorder({
  label,
  onKey,
  busy = false,
  disabled = false,
}: {
  /** The button's accessible name, e.g. "Record a key for Panic". */
  label: string;
  onKey: (e: KeyboardEvent) => void;
  busy?: boolean;
  disabled?: boolean;
}) {
  const [listening, setListening] = useState(false);
  const callback = useRef(onKey);
  useEffect(() => {
    callback.current = onKey;
  });
  useEffect(() => {
    if (!listening) return;
    setRecording(true);
    function key(e: KeyboardEvent) {
      if (e.key === 'Tab') {
        setListening(false);
        return;
      }
      e.preventDefault();
      e.stopPropagation();
      if (MODIFIER_KEYS.has(e.key)) return; // wait for the key itself
      setListening(false);
      if (e.key === 'Escape' && !e.shiftKey && !e.ctrlKey && !e.altKey && !e.metaKey) return;
      callback.current(e);
    }
    window.addEventListener('keydown', key, true);
    return () => {
      window.removeEventListener('keydown', key, true);
      setRecording(false);
    };
  }, [listening]);
  return (
    <button
      type="button"
      className="shortcut-record"
      aria-label={listening ? `${label}: press the new key now, or Escape to cancel` : label}
      aria-pressed={listening}
      disabled={disabled}
      aria-disabled={busy || undefined}
      onClick={() => !busy && setListening((on) => !on)}
      onBlur={() => setListening(false)}
    >
      <Keyboard size={15} aria-hidden="true" /> {listening ? 'Press a key…' : 'Record'}
    </button>
  );
}
