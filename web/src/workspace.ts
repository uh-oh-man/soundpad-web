import { useEffect, useState } from 'react';
import { z } from 'zod';
export const viewModes = ['icons', 'large', 'list', 'details', 'waveforms'] as const;
const soundId = z.string().regex(/^[a-f0-9]{64}$/);
const cue = z
  .object({
    id: z.uuid(),
    name: z.string().min(1).max(80),
    at: z.number().int().min(0).max(1800000),
  })
  .strict();
const region = z
  .object({
    id: z.uuid(),
    name: z.string().min(1).max(80),
    start: z.number().int().min(0).max(1800000),
    end: z.number().int().min(1).max(1800000),
    repeat: z.boolean(),
  })
  .strict()
  .refine((r) => r.end > r.start);
export const workspaceSchema = z
  .object({
    schemaVersion: z.literal(2),
    hostId: soundId,
    layout: z.enum(['host', 'mine']),
    view: z.enum(viewModes),
    sort: z.enum(['original', 'name', 'duration', 'native', 'web', 'last']),
    descending: z.boolean(),
    folders: z
      .array(
        z
          .object({
            id: z.uuid(),
            name: z.string().min(1).max(80),
            parent: z.uuid().nullable(),
            soundIds: z.array(soundId).max(50000),
          })
          .strict(),
      )
      .max(128),
    favorites: z.array(soundId).max(50000),
    hidden: z.array(soundId).max(50000),
    order: z.array(soundId).max(50000),
    cues: z.record(soundId, z.array(cue).max(64)),
    regions: z.record(soundId, z.array(region).max(64)),
    zoom: z.record(soundId, z.number().min(1).max(32)),
    lastCue: z
      .object({
        soundId,
        at: z.number().int().min(0).max(1800000),
        end: z.number().int().optional(),
        repeat: z.boolean().optional(),
      })
      .strict()
      .nullable(),
  })
  .strict()
  .superRefine((w, ctx) => {
    const ids = new Set(w.folders.map((f) => f.id));
    if (ids.size !== w.folders.length)
      ctx.addIssue({ code: 'custom', message: 'Duplicate folder identities.' });
    for (const f of w.folders) {
      let parent = f.parent;
      const seen = new Set([f.id]);
      while (parent) {
        if (!ids.has(parent) || seen.has(parent)) {
          ctx.addIssue({ code: 'custom', message: 'Invalid or cyclic folder tree.' });
          break;
        }
        seen.add(parent);
        parent = w.folders.find((x) => x.id === parent)!.parent;
      }
    }
  })
  .transform((w) => {
    const folders: typeof w.folders = [];
    const visit = (parent: string | null) => {
      for (const f of w.folders.filter((f) => f.parent === parent)) {
        folders.push({ ...f, soundIds: [...new Set(f.soundIds)] });
        visit(f.id);
      }
    };
    visit(null);
    return {
      ...w,
      folders,
      favorites: [...new Set(w.favorites)],
      hidden: [...new Set(w.hidden)],
      order: [...new Set(w.order)],
    };
  });
