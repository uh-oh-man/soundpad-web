/**
 * "Edit" on a fresh Mimic clip (2.21): opens Temp's Edit copy for that clip, as Voice Replay Studio opens a
 * new capture in its editor. Mimic and Temp live in different parts of the page, so they meet through
 * this one window event.
 */
const EVENT = 'soundpad-web:edit-copy';

export function requestEditCopy(assetId: string) {
  window.dispatchEvent(new CustomEvent<string>(EVENT, { detail: assetId }));
}

export function onEditCopyRequest(listener: (assetId: string) => void) {
  const handle = (e: Event) => {
    const id = (e as CustomEvent<unknown>).detail;
    if (typeof id === 'string') listener(id);
  };
  window.addEventListener(EVENT, handle);
  return () => window.removeEventListener(EVENT, handle);
}
