/** Stage H (2.44) browser shortcuts. Phase 0 stub: WP4 replaces it with the registry and storage. */
export type ShortcutId = 'search' | 'playPause' | 'panic' | 'stop';
export type Chord = { key: string; ctrl: boolean; alt: boolean; shift: boolean; meta: boolean };
/** Today's keys: / for search, Space for play/pause, Shift+Escape for Panic; Stop has none. */
export const DEFAULT_SHORTCUTS: Record<ShortcutId, Chord | null> = {
  search: { key: '/', ctrl: false, alt: false, shift: false, meta: false },
  playPause: { key: ' ', ctrl: false, alt: false, shift: false, meta: false },
  panic: { key: 'Escape', ctrl: false, alt: false, shift: true, meta: false },
  stop: null,
};
export function useShortcut(id: ShortcutId): Chord | null {
  return DEFAULT_SHORTCUTS[id];
}
