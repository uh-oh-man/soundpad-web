import { useEffect, useSyncExternalStore } from 'react';
import { readPref, subscribePrefs, usePref } from './prefs';

/**
 * Motion and contrast (Stage G, 2.43). The page honours the system's Reduce motion and More contrast,
 * and each browser can also ask for them in Settings → Appearance. The choice is put on <html> as
 * `data-motion="reduce"` and `data-contrast="high"`, which tokens.css and studio.css read; with the
 * defaults neither attribute is set, so nothing changes for anyone who has not chosen.
 */
const REDUCE_QUERY = '(prefers-reduced-motion: reduce)';

function systemReduces() {
  try {
    return typeof matchMedia === 'function' && matchMedia(REDUCE_QUERY).matches;
  } catch {
    return false;
  }
}

/** True when motion should be kept to a minimum: the system asks for it, or this browser's setting. */
export function motionReduced() {
  if (typeof document !== 'undefined' && document.documentElement.dataset.motion === 'reduce')
    return true;
  return readPref('motion') === 'reduce' || systemReduces();
}

function subscribeMotion(listener: () => void) {
  const stopPrefs = subscribePrefs(listener);
  let media: MediaQueryList | null = null;
  try {
    media = typeof matchMedia === 'function' ? matchMedia(REDUCE_QUERY) : null;
    media?.addEventListener('change', listener);
  } catch {
    media = null;
  }
  return () => {
    stopPrefs();
    media?.removeEventListener('change', listener);
  };
}

/** `motionReduced()` as React state: it follows the system setting and this browser's choice. */
export function useReducedMotion() {
  return useSyncExternalStore(subscribeMotion, motionReduced, () => false);
}

/** Puts this browser's Motion and Contrast choices on <html> (mounted once, by the app). */
export function useAccessibilityPrefs() {
  const [motion] = usePref('motion');
  const [contrast] = usePref('contrast');
  useEffect(() => {
    const root = document.documentElement;
    if (motion === 'reduce') root.dataset.motion = 'reduce';
    else delete root.dataset.motion;
  }, [motion]);
  useEffect(() => {
    const root = document.documentElement;
    if (contrast === 'high') root.dataset.contrast = 'high';
    else delete root.dataset.contrast;
  }, [contrast]);
}
