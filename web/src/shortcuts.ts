import { useSyncExternalStore } from 'react';

/**
 * Keyboard shortcuts of this page (2.44, Settings → Keyboard): Search, Play/Pause, Panic and Stop. Each
 * person can change or turn off their own, per browser (the companion window and Chrome keep separate
 * sets, as their storage differs). They live in this browser's localStorage under
 * `soundpad-web:shortcuts` as `{version: 1, bindings: {id: chord | null}}`, read the `prefs.ts` way: every
 * read and write survives storage being unavailable (a change then applies to this tab until it
 * reloads), other tabs follow through the `storage` event, a binding that does not parse falls back to
 * its default, and ids this version does not know are kept when the file is written again.
 *
 * Defaults are today's keys exactly: "/" (by key) for Search, Space (by code) for Play/Pause, and
 * Shift+Escape for Panic (host only); Stop has none. Matching compares the key (case-folded, Space by
 * `code`) and Ctrl, Alt, Shift and Meta. A binding without Shift also takes the key with Shift held, as
 * "/" and Space always did (some layouts need Shift for "/"), unless another binding is that exact chord.
 */
export type ShortcutId = 'search' | 'playPause' | 'panic' | 'stop';
export type Chord = { key: string; ctrl: boolean; alt: boolean; shift: boolean; meta: boolean };
export type ShortcutDef = {
  id: ShortcutId;
  label: string;
  /** What it does, for Settings. */
  description: string;
  default: Chord | null;
  /** Who has it: everyone, or the host (Panic). */
  scope: 'all' | 'host';
  /** When the page leaves the key alone, for Settings. */
  guard: string;
};

const chord = (key: string, mods: Partial<Omit<Chord, 'key'>> = {}): Chord => ({
  key,
  ctrl: false,
  alt: false,
  shift: false,
  meta: false,
  ...mods,
});

export const SHORTCUTS: readonly ShortcutDef[] = [
  {
    id: 'search',
    label: 'Search',
    description: "Jumps to the current view's search.",
    default: chord('/'),
    scope: 'all',
    guard: 'Not while typing or while a dialog is open.',
  },
  {
    id: 'playPause',
    label: 'Play/Pause',
    description: 'Pauses or resumes what plays (or stops it when it cannot pause).',
    default: chord('Space'),
    scope: 'all',
    guard: 'Not while typing, or on a button, link or slider that uses the key itself.',
  },
  {
    id: 'panic',
    label: 'Panic',
    description: 'Stops Soundpad and holds guest playback until you resume it.',
    default: chord('Escape', { shift: true }),
    scope: 'host',
    guard: 'Works everywhere on the page, dialogs included, except while typing.',
  },
  {
    id: 'stop',
    label: 'Stop',
    description: 'Stops what plays.',
    default: null,
    scope: 'all',
    guard: 'Not while typing.',
  },
];
export const DEFAULT_SHORTCUTS: Record<ShortcutId, Chord | null> = Object.fromEntries(
  SHORTCUTS.map((s) => [s.id, s.default]),
) as Record<ShortcutId, Chord | null>;
const IDS = SHORTCUTS.map((s) => s.id);
const label = (id: ShortcutId) => SHORTCUTS.find((s) => s.id === id)!.label;

export const SHORTCUTS_KEY = 'soundpad-web:shortcuts';
const MODIFIERS = new Set(['Shift', 'Control', 'Alt', 'Meta', 'AltGraph', 'OS', 'Hyper', 'Super']);
const NAMED: Record<string, string> = {
  ' ': 'Space',
  space: 'Space',
  spacebar: 'Space',
  esc: 'Escape',
  escape: 'Escape',
  enter: 'Enter',
  tab: 'Tab',
  arrowup: 'ArrowUp',
  arrowdown: 'ArrowDown',
  arrowleft: 'ArrowLeft',
  arrowright: 'ArrowRight',
  backspace: 'Backspace',
  delete: 'Delete',
  insert: 'Insert',
  home: 'Home',
  end: 'End',
  pageup: 'PageUp',
  pagedown: 'PageDown',
  pause: 'Pause',
  scrolllock: 'ScrollLock',
};

