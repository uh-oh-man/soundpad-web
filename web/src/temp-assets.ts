import { useEffect, useSyncExternalStore } from 'react';
import { z } from 'zod';
import { controller } from './controller';

/**
 * The managed-audio list (Temp sources, their variants, issues and file locations), fetched once per
 * change the companion announces and shared by the Temp list and the copy editor in the panel
 * (2.43: they are in different parts of the page, and a guest must not fetch it twice).
 */
const variant = z.object({
  id: z.string(),
  soundId: z.string().nullable(),
  orientation: z.string(),
  state: z.string(),
  ready: z.boolean(),
  permanent: z.boolean(),
  legacyApproval: z.boolean(),
  current: z.boolean(),
  nativeLabel: z.string().nullable().optional(),
});
const asset = z.object({
  id: z.string(),
  name: z.string(),
  origin: z.string(),
  uploader: z.string(),
  state: z.string(),
  normal: variant.nullable(),
  reversed: variant.nullable(),
  variants: z.array(variant),
  hasTemporary: z.boolean(),
});
export const tempResponse = z.object({
  manager: z.object({
    revision: z.number(),
    assets: z.array(asset),
    issues: z.array(
      z.object({
        id: z.string(),
        assetId: z.string().nullable(),
        code: z.string().optional(),
        message: z.string(),
        severity: z.string(),
      }),
    ),
    blocked: z.boolean(),
    temporaryBytes: z.number(),
  }),
  locations: z
    .array(
      z.object({
        assetId: z.string(),
        variantId: z.string(),
        path: z.string(),
        storage: z.string(),
      }),
    )
    .nullable(),
  // 2.46: for a guest, the sources that are theirs to remove (absent from older companions and for the host).
  mine: z.array(z.string()).max(4096).nullable().optional(),
});
export type TempData = z.infer<typeof tempResponse>;
export type TempAsset = z.infer<typeof asset>;
export type TempVariant = z.infer<typeof variant>;

/**
 * Sources taken off the list by a Remove this page asked for (2.46): hidden at once, before the companion
 * answers, and shown again if it refuses. Once removed, the companion's own record keeps them off the list
 * (see `removedFromTemp`).
 */
type Store = { data?: TempData; loadError: string; refresh: number; hidden: ReadonlySet<string> };
let store: Store = { loadError: '', refresh: 0, hidden: new Set() };
const listeners = new Set<() => void>();
const set = (next: Partial<Store>) => {
  store = { ...store, ...next };
  for (const listener of listeners) listener();
};
const subscribe = (listener: () => void) => {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
};
export const useTempAssets = () => useSyncExternalStore(subscribe, () => store);
/** Asks for the list again (after an action that changed it). */
export const refreshTempAssets = () => set({ refresh: store.refresh + 1 });
/** Takes these sources off the list at once (a Remove was pressed). */
export function hideTemp(ids: readonly string[]) {
  set({ hidden: new Set([...store.hidden, ...ids]) });
}
/** Shows these sources again (the companion refused the Remove, or Undo brought them back). */
export function showTemp(ids: readonly string[]) {
  const hidden = new Set(store.hidden);
  for (const id of ids) hidden.delete(id);
  set({ hidden });
}
/**
 * Whether a source was removed from Temp, also while its files are still on the host's PC (2.46). The real
 * Soundpad cannot remove one entry safely, so a removed file stays (retired, "cleanup-pending") until the
 * app's Temp category goes at quit or Clear Temp. That is the companion's business, not a pending item: such a
 * source has nothing left but files on their way out (and maybe the native original it was reversed from).
 */
export function removedFromTemp(a: TempAsset) {
  if (a.state === 'removed') return true;
  if (a.state !== 'cleanup-pending') return false;
  return !a.variants.some(
    (v) =>
      v.permanent ||
      v.legacyApproval ||
      (v.nativeLabel !== null && v.state !== 'cleanup-pending' && v.state !== 'removed'),
  );
}
/** Whether this guest may remove the source: the companion says (2.46), or else it is in their name. */
export function guestOwns(data: TempData | undefined, a: TempAsset, name: string) {
  return data?.mine ? data.mine.includes(a.id) : a.uploader === name;
}

/**
 * Keeps the list in step while `enabled` (mounted once, by the app). `scope` names whose list it is (the
 * example or a host): when it changes, the old list is dropped at once, so a new session never shows, or
 * sends, the last session's sources while its own list is on its way (or if fetching it fails).
 */
export function useTempAssetsSync(enabled: boolean, version: number | undefined, scope: string) {
  const { refresh } = useTempAssets();
  useEffect(() => {
    if (store.data || store.loadError) set({ data: undefined, loadError: '' });
  }, [scope]);
  useEffect(() => {
    if (!enabled) return;
    let live = true;
    // Processing one clip bumps the version several times within a second; fetch once it settles, so a
    // guest doesn't send a burst of requests (9 in 0.4 s were measured) and hit the host's rate limit.
    const timer = setTimeout(() => {
      void controller
        .run('audioAssets')
        .then((raw) => {
          if (live) set({ data: tempResponse.parse(raw), loadError: '' });
        })
        .catch((e: unknown) => {
          if (live) set({ loadError: e instanceof Error ? e.message : String(e) });
        });
    }, 300);
    return () => {
      live = false;
      clearTimeout(timer);
    };
  }, [enabled, version, refresh, scope]);
}

/** Test seam: the list as the page has it now. */
export const tempAssetsSnapshot = () => store;
/** Test seam. */
export function resetTempAssets() {
  store = { loadError: '', refresh: 0, hidden: new Set() };
}
