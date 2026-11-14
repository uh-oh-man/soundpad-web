import { useEffect, useState } from 'react';
export const themes = [
  // The id is what browsers have stored since 2.23; the look is Stage G's Blue studio (2.43).
  { id: 'red-blue', name: 'Blue studio' },
  { id: 'purple', name: 'Original Purple' },
  { id: 'ocean', name: 'Ocean' },
] as const;
export type Theme = (typeof themes)[number]['id'];
export function readTheme(): Theme {
  try {
    const value = localStorage.getItem('soundpad-appearance');
    if (themes.some((t) => t.id === value)) return value as Theme;
  } catch {
    /* Appearance still works without storage. */
  }
  return 'red-blue';
}
/** A theme set in this tab while storage was unavailable (it holds until reload). */
let unstored: Theme | null = null;
const themeListeners = new Set<() => void>();
/** Calls `listener` whenever this tab's theme changes (Settings, another tab, or host style sync). */
export function subscribeTheme(listener: () => void) {
  themeListeners.add(listener);
  return () => {
    themeListeners.delete(listener);
  };
}
/** Stores and shows `value`; false when the browser would not store it (it still applies to this tab). */
export function applyTheme(value: Theme): boolean {
  let stored = true;
  try {
    localStorage.setItem('soundpad-appearance', value);
    unstored = null;
  } catch {
    unstored = value;
    stored = false;
  }
  for (const l of themeListeners) l();
  return stored;
}
/** This tab's theme: the stored one, or one set while storage was unavailable. */
export const currentTheme = () => unstored ?? readTheme();
export function useAppearance() {
  const [theme, change] = useState<Theme>(currentTheme),
    [error, setError] = useState('');
  useEffect(() => {
    document.documentElement.dataset.theme = theme;
  }, [theme]);
  useEffect(() => {
    const sync = (e: StorageEvent) => {
      if (e.key === 'soundpad-appearance') {
        change(readTheme());
        for (const l of themeListeners) l();
      }
    };
    window.addEventListener('storage', sync);
    const follow = () => change(currentTheme());
    themeListeners.add(follow);
    return () => {
      window.removeEventListener('storage', sync);
      themeListeners.delete(follow);
    };
  }, []);
  function setTheme(value: Theme) {
    setError('');
    if (!applyTheme(value)) setError('Theme applied for this tab. Browser storage is unavailable.');
  }
  return { theme, setTheme, error };
}
