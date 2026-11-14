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
});
export type TempData = z.infer<typeof tempResponse>;
export type TempAsset = z.infer<typeof asset>;
export type TempVariant = z.infer<typeof variant>;

type Store = { data?: TempData; loadError: string; refresh: number };
let store: Store = { loadError: '', refresh: 0 };
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

/** Keeps the list in step while `enabled` (mounted once, by the app). */
export function useTempAssetsSync(enabled: boolean, version: number | undefined) {
  const { refresh } = useTempAssets();
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
  }, [enabled, version, refresh]);
}

/** Test seam. */
export function resetTempAssets() {
  store = { loadError: '', refresh: 0 };
}
