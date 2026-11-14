import { useEffect, useSyncExternalStore } from 'react';
import { controller } from './controller';
import { PREF_DEFAULTS, readPref, subscribePrefs, writePref } from './prefs';
import { applyTheme, currentTheme, subscribeTheme, type Theme } from './appearance';
import { uiPrefsReplySchema, type Snapshot, type UiPrefs } from './protocol';

/**
 * Host style sync (2.43, gate uiSync). The host's browser and the companion window are separate
 * browsers, so their localStorage differs; the companion keeps one shared copy of the style choices
 * (navigation, theme, motion, contrast, header animation) and of Mimic's open draft in its ui.json.
 * A host page applies ui.json over its own choices when it connects and whenever the snapshot's
 * `uiRevision` rises, and sends each change it makes. The first host page to connect to a companion
 * that has no ui.json yet seeds it with its own choices. A browser that syncs for the first time keeps
 * the choices it made before (2.42 kept them per browser) where ui.json still has the default, and sends
 * them, so the page that happened to connect first cannot reset the other one's theme. Guests never send
 * these commands: their choices stay on their own browser. Notice sounds, the panel width and Spotify's
 * row stay per browser.
 */
type Style = Pick<UiPrefs, 'navStyle' | 'theme' | 'motion' | 'contrast' | 'headerAnimation'>;
type ActiveDraft = UiPrefs['activeDraft'];
const STYLE_KEYS = ['navStyle', 'theme', 'motion', 'contrast', 'headerAnimation'] as const;
const DEFAULT_STYLE: Style = {
  navStyle: PREF_DEFAULTS.nav,
  theme: 'red-blue',
  motion: PREF_DEFAULTS.motion,
  contrast: PREF_DEFAULTS.contrast,
  headerAnimation: PREF_DEFAULTS.headerAnimation,
};
/** Set once this browser has synced its style with a companion (before that, its own choices count). */
const SYNCED_KEY = 'soundpad-web:ui-synced';
function syncedBefore() {
  try {
    return localStorage.getItem(SYNCED_KEY) !== null;
  } catch {
    // No storage: nothing was stored either, so there are no earlier choices to keep.
    return true;
  }
}
function markSynced() {
  try {
    localStorage.setItem(SYNCED_KEY, '1');
  } catch {
    // Storage unavailable: the page works without the mark.
  }
}

function localStyle(): Style {
  return {
    navStyle: readPref('nav'),
    theme: currentTheme(),
    motion: readPref('motion'),
    contrast: readPref('contrast'),
    headerAnimation: readPref('headerAnimation'),
  };
}
/** True while ui.json is being applied here, so applying it is never taken for a change to send back. */
let applying = false;
function applyStyle(style: Style, except: Set<keyof Style>) {
  applying = true;
  try {
    applyStyleNow(style, except);
  } finally {
    applying = false;
  }
}
function applyStyleNow(style: Style, except: Set<keyof Style>) {
  const skip = (key: keyof Style) => except.has(key);
  if (!skip('navStyle') && readPref('nav') !== style.navStyle) writePref('nav', style.navStyle);
  if (!skip('theme') && currentTheme() !== style.theme) applyTheme(style.theme as Theme);
  if (!skip('motion') && readPref('motion') !== style.motion) writePref('motion', style.motion);
  if (!skip('contrast') && readPref('contrast') !== style.contrast)
    writePref('contrast', style.contrast);
  if (!skip('headerAnimation') && readPref('headerAnimation') !== style.headerAnimation)
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
/** What this page showed when it last read, applied or noted its style while a pull was running. */
let base: Style | null = null;
/** Style fields chosen on this page while a pull was reading or writing ui.json: they win, and are sent. */
let dirty = new Set<keyof Style>();
/** A choice made while a pull is under way: noted, and the pull runs once more to send it. */
function noteWhileFetching() {
  const now = localStyle();
  if (base) for (const key of STYLE_KEYS) if (now[key] !== base[key]) dirty.add(key);
  base = now;
  again = true;
}
async function pull() {
  if (fetching) {
    again = true;
    return;
  }
  fetching = true;
  try {
    do {
      again = false;
      // What this page shows when the request leaves: a choice made while it is on its way, or while an
      // earlier pass was sending, wins over what comes back and is sent afterwards (it is never
      // overwritten by an older ui.json).
      const before = localStyle();
      base = before;
      let reply;
      try {
        reply = uiPrefsReplySchema.parse(await controller.run('uiPrefsGet', {}, { quiet: true }));
      } catch {
        return;
      }
      if (!syncing) return;
      const now = localStyle();
      const mineKeys = new Set([...dirty, ...STYLE_KEYS.filter((key) => now[key] !== before[key])]);
      dirty = new Set();
      const first = !syncedBefore();
      markSynced();
      if (!reply.saved) {
        // No shared style yet: this page's choices become it.
        remote = reply.prefs;
        base = now;
        await send({ ...now, activeDraft: null });
      } else {
        remote = reply.prefs;
        // This browser's first sync: what it chose before (anything but a default) stays where the
        // shared style still has the default, and is sent.
        if (first)
          for (const key of STYLE_KEYS)
            if (now[key] !== DEFAULT_STYLE[key] && reply.prefs[key] === DEFAULT_STYLE[key])
              mineKeys.add(key);
        applyStyle(reply.prefs, mineKeys);
        const shown = localStyle();
        base = shown;
        emit();
        const mine: Partial<Style> = {};
        for (const key of mineKeys) Object.assign(mine, { [key]: shown[key] });
        if (mineKeys.size) await send(mine);
      }
    } while (again);
  } finally {
    fetching = false;
  }
}

/** A style choice on this page: sent now, or noted for the pull that is under way. */
function styleChanged() {
  if (!syncing || applying) return;
  if (fetching) {
    noteWhileFetching();
    return;
  }
  if (!remote) return;
  const change = changedStyle();
  if (Object.keys(change).length) void send(change);
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
    const stopPrefs = subscribePrefs(styleChanged);
    const stopTheme = subscribeTheme(styleChanged);
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
  base = null;
  dirty = new Set();
  emit();
}
/** Test seam: the hook's parts, without React. */
export const hostStyleSyncForTests = {
  enable(on: boolean) {
    syncing = on;
  },
  pull,
  styleChanged,
  remote: () => remote,
};