/** A key as stored and compared: named keys in their usual case, F-keys as F1, other single keys folded. */
export function foldKey(key: string): string {
  const named = NAMED[key.toLowerCase()];
  if (named) return named;
  if (/^f([1-9]|1\d|2[0-4])$/i.test(key)) return key.toUpperCase();
  return key.length === 1 ? key.toLowerCase() : key;
}

/** The chord a key press makes, or null for a lone modifier (Shift, Ctrl…), which is never a shortcut. */
export function chordOf(
  e: Pick<KeyboardEvent, 'key' | 'code' | 'ctrlKey' | 'altKey' | 'shiftKey' | 'metaKey'>,
) {
  if (MODIFIERS.has(e.key)) return null;
  const key = e.code === 'Space' ? 'Space' : foldKey(e.key);
  return chord(key, { ctrl: e.ctrlKey, alt: e.altKey, shift: e.shiftKey, meta: e.metaKey });
}

const sameKey = (c: Chord, e: Pick<KeyboardEvent, 'key' | 'code'>) =>
  c.key === 'Space' ? e.code === 'Space' : c.key === '/' ? e.key === '/' : foldKey(e.key) === c.key;
const sameChord = (a: Chord, b: Chord) =>
  a.key === b.key &&
  a.ctrl === b.ctrl &&
  a.alt === b.alt &&
  a.shift === b.shift &&
  a.meta === b.meta;

/** "Ctrl+Alt+F9", "Shift+Escape", "/", "Space": how Settings and titles show a chord. */
export function formatChord(c: Chord): string {
  const key = c.key.length === 1 ? c.key.toUpperCase() : c.key;
  return [c.ctrl && 'Ctrl', c.alt && 'Alt', c.shift && 'Shift', c.meta && 'Meta', key]
    .filter(Boolean)
    .join('+');
}
/** The same in ARIA's terms (aria-keyshortcuts): "Control+Alt+F9", "Shift+Escape", "Space". */
export function ariaChord(c: Chord | null | undefined): string | undefined {
  if (!c) return undefined;
  const key = c.key.length === 1 ? c.key.toUpperCase() : c.key;
  return [c.ctrl && 'Control', c.alt && 'Alt', c.shift && 'Shift', c.meta && 'Meta', key]
    .filter(Boolean)
    .join('+');
}

function validChord(raw: unknown): Chord | null | undefined {
  if (raw === null) return null;
  if (!raw || typeof raw !== 'object') return undefined;
  const r = raw as Record<string, unknown>;
  if (typeof r.key !== 'string' || r.key.length < 1 || r.key.length > 20) return undefined;
  if (MODIFIERS.has(r.key)) return undefined;
  for (const m of ['ctrl', 'alt', 'shift', 'meta'] as const)
    if (typeof r[m] !== 'boolean') return undefined;
  return chord(foldKey(r.key), {
    ctrl: r.ctrl as boolean,
    alt: r.alt as boolean,
    shift: r.shift as boolean,
    meta: r.meta as boolean,
  });
}

// ---- Storage ----
type Stored = { bindings: Record<ShortcutId, Chord | null>; extra: Record<string, unknown> };
/** Set in this tab while storage was unavailable (so a change still holds until reload). */
let session: Stored | null = null;
let cache: { raw: string | null; value: Stored } | null = null;
const listeners = new Set<() => void>();
const notify = () => {
  for (const l of listeners) l();
};

function parse(raw: string | null): Stored {
  const bindings = { ...DEFAULT_SHORTCUTS };
  const extra: Record<string, unknown> = {};
  if (raw === null) return { bindings, extra };
  try {
    const data = JSON.parse(raw) as unknown;
    if (!data || typeof data !== 'object' || (data as { version?: unknown }).version !== 1)
      return { bindings, extra };
    const stored = (data as { bindings?: unknown }).bindings;
    if (!stored || typeof stored !== 'object') return { bindings, extra };
    for (const [id, value] of Object.entries(stored as Record<string, unknown>)) {
      if (!IDS.includes(id as ShortcutId)) {
        extra[id] = value; // a newer site's shortcut: kept as it is
        continue;
      }
      const parsed = validChord(value);
      if (parsed !== undefined) bindings[id as ShortcutId] = parsed;
    }
  } catch {
    // Damaged: the defaults apply.
  }
  return { bindings, extra };
}

