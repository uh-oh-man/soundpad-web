/**
 * The start page's one signal: a pad was pressed (homepage). The pretend soundboard sends it and the
 * ribbon behind the hero listens, so a press ripples through the page without shared React state.
 */
type Listener = (strength: number) => void;
const listeners = new Set<Listener>();

export function onPulse(listener: Listener) {
  listeners.add(listener);
  return () => void listeners.delete(listener);
}

export function pulse(strength = 1) {
  for (const listener of listeners) listener(strength);
}