export type Workspace = z.infer<typeof workspaceSchema>;
export type Cue = z.infer<typeof cue>;
export type Region = z.infer<typeof region>;
export function blankWorkspace(hostId: string): Workspace {
  return {
    schemaVersion: 2,
    hostId,
    layout: 'host',
    view: 'icons',
    sort: 'original',
    descending: false,
    folders: [],
    favorites: [],
    hidden: [],
    order: [],
    cues: {},
    regions: {},
    zoom: {},
    lastCue: null,
  };
}
let database: Promise<IDBDatabase> | undefined;
function db() {
  return (database ??= new Promise<IDBDatabase>((resolve, reject) => {
    const open = indexedDB.open('soundpad-web-personal', 1);
    open.onupgradeneeded = () => {
      open.result.createObjectStore('hosts');
    };
    open.onsuccess = () => resolve(open.result);
    open.onerror = () => {
      database = undefined;
      reject(Error('Browser workspace storage is unavailable.'));
    };
  }));
}
export async function loadWorkspace(hostId: string) {
  const database = await db();
  return new Promise<Workspace | undefined>((resolve, reject) => {
    const tx = database.transaction('hosts', 'readonly');
    const request = tx.objectStore('hosts').get(hostId);
    request.onsuccess = () => {
      try {
        resolve(request.result ? workspaceSchema.parse(request.result) : undefined);
      } catch {
        reject(
          Error(
            'The saved workspace is invalid. It has been preserved; export or explicitly reset it.',
          ),
        );
      }
    };
    request.onerror = () => reject(request.error);
  });
}
export async function saveWorkspace(w: Workspace) {
  workspaceSchema.parse(w);
  const database = await db();
  return new Promise<void>((resolve, reject) => {
    const tx = database.transaction('hosts', 'readwrite');
    tx.objectStore('hosts').put(w, w.hostId);
    tx.oncomplete = () => resolve();
    tx.onerror = () => reject(tx.error);
  });
}
export function parseWorkspace(text: string, hostId: string) {
  if (text.length > 2 * 1024 * 1024) throw Error('Workspace import exceeds 2 MiB.');
  const parsed = workspaceSchema.parse(JSON.parse(text));
  if (parsed.hostId !== hostId) throw Error('This workspace belongs to a different host.');
  return parsed;
}
export function moveSound(w: Workspace, id: string, folder: string | undefined, before?: string) {
  const key = folder ? w.folders.find((x) => x.id === folder)?.soundIds : w.order;
  if (!key) return w;
  const next = key.filter((x) => x !== id);
  const index = before ? next.indexOf(before) : -1;
  next.splice(index < 0 ? next.length : index, 0, id);
  return folder
    ? { ...w, folders: w.folders.map((f) => (f.id === folder ? { ...f, soundIds: next } : f)) }
    : { ...w, order: next };
}
export function toggleFavorite(w: Workspace, id: string) {
  return {
    ...w,
    favorites: w.favorites.includes(id)
      ? w.favorites.filter((x) => x !== id)
      : [...w.favorites, id],
  };
}
export function useWorkspace(hostId: string | undefined | null) {
  const [workspace, set] = useState<Workspace | null>(null);
  const [status, setStatus] = useState('');
  const [loaded, setLoaded] = useState(false);
  const [blocked, setBlocked] = useState(false);
  useEffect(() => {
    let live = true;
    setLoaded(false);
    set(null);
    setBlocked(false);
    if (!hostId) return;
    setStatus('Loading personal workspace…');
    void loadWorkspace(hostId)
      .then((value) => {
        if (live) {
          set(value ?? blankWorkspace(hostId));
          setLoaded(true);
          setStatus('Saved on this browser');
        }
      })
      .catch((e) => {
        if (live) {
          set(blankWorkspace(hostId));
          setLoaded(true);
          setBlocked(true);
          setStatus(String(e.message));
        }
      });
    return () => {
      live = false;
    };
  }, [hostId]);
  useEffect(() => {
    if (!loaded || !workspace || blocked) return;
    setStatus('Saving…');
    const timer = setTimeout(() => {
      void saveWorkspace(workspace)
        .then(() => setStatus('Saved on this browser'))
        .catch(() => setStatus('Could not save. Export your workspace to keep a copy.'));
    }, 250);
    return () => {
      clearTimeout(timer);
      void saveWorkspace(workspace).catch(() => {});
    };
  }, [workspace, loaded, blocked]);
  return {
    workspace,
    status,
    update: (fn: (w: Workspace) => Workspace) => set((w) => (w ? workspaceSchema.parse(fn(w)) : w)),
    replace: (w: Workspace) => {
      setBlocked(false);
      set(workspaceSchema.parse(w));
    },
    ready: loaded,
  };
}