function read(): Stored {
  if (session) return session;
  let raw: string | null = null;
  try {
    raw = localStorage.getItem(SHORTCUTS_KEY);
  } catch {
    // Storage unavailable: the defaults apply.
  }
  if (cache && cache.raw === raw) return cache.value;
  cache = { raw, value: parse(raw) };
  return cache.value;
}

/** Every shortcut's chord now (null = off). Never throws. */
export function currentShortcuts(): Record<ShortcutId, Chord | null> {
  return read().bindings;
}
export function readShortcut(id: ShortcutId): Chord | null {
  return read().bindings[id];
}

/**
 * Stores `next` for `id` (null turns it off) and tells this tab's listeners. Returns false when the
 * browser would not store it; the change then applies to this tab only.
 */
export function writeShortcut(id: ShortcutId, next: Chord | null): boolean {
  const current = read();
  const value: Stored = { bindings: { ...current.bindings, [id]: next }, extra: current.extra };
  let stored = true;
  try {
    localStorage.setItem(
      SHORTCUTS_KEY,
      JSON.stringify({ version: 1, bindings: { ...value.extra, ...value.bindings } }),
    );
    session = null;
  } catch {
    stored = false;
    session = value;
  }
  cache = null;
  notify();
  return stored;
}
export const resetShortcut = (id: ShortcutId) => writeShortcut(id, DEFAULT_SHORTCUTS[id]);

let storageAttached = false;
export function subscribeShortcuts(listener: () => void) {
  listeners.add(listener);
  if (!storageAttached && typeof window !== 'undefined') {
    storageAttached = true;
    window.addEventListener('storage', (e) => {
      if (e.key === null || e.key === SHORTCUTS_KEY) {
        cache = null;
        notify();
      }
    });
  }
  return () => {
    listeners.delete(listener);
  };
}

/** The current chord of `id`, kept in step across this tab and other tabs. */
export function useShortcut(id: ShortcutId): Chord | null {
  return useSyncExternalStore(
    subscribeShortcuts,
    () => readShortcut(id),
    () => DEFAULT_SHORTCUTS[id],
  );
}
export function useShortcuts(): Record<ShortcutId, Chord | null> {
  return useSyncExternalStore(subscribeShortcuts, currentShortcuts, () => DEFAULT_SHORTCUTS);
}

// ---- Matching ----
/** True while a shortcut recorder listens: no shortcut acts on the key being recorded. */
let recording = 0;
export function setRecording(on: boolean) {
  recording = Math.max(0, recording + (on ? 1 : -1));
}
export const isRecording = () => recording > 0;

/** True when the key press `e` is shortcut `id` now (never while a recorder listens). */
export function matchesShortcut(
  id: ShortcutId,
  e: Pick<KeyboardEvent, 'key' | 'code' | 'ctrlKey' | 'altKey' | 'shiftKey' | 'metaKey'>,
): boolean {
  if (recording) return false;
  const all = currentShortcuts();
  const c = all[id];
  if (!c || !sameKey(c, e) || c.ctrl !== e.ctrlKey || c.alt !== e.altKey || c.meta !== e.metaKey)
    return false;
  if (c.shift === e.shiftKey) return true;
  // Held Shift on a binding without it: still this one, unless another binding is that exact chord.
  if (c.shift || !e.shiftKey) return false;
  return !IDS.some((other) => {
    const o = all[other];
    return (
      other !== id &&
      !!o &&
      o.shift &&
      sameKey(o, e) &&
      o.ctrl === e.ctrlKey &&
      o.alt === e.altKey &&
      o.meta === e.metaKey
    );
  });
}

// ---- Choosing a key ----
const CTRL_RESERVED = new Set(['c', 'v', 'x', 'a', 'z', 'y', 'f', 'l', 'r', 't', 'n', 'w']);
/** Bare keys the page's own areas use (named in the refusal). */
function contextualArea(c: Chord): string | null {
  if (c.ctrl || c.alt || c.meta) return null;
  if (['3', '5', '0'].includes(c.key))
    return 'Mimic uses 3, 5 and 0 to pick how many seconds to keep';
  if (['+', '-', '='].includes(c.key)) return 'the Kick counter uses + and -';
  if (/^[a-z0-9]$/.test(c.key))
    return 'letters and digits type in search and name fields, and Mimic, Review and the timeline use single keys';
  return null;
}

