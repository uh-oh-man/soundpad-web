import { useSyncExternalStore } from 'react';

/**
 * Per-browser view preferences (Stage G, 2.43): navigation style, the editor panel's width, motion,
 * contrast, the header ribbon, notice sounds, Spotify's expanded row and the sidebar rail. They live in
 * this browser's localStorage only and change nothing on the host. Every read and write survives
 * storage being unavailable (private windows, blocked site data): the page then uses the defaults, and
 * a change still applies to this tab until it reloads. Other tabs of this browser follow a change
 * through the `storage` event.
 */
export const PREF_KEYS = {
  nav: 'soundpad-web:nav',
  editorWidth: 'soundpad-web:editor-width',
  motion: 'soundpad-web:motion',
  contrast: 'soundpad-web:contrast',
  headerAnimation: 'soundpad-web:header-animation',
  noticeSounds: 'soundpad-web:notice-sounds',
  spotifyExpanded: 'soundpad-web:spotify-expanded',
  sidebarRail: 'soundpad-web:sidebar-rail',
  mimicFull: 'soundpad-web:mimic-full',
} as const;

export type Prefs = {
  /** Sections as a sidebar list or as top tabs. */
  nav: 'sidebar' | 'tabs';
  /** The docked editor panel's width in CSS pixels (clamped to the window by the panel). */
  editorWidth: number;
  /** "reduce" turns motion down on this browser even when the system allows it. */
  motion: 'system' | 'reduce';
  /** "high" strengthens lines, text and focus, as the system's More contrast does. */
  contrast: 'standard' | 'high';
  /** The Soundboard header ribbon: animated, a still picture, or nothing. */
  headerAnimation: 'on' | 'still' | 'off';
  /** A short local blip when a notice appears (never through Soundpad). */
  noticeSounds: 'off' | 'subtle';
  spotifyExpanded: boolean;
  /** The contextual sidebar collapsed to icons (top tabs style). */
  sidebarRail: boolean;
  /** Mimic fills the main area beside the sidebar instead of the right panel (2.46, wide screens). */
  mimicFull: boolean;
};
export type PrefName = keyof Prefs;

export const PREF_DEFAULTS: Prefs = {
  // Top tabs by default (2.47, the owner's choice); the sidebar stays one choice away in Settings.
  nav: 'tabs',
  editorWidth: 480,
  motion: 'system',
  contrast: 'standard',
  headerAnimation: 'on',
  noticeSounds: 'off',
  spotifyExpanded: false,
  sidebarRail: false,
  mimicFull: false,
};

const oneOf =
  <T extends string>(...values: T[]) =>
  (raw: string): T | undefined =>
    (values as string[]).includes(raw) ? (raw as T) : undefined;
const flag = (raw: string) => (raw === 'true' ? true : raw === 'false' ? false : undefined);

/** What a stored string means for each preference; anything else falls back to the default. */
const parsers: { [K in PrefName]: (raw: string) => Prefs[K] | undefined } = {
  nav: oneOf('sidebar', 'tabs'),
  editorWidth: (raw) => {
    const n = Number(raw);
    return Number.isInteger(n) && n >= 200 && n <= 4000 ? n : undefined;
  },
  motion: oneOf('system', 'reduce'),
  contrast: oneOf('standard', 'high'),
  headerAnimation: oneOf('on', 'still', 'off'),
  noticeSounds: oneOf('off', 'subtle'),
  spotifyExpanded: flag,
  sidebarRail: flag,
  mimicFull: flag,
};

/** Values set in this tab while storage was unavailable (so the change still holds until reload). */
const session = new Map<PrefName, unknown>();
const listeners = new Set<() => void>();
const notify = () => {
  for (const l of listeners) l();
};

/** The stored value of `name`, or its default. Never throws. */
export function readPref<K extends PrefName>(name: K): Prefs[K] {
  if (session.has(name)) return session.get(name) as Prefs[K];
  try {
    const raw = localStorage.getItem(PREF_KEYS[name]);
    if (raw !== null) {
      const value = parsers[name](raw);
      if (value !== undefined) return value;
    }
  } catch {
    // Storage unavailable: the default applies.
  }
  return PREF_DEFAULTS[name];
}

/**
 * Stores `value` for `name` and tells this tab's listeners. Returns false when the browser would not
 * store it; the value then applies to this tab only.
 */
export function writePref<K extends PrefName>(name: K, value: Prefs[K]): boolean {
  let stored = true;
  try {
    localStorage.setItem(PREF_KEYS[name], String(value));
    session.delete(name);
  } catch {
    stored = false;
    session.set(name, value);
  }
  notify();
  return stored;
}

/** Calls `listener` when any preference changes, here or in another tab of this browser. */
let storageAttached = false;
export function subscribePrefs(listener: () => void) {
  listeners.add(listener);
  if (!storageAttached && typeof window !== 'undefined') {
    storageAttached = true;
    window.addEventListener('storage', (e) => {
      if (e.key === null || (Object.values(PREF_KEYS) as string[]).includes(e.key)) notify();
    });
  }
  return () => {
    listeners.delete(listener);
  };
}

/** The current value of `name` and a setter, kept in step across this tab and other tabs. */
export function usePref<K extends PrefName>(name: K): [Prefs[K], (value: Prefs[K]) => boolean] {
  const value = useSyncExternalStore(
    subscribePrefs,
    () => readPref(name),
    () => PREF_DEFAULTS[name],
  );
  return [value, (next: Prefs[K]) => writePref(name, next)];
}

/** Test seam: forgets this tab's unstored values. */
export function resetPrefsForTests() {
  session.clear();
  notify();
}
