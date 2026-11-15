import { useEffect, useRef } from 'react';

/**
 * The shell's shared vocabulary (Stage G, 2.43): which main view shows, and what the right editor panel
 * is asked to open. Any part of the page asks through a window event, so a tile, the player or a notice
 * never imports the shell, and the shell never imports them.
 *
 * Phase 0 froze the exported types and event names below. WP-A owns the implementation of the
 * functions (the panel itself listens with `useEditorRequests`/`useViewRequests`).
 */

/** The main views. Mimic is not a view: it is an editor, in the panel. */
export type ShellView = 'board' | 'voices' | 'sentences';
export type NavStyle = 'sidebar' | 'tabs';
export type EditorKind = 'mimic' | 'temp-edit' | 'sound-edit' | 'activity' | 'organize';
/** How the open panel is shown: a grid column, a sheet over the board, or a bar above the player. */
export type PanelMode = 'docked' | 'sheet' | 'minimized';

/** The Edit copy form's fields (TempManager's trimming state), to reopen a copy editor as it was. */
export type EditFields = {
  from: number;
  to: number;
  name: string;
  effect: string;
  speed: number;
  keepPitch: boolean;
};

export type EditorRequest =
  | { kind: 'mimic'; draftId?: string; sessionId?: string }
  | { kind: 'temp-edit'; sourceId: string; fields?: Partial<EditFields> }
  | { kind: 'sound-edit'; soundId: string; focus?: 'effects' | 'statistics' }
  | { kind: 'activity' }
  // Organize mode's Review changes (2.44).
  | { kind: 'organize' };

export type ViewRequest = { view: ShellView; focus?: string };

export const OPEN_EDITOR_EVENT = 'soundpad-web:open-editor';
export const SHOW_VIEW_EVENT = 'soundpad-web:show-view';

const kinds: readonly EditorKind[] = ['mimic', 'temp-edit', 'sound-edit', 'activity', 'organize'];
const views: readonly ShellView[] = ['board', 'voices', 'sentences'];

/** True for a well-formed request (events can come from anywhere on the page). */
export function isEditorRequest(value: unknown): value is EditorRequest {
  if (!value || typeof value !== 'object') return false;
  const r = value as Record<string, unknown>;
  if (!kinds.includes(r.kind as EditorKind)) return false;
  if (r.kind === 'temp-edit') return typeof r.sourceId === 'string' && !!r.sourceId;
  if (r.kind === 'sound-edit')
    return (
      typeof r.soundId === 'string' &&
      !!r.soundId &&
      (r.focus === undefined || r.focus === 'effects' || r.focus === 'statistics')
    );
  return true;
}

/** Asks the shell to open the editor panel at `request` (or switch it there). */
export function requestEditor(request: EditorRequest) {
  if (typeof window === 'undefined') return; // no page to show it on (unit tests)
  window.dispatchEvent(new CustomEvent<EditorRequest>(OPEN_EDITOR_EVENT, { detail: request }));
}

/** Asks the shell to show a main view (Soundboard, Voices, Sentences). */
export function showView(view: ShellView, focus?: string) {
  if (typeof window === 'undefined') return;
  window.dispatchEvent(
    new CustomEvent<ViewRequest>(SHOW_VIEW_EVENT, { detail: focus ? { view, focus } : { view } }),
  );
}

/** Calls the newest `handler` for each editor request while mounted. */
export function useEditorRequests(handler: (request: EditorRequest) => void) {
  const current = useRef(handler);
  current.current = handler;
  useEffect(() => {
    const listen = (e: Event) => {
      const detail = (e as CustomEvent<unknown>).detail;
      if (isEditorRequest(detail)) current.current(detail);
    };
    window.addEventListener(OPEN_EDITOR_EVENT, listen);
    return () => window.removeEventListener(OPEN_EDITOR_EVENT, listen);
  }, []);
}

/** Calls the newest `handler` for each view request while mounted. */
export function useViewRequests(handler: (request: ViewRequest) => void) {
  const current = useRef(handler);
  current.current = handler;
  useEffect(() => {
    const listen = (e: Event) => {
      const detail = (e as CustomEvent<unknown>).detail as Partial<ViewRequest> | null;
      if (detail && views.includes(detail.view as ShellView))
        current.current({
          view: detail.view as ShellView,
          ...(typeof detail.focus === 'string' ? { focus: detail.focus } : {}),
        });
    };
    window.addEventListener(SHOW_VIEW_EVENT, listen);
    return () => window.removeEventListener(SHOW_VIEW_EVENT, listen);
  }, []);
}