/**
 * Why `next` can't be `id`'s key, in plain words, or null when it can: another shortcut has it, the
 * browser or the page needs it, or a part of the page uses that single key.
 */
export function refusal(
  id: ShortcutId,
  next: Chord,
  bindings: Record<ShortcutId, Chord | null> = currentShortcuts(),
): string | null {
  const text = formatChord(next);
  const command = next.ctrl || next.meta;
  if (next.key === 'Tab') return 'Tab moves between controls; choose another key.';
  if (
    !next.ctrl &&
    !next.alt &&
    !next.meta &&
    (next.key === 'Enter' || (next.key === 'Escape' && !next.shift))
  )
    return `${text} answers buttons and closes dialogs; choose another key.`;
  if (command && !next.alt && CTRL_RESERVED.has(next.key))
    return `${text} is the browser's or this page's own key; choose another.`;
  if (['F5', 'F11', 'F12'].includes(next.key) && !next.ctrl && !next.alt && !next.meta)
    return `${text} is the browser's own key; choose another.`;
  if (next.alt && next.key === 'F4') return 'Alt+F4 closes the window; choose another key.';
  const area = contextualArea(next);
  if (area) return `${text} can't be a shortcut: ${area}. Add Ctrl or Alt.`;
  for (const other of IDS) {
    const o = bindings[other];
    if (other !== id && o && sameChord(o, next)) return `${text} is already ${label(other)}.`;
  }
  return null;
}

/**
 * The warning for a chord the companion takes on this PC (a host-wide hotkey, 2.44): this page never
 * receives that key while the companion runs. `hostKeys` are the companion's chords ("Ctrl+Alt+F9").
 */
export function hostClash(next: Chord, hostKeys: (string | null | undefined)[]): string | null {
  const mine = [
    next.ctrl && 'ctrl',
    next.alt && 'alt',
    next.shift && 'shift',
    next.meta && 'win',
    next.key.toLowerCase(),
  ]
    .filter(Boolean)
    .join('+');
  const taken = hostKeys.some((k) => {
    if (!k) return false;
    const parts = k.toLowerCase().split('+');
    return parts.join('+') === mine;
  });
  return taken
    ? `${formatChord(next)}: the companion takes this key on this PC; this page never receives it.`
    : null;
}

/** Test seam: forgets this tab's unstored values and the cache. */
export function resetShortcutsForTests() {
  session = null;
  cache = null;
  recording = 0;
  notify();
}

/**
 * The host-wide hotkey a key press names, as the companion writes it ("Ctrl+Alt+F9", "Shift+Win+Num5"),
 * from the physical key (`code`), or null for a key the companion never accepts (it checks the rest:
 * modifiers needed, Windows' own combinations). Meta is Windows' Win key.
 */
export function hostChordOf(
  e: Pick<KeyboardEvent, 'key' | 'code' | 'ctrlKey' | 'altKey' | 'shiftKey' | 'metaKey'>,
): string | null {
  const code = e.code;
  let key: string | null;
  if (/^F([1-9]|1\d|2[0-4])$/.test(code)) key = code;
  else if (/^Key[A-Z]$/.test(code)) key = code.slice(3);
  else if (/^Digit\d$/.test(code)) key = code.slice(5);
  else if (/^Numpad\d$/.test(code)) key = 'Num' + code.slice(6);
  else {
    const named: Record<string, string> = {
      NumpadAdd: 'NumAdd',
      NumpadSubtract: 'NumSubtract',
      NumpadMultiply: 'NumMultiply',
      NumpadDivide: 'NumDivide',
      NumpadDecimal: 'NumDecimal',
      Pause: 'Pause',
      ScrollLock: 'ScrollLock',
      Insert: 'Insert',
      Home: 'Home',
      End: 'End',
      PageUp: 'PageUp',
      PageDown: 'PageDown',
    };
    key = named[code] ?? null;
  }
  if (!key) return null;
  return [e.ctrlKey && 'Ctrl', e.altKey && 'Alt', e.shiftKey && 'Shift', e.metaKey && 'Win', key]
    .filter(Boolean)
    .join('+');
}
