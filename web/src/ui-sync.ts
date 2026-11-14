import { useEffect, useSyncExternalStore } from 'react';
import { controller } from './controller';
import { readPref, subscribePrefs, writePref } from './prefs';
import { applyTheme, currentTheme, subscribeTheme, type Theme } from './appearance';
import { uiPrefsReplySchema, type Snapshot, type UiPrefs } from './protocol';

/**
 * Host style sync (2.43, gate uiSync). The host's browser and the companion window are separate
 * browsers, so their localStorage differs; the companion keeps one shared copy of the style choices
 * (navigation, theme, motion, contrast, header animation) and of Mimic's open draft in its ui.json.
 * A host page applies ui.json over its own choices when it connects and whenever the snapshot's
 * `uiRevision` rises, and sends each change it makes. The first host page to connect to a companion
 * that has no ui.json yet seeds it with its own choices. Guests never send these commands: their
 * choices stay on their own browser. Notice sounds, the panel width and Spotify's row stay per browser.
 */
type Style = Pick<UiPrefs, 'navStyle' | 'theme' | 'motion' | 'contrast' | 'headerAnimation'>;
type ActiveDraft = UiPrefs['activeDraft'];
const STYLE_KEYS = ['navStyle', 'theme', 'motion', 'contrast', 'headerAnimation'] as const;

function localStyle(): Style {
  return {
    navStyle: readPref('nav'),
    theme: currentTheme(),
    motion: readPref('motion'),
    contrast: readPref('contrast'),
    headerAnimation: readPref('headerAnimation'),
  };
}
function applyStyle(style: Style) {
  if (readPref('nav') !== style.navStyle) writePref('nav', style.navStyle);
  if (currentTheme() !== style.theme) applyTheme(style.theme as Theme);
  if (readPref('motion') !== style.motion) writePref('motion', style.motion);
  if (readPref('contrast') !== style.contrast) writePref('contrast', style.contrast);
  if (readPref('headerAnimation') !== style.headerAnimation)
    writePref('headerAnimation', style.headerAnimation);
}

/** What ui.json held when this page last read or wrote it; null until then (or while not syncing). */
let remote: UiPrefs | null = null;
let syncing = false;
const listeners = new Set<() => void>();
const emit = () => {
  for (const l of listeners) l();
};
function setRemote(next: UiPrefs | null) {
  remote = next;
  emit();
}

async function send(change: Partial<UiPrefs>) {
  try {
    const reply = uiPrefsReplySchema.parse(
      await controller.run('uiPrefsSet', { uiPrefs: change }, { quiet: true }),
    );
    setRemote(reply.prefs);
  } catch {
    // Not saved on the companion (a refusal or a lost connection): this browser keeps the choice.
  }
}

/** The style fields where this browser differs from ui.json. */
function changedStyle(): Partial<Style> {
  if (!remote) return {};
  const local = localStyle();
  const change: Partial<Style> = {};
  for (const key of STYLE_KEYS)
    if (local[key] !== remote[key]) Object.assign(change, { [key]: local[key] });
  return change;
}

let fetching = false;
let again = false;
async function pull() {
  if (fetching) {
    again = true;
    return;
  }
  fetching = true;
  try {
    do {
      again = false;
      let reply;
      try {
        reply = uiPrefsReplySchema.parse(await controller.run('uiPrefsGet', {}, { quiet: true }));
      } catch {
        return;
      }
      if (!syncing) return;
      if (!reply.saved) {
        // No shared style yet: this page's choices become it.
        remote = reply.prefs;
        await send({ ...localStyle(), activeDraft: null });
      } else {
        // Remember ui.json first, so applying it locally is not taken for a change to send back.
        remote = reply.prefs;
        applyStyle(reply.prefs);
        emit();
      }
    } while (again);
  } finally {
    fetching = false;
  }
}

/**
 * Mounted once by the app. `enabled`: this page is the host's (not a guest, not the example) and its
 * companion supports uiSync.
 */
export function useHostStyleSync(state: Snapshot, enabled: boolean) {
  const revision = state.uiRevision ?? 0;
  useEffect(() => {
    syncing = enabled;
    if (!enabled) setRemote(null);
    return () => {
      syncing = false;
    };
  }, [enabled]);
  useEffect(() => {
    if (enabled) void pull();
  }, [enabled, revision]);
  useEffect(() => {
    if (!enabled) return;
    const changed = () => {
      if (!syncing || !remote || fetching) return;
      const change = changedStyle();
      if (Object.keys(change).length) void send(change);
    };
    const stopPrefs = subscribePrefs(changed);
    const stopTheme = subscribeTheme(changed);
    return () => {
      stopPrefs();
      stopTheme();
    };
  }, [enabled]);
}

const subscribe = (listener: () => void) => {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
};
/** The draft open in Mimic on the host's other page, as ui.json has it (null: none, or not syncing). */
export const useSharedDraft = () =>
  useSyncExternalStore(subscribe, () => (syncing ? (remote?.activeDraft ?? null) : null));

/** Tells the host's other pages which draft this one has open (host pages only; a no-op otherwise). */
export function shareActiveDraft(draft: ActiveDraft) {
  if (!syncing || !remote) return;
  const now = remote.activeDraft;
  if (now?.draftId === draft?.draftId && now?.sessionId === draft?.sessionId) return;
  void send({ activeDraft: draft });
}

/** Test seam. */
export function resetHostStyleSync() {
  remote = null;
  syncing = false;
  fetching = false;
  again = false;
  emit();
}
