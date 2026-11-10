/**
 * Buttons on a fresh Mimic clip that open Temp at the right step, as Voice Replay Studio opens a new
 * capture in its editor and library: "edit" opens Edit copy (2.21), "keep" opens Keep permanently
 * (host, 2.26). Mimic and Temp live in different parts of the page, so they meet through one window event.
 */
const EVENT = 'soundpad-web:temp-request';

export type TempRequest = { kind: 'edit' | 'keep'; assetId: string };

export function requestTemp(kind: TempRequest['kind'], assetId: string) {
  window.dispatchEvent(new CustomEvent<TempRequest>(EVENT, { detail: { kind, assetId } }));
}

export function onTempRequest(listener: (request: TempRequest) => void) {
  const handle = (e: Event) => {
    const detail = (e as CustomEvent<unknown>).detail as Partial<TempRequest> | null;
    if (
      detail &&
      (detail.kind === 'edit' || detail.kind === 'keep') &&
      typeof detail.assetId === 'string'
    )
      listener({ kind: detail.kind, assetId: detail.assetId });
  };
  window.addEventListener(EVENT, handle);
  return () => window.removeEventListener(EVENT, handle);
}
