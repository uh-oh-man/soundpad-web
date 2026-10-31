import { useEffect, useState } from 'react';
export const themes = [
  { id: 'red-blue', name: 'Red & Blue' },
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
export function useAppearance() {
  const [theme, change] = useState<Theme>(readTheme),
    [error, setError] = useState('');
  useEffect(() => {
    document.documentElement.dataset.theme = theme;
  }, [theme]);
  useEffect(() => {
    const sync = (e: StorageEvent) => {
      if (e.key === 'soundpad-appearance') change(readTheme());
    };
    window.addEventListener('storage', sync);
    return () => window.removeEventListener('storage', sync);
  }, []);
  function setTheme(value: Theme) {
    change(value);
    setError('');
    try {
      localStorage.setItem('soundpad-appearance', value);
    } catch {
      setError('Theme applied for this tab. Browser storage is unavailable.');
    }
  }
  return { theme, setTheme, error };
}
