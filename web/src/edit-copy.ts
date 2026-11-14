import { requestEditor, showView } from './shell';

/**
 * Buttons on a fresh Mimic clip that open Temp at the right step, as Voice Replay Studio opens a new
 * capture in its editor and library: "edit" opens the copy editor (2.21; in the editor panel since
 * 2.43), "keep" opens Keep permanently in the Temp list (host, 2.26). Mimic and Temp live in different
 * parts of the page, so they meet through one window event.
 */
const EVENT = 'soundpad-web:temp-request';

export type TempRequest = { kind: 'edit' | 'keep'; assetId: string };

/** The Keep request still waiting for the Temp list to show (it is on the Soundboard view). */
let waitingKeep: TempRequest | null = null;
/** The clip whose copy editor should put the keyboard on Start once it shows (the Mimic flow). */
let focusStart: string | null = null;

export function requestTemp(kind: TempRequest['kind'], assetId: string) {
  if (kind === 'edit') {
    // Like Voice Replay Studio opening a new capture in its editor: Start gets the keyboard.
    focusStart = assetId;
    requestEditor({ kind: 'temp-edit', sourceId: assetId });
    return;
  }
  waitingKeep = { kind, assetId };
  showView('board');
  window.dispatchEvent(new CustomEvent<TempRequest>(EVENT, { detail: { kind, assetId } }));
}

/** Takes the Keep request made before the Temp list was on the page (once). */
export function takeTempRequest() {
  const request = waitingKeep;
  waitingKeep = null;
  return request;
}

/** True once for the clip the Mimic flow opened: its editor then focuses Start. */
export function takeFocusStart(assetId: string) {
  if (focusStart !== assetId) return false;
  focusStart = null;
  return true;
}

export function onTempRequest(listener: (request: TempRequest) => void) {
  const handle = (e: Event) => {
    const detail = (e as CustomEvent<unknown>).detail as Partial<TempRequest> | null;
    if (
      detail &&
      (detail.kind === 'edit' || detail.kind === 'keep') &&
      typeof detail.assetId === 'string'
    ) {
      if (waitingKeep?.assetId === detail.assetId) waitingKeep = null;
      listener({ kind: detail.kind, assetId: detail.assetId });
    }
  };
  window.addEventListener(EVENT, handle);
  return () => window.removeEventListener(EVENT, handle);
}
