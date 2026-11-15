import { z } from 'zod';
import { flattenCategories, soundKeys, type Category, type Sound } from './protocol';
import type { Workspace } from './workspace';
import type { Organization } from './shared-layout';
import { supports } from './version';

/**
 * Organize mode (2.44): the pure rules behind it, unit-tested like arrange.ts. Selecting sounds without
 * playing them, turning drops into organization steps, saying which steps Soundpad can do and which the
 * host does in Soundpad, the Needs attention group, My Layout's Undo stack and the shared-layout copy.
 */

// ---- Selection ----

/** Toggles one sound in the selection. */
export function toggleSelected(selected: ReadonlySet<string>, id: string): Set<string> {
  const next = new Set(selected);
  if (next.has(id)) next.delete(id);
  else next.add(id);
  return next;
}
/**
 * Shift-click: everything between the anchor and the target (in the order shown) is selected, added to
 * what was selected. Without an anchor still shown, only the target toggles.
 */
export function rangeSelect(
  order: readonly string[],
  selected: ReadonlySet<string>,
  anchor: string | null,
  target: string,
): Set<string> {
  const from = anchor === null ? -1 : order.indexOf(anchor);
  const to = order.indexOf(target);
  if (to < 0) return new Set(selected);
  if (from < 0) return toggleSelected(selected, target);
  const next = new Set(selected);
  for (let i = Math.min(from, to); i <= Math.max(from, to); i++) next.add(order[i]);
  return next;
}
/** What a drag carries: the selection when the dragged sound is part of it, else that sound alone. */
export function dragged(selected: ReadonlySet<string>, id: string, order: readonly string[]) {
  return selected.has(id) ? order.filter((x) => selected.has(x)) : [id];
}
export const DRAG_TYPE = 'application/x-soundpad-ids';
export function dragIds(raw: string): string[] {
  try {
    const value: unknown = JSON.parse(raw);
    return Array.isArray(value)
      ? value
          .filter((x): x is string => typeof x === 'string' && /^[a-f0-9]{64}$/.test(x))
          .slice(0, 500)
      : [];
  } catch {
    return [];
  }
}

// ---- Steps ----

export const stepKinds = [
  'createCategory',
  'addSound',
  'removeCategory',
  'removeFromCategory',
  'renameCategory',
  'createNested',
  'reorder',
] as const;
export type StepKind = (typeof stepKinds)[number];
/** One change as the page sends it (organizeEdit add, organizePropose): ids and names only, never a path. */
export const stepInputSchema = z
  .object({
    kind: z.enum(stepKinds),
    soundId: z
      .string()
      .regex(/^[a-f0-9]{64}$/)
      .optional(),
    categoryId: z.string().max(16).optional(),
    categoryName: z.string().max(4096).optional(),
    /** The category's parents' names as the page shows them, top first (the companion refuses a stale id that moved). */
    categoryParents: z.array(z.string().max(4096)).max(16).optional(),
    targetStepId: z
      .string()
      .regex(/^[a-f0-9]{32}$/)
      .optional(),
    targetKey: z.string().max(32).optional(),
    name: z.string().max(80).optional(),
    key: z.string().max(32).optional(),
  })
  .strict();
export type StepInput = z.infer<typeof stepInputSchema>;
export const editBodySchema = z
  .object({
    planRevision: z.number().int().nonnegative(),
    add: z.array(stepInputSchema).max(50).optional(),
    remove: z
      .array(z.string().regex(/^[a-f0-9]{32}$/))
      .max(200)
      .optional(),
    acceptProposal: z
      .string()
      .regex(/^[a-f0-9]{32}$/)
      .optional(),
    rejectProposal: z
      .string()
      .regex(/^[a-f0-9]{32}$/)
      .optional(),
  })
  .strict();
export const proposeBodySchema = z
  .object({ steps: z.array(stepInputSchema).min(1).max(50) })
  .strict();
export const resolutions = [
  'skip',
  'retry',
  'rebind',
  'done',
  'cancel',
  'continue',
  'stop',
  'removeCreated',
] as const;
export const resolveBodySchema = z
  .object({
    resolution: z.enum(resolutions),
    stepId: z
      .string()
      .regex(/^[a-f0-9]{32}$/)
      .optional(),
    targetCategoryId: z.string().max(16).optional(),
    targetCategoryName: z.string().max(4096).optional(),
    targetCategoryParents: z.array(z.string().max(4096)).max(16).optional(),
  })
  .strict();
export const applyBodySchema = z
  .object({ planId: z.string().regex(/^[a-f0-9]{32}$/), planRevision: z.number().int() })
  .strict();

const categoryViewSchema = z.object({
  categoryId: z.string().max(16).nullable(),
  name: z.string().max(4096),
  chain: z.array(z.string().max(4096)).max(16),
});
export const stepViewSchema = z.object({
  id: z.string().max(32),
  kind: z.enum(stepKinds),
  support: z.enum(['native', 'manual']),
  reason: z.string().max(600),
  state: z.enum([
    'pending',
    'applying',
    'verified',
    'conflict',
    'failed',
    'skipped',
    'manual',
    'done',
    'flagged',
  ]),
  note: z.string().max(600).nullable(),
  by: z.string().max(40),
  mine: z.boolean(),
  soundId: z.string().max(64).nullable(),
  soundName: z.string().max(4096).nullable(),
  target: categoryViewSchema.nullable(),
  targetStepId: z.string().max(32).nullable(),
  name: z.string().max(80).nullable(),
  candidate: categoryViewSchema.nullable(),
  proposalId: z.string().max(32).nullable(),
  addedToAllSounds: z.boolean().nullable(),
});
export type StepView = z.infer<typeof stepViewSchema>;
export const organizeViewSchema = z.object({
  revision: z.number().int().nonnegative(),
  plan: z
    .object({
      id: z.string().max(32),
      revision: z.number().int().nonnegative(),
      state: z.enum(['draft', 'applying', 'paused', 'done']),
      paused: z.string().max(40).nullable(),
      pausedText: z.string().max(600).nullable(),
      waiting: z.boolean(),
      steps: z.array(stepViewSchema).max(200),
      addBehaviour: z.enum(['unknown', 'noDuplicate', 'duplicates']),
    })
    .nullable(),
  proposals: z
    .array(
      z.object({
        id: z.string().max(32),
        by: z.string().max(40),
        mine: z.boolean(),
        createdAt: z.number(),
        steps: z.array(stepViewSchema).max(50),
      }),
    )
    .max(400),
  settings: z.object({
    guestOrganize: z.boolean(),
    addBehaviour: z.enum(['unknown', 'noDuplicate', 'duplicates']),
  }),
  problem: z.string().max(600).nullable().optional(),
  recovery: z
    .object({
      created: z.number().int().nonnegative(),
      added: z
        .array(z.object({ soundName: z.string().nullable(), category: z.string().nullable() }))
        .max(200),
    })
    .nullable()
    .optional(),
  checkpoint: z
    .object({ copy: z.string().max(1024), takenAtUtcMs: z.number().nullable().optional() })
    .nullable()
    .optional(),
});
export type OrganizeView = z.infer<typeof organizeViewSchema>;

/** The app's own categories (Temp and Saved), which are never offered as targets. */
export const isAppCategory = (name: string) => /^(Temp|Saved) - Soundpad Web \[/.test(name);
/** A category name the companion accepts (it checks again, and for Soundpad's code page). */
export function nameProblem(name: string): string | null {
  if (!name.trim()) return 'Type a name.';
  if (name.length > 80) return 'Use at most 80 characters.';
  if (name !== name.trim()) return 'Remove the spaces at the start or end.';
  if (/["\p{Cc}\p{Cf}\p{Co}\p{Cs}]/u.test(name))
    return 'Use letters, digits and ordinary punctuation, without quotation marks.';
  return null;
}

export type Target =
  { kind: 'category'; category: Pick<Category, 'id' | 'name'> } | { kind: 'new'; name: string };
/**
 * The steps for adding sounds to a category: each sound not already in it. With `from` (a move), each
 * also gets the guided "remove from" step for the category it leaves: Soundpad can't move an entry, and
 * Soundpad Web never fakes a move by re-importing.
 */
export function stepsForAdd(
  soundIds: readonly string[],
  target: Target,
  categories: Category[],
  from?: Pick<Category, 'id' | 'name'>,
): StepInput[] {
  const steps: StepInput[] = [];
  let inside = new Set<string>();
  let into: Partial<StepInput>;
  if (target.kind === 'new') {
    steps.push({ kind: 'createCategory', name: target.name, key: 'new' });
    into = { targetKey: 'new' };
  } else {
    const found = flattenCategories(categories).find((c) => c.category.id === target.category.id);
    inside = new Set(found?.category.soundIds ?? []);
    into = { categoryId: target.category.id, categoryName: target.category.name };
  }
  const fromIds = from
    ? new Set(
        flattenCategories(categories).find((c) => c.category.id === from.id)?.category.soundIds ??
          [],
      )
    : null;
  for (const id of new Set(soundIds)) {
    if (!inside.has(id)) steps.push({ kind: 'addSound', soundId: id, ...into });
    if (from && fromIds?.has(id) && !(target.kind === 'category' && target.category.id === from.id))
      steps.push({
        kind: 'removeFromCategory',
        soundId: id,
        categoryId: from.id,
        categoryName: from.name,
      });
  }
  return steps;
}
/** The guided steps for taking sounds out of a category (Soundpad can't do it from outside). */
export function stepsForRemove(soundIds: readonly string[], from: Pick<Category, 'id' | 'name'>) {
  return [...new Set(soundIds)].map<StepInput>((id) => ({
    kind: 'removeFromCategory',
    soundId: id,
    categoryId: from.id,
    categoryName: from.name,
  }));
}

/**
 * Whether Soundpad can do a step itself ("native") or the host does it in Soundpad ("manual"), and why,
 * in one line. The companion says the same for the steps it holds; this is for a guest's draft before it
 * is proposed. A category removal is native only for an empty top-level category.
 */
export function classify(
  step: StepInput,
  categories: Category[],
  sounds: readonly Sound[] = [],
): { support: 'native' | 'manual'; reason: string } {
  const flat = flattenCategories(categories);
  const found = flat.find((c) => c.category.id === step.categoryId);
  const target = `“${step.categoryName ?? found?.category.name ?? 'the category'}”`;
  const sound = sounds.find((s) => s.id === step.soundId)?.name;
  const named = sound ? `“${sound}”` : 'the sound';
  switch (step.kind) {
    case 'createCategory':
      return {
        support: 'native',
        reason: 'Soundpad can do this: a new top-level category at the end of the list.',
      };
    case 'addSound':
      return {
        support: 'native',
        reason: 'Soundpad can do this: the sound is added at the end of the category.',
      };
    case 'removeCategory':
      if (found && found.depth > 0)
        return {
          support: 'manual',
          reason: `Do this in Soundpad: Soundpad Web removes only top-level categories. Remove ${target} there.`,
        };
      if (found && found.category.children.length)
        return {
          support: 'manual',
          reason: `Do this in Soundpad: remove the categories inside ${target} first.`,
        };
      if (found && found.category.soundIds.length)
        return {
          support: 'manual',
          reason: `Do this in Soundpad first: move or remove the sounds in ${target} (removing a category with sounds also takes them out of All sounds). Then Soundpad Web can remove it.`,
        };
      return { support: 'native', reason: 'Soundpad can do this: the category is empty.' };
    case 'removeFromCategory':
      return {
        support: 'manual',
        reason: `Do this in Soundpad: Soundpad can't take a sound out of a category from outside. Open ${target} and remove ${named} from that category only.`,
      };
    case 'renameCategory':
      return {
        support: 'manual',
        reason: `Do this in Soundpad: rename ${target} to “${step.name ?? ''}”. Soundpad can't rename from outside.`,
      };
    case 'createNested':
      return {
        support: 'manual',
        reason: `Do this in Soundpad: add “${step.name ?? ''}” inside ${target}. Soundpad Web creates only top-level categories.`,
      };
    case 'reorder':
      return {
        support: 'manual',
        reason: `Do this in Soundpad: drag to reorder ${target}. Soundpad can't reorder from outside.`,
      };
  }
}

/** Steps grouped by the category they act on (a new category by its name), in first-seen order. */
export function groupByTarget<
  T extends {
    target: { name: string; chain: string[] } | null;
    name: string | null;
    kind: string;
    targetStepId: string | null;
    id: string;
  },
>(steps: readonly T[]): { key: string; label: string; steps: T[] }[] {
  const groups = new Map<string, { key: string; label: string; steps: T[] }>();
  const created = new Map(
    steps.filter((s) => s.kind === 'createCategory').map((s) => [s.id, s.name ?? '']),
  );
  for (const step of steps) {
    const label =
      step.kind === 'createCategory'
        ? (step.name ?? '')
        : step.targetStepId
          ? (created.get(step.targetStepId) ?? 'New category')
          : step.target
            ? [...step.target.chain, step.target.name].join(' › ')
            : 'Other';
    const key = label;
    const group = groups.get(key) ?? { key, label, steps: [] };
    group.steps.push(step);
    groups.set(key, group);
  }
  return [...groups.values()];
}

/**
 * Why Host Layout changes can't be planned here, in words (the bar's note, or Review changes'). The
 * example never changes Soundpad; an older companion can't take a plan or proposals; otherwise a guest's
 * host has not allowed proposals (or the host page is not connected).
 */
export function planUnavailable(
  host: boolean,
  demo: boolean,
  companionVersion: string | null | undefined,
  where: 'bar' | 'review',
) {
  const mine = where === 'bar' ? ' My Layout works here.' : ' My Layout organizing works here.';
  if (demo) return "The example doesn't change Soundpad categories." + mine;
  if (!supports(companionVersion, 'organize'))
    return host
      ? where === 'bar'
        ? 'Changing Soundpad categories needs companion 2.44. My Layout works now.'
        : 'Review changes needs companion 2.44 or later. My Layout organizing works without it.'
      : "The host's companion can't take organization proposals yet." +
          (where === 'bar' ? ' My Layout is yours to organize.' : '');
  if (host) return 'Connect to the companion to change Soundpad categories.' + mine;
  return (
    'The host has not allowed organization proposals.' +
    (where === 'bar' ? ' My Layout is yours to organize.' : '')
  );
}

/** The names of a category's parents, top first; undefined when it is not in `categories`. */
export function parentsOf(categories: Category[], id: string): string[] | undefined {
  const walk = (list: Category[], chain: string[]): string[] | undefined => {
    for (const c of list) {
      if (c.id === id) return chain;
      const found = walk(c.children, [...chain, c.name]);
      if (found) return found;
    }
    return undefined;
  };
  return walk(categories, []);
}
/** Steps with each category's parents added (for the companion's check that an id still names that category). */
export function withParents(steps: StepInput[], categories: Category[]): StepInput[] {
  return steps.map((s) => {
    if (s.categoryId === undefined || s.categoryParents) return s;
    const parents = parentsOf(categories, s.categoryId);
    return parents ? { ...s, categoryParents: parents } : s;
  });
}

// ---- Needs attention ----

/** Sounds whose file is missing go to the end, in their own group; the rest keep their order. */
export function needsAttention(sounds: readonly Sound[]) {
  const ok: Sound[] = [];
  const missing: Sound[] = [];
  for (const sound of sounds) (sound.missing ? missing : ok).push(sound);
  return { ok, missing };
}

// ---- My Layout ----

/** The last 20 layouts before each Organize change in My Layout, for Undo. In memory only. */
export class UndoStack<T> {
  private items: T[] = [];
  constructor(private limit = 20) {}
  push(item: T) {
    this.items.push(item);
    if (this.items.length > this.limit) this.items.shift();
  }
  pop(): T | undefined {
    return this.items.pop();
  }
  get size() {
    return this.items.length;
  }
  clear() {
    this.items = [];
  }
}
/** Adds sounds to a My Layout folder (at the end, each once). */
export function addToFolder(w: Workspace, folderId: string, ids: readonly string[]): Workspace {
  return {
    ...w,
    folders: w.folders.map((f) =>
      f.id === folderId ? { ...f, soundIds: [...new Set([...f.soundIds, ...ids])] } : f,
    ),
  };
}
/** Adds sounds to a new My Layout folder at the top level. */
export function addToNewFolder(
  w: Workspace,
  name: string,
  ids: readonly string[],
  id = crypto.randomUUID(),
) {
  return {
    ...w,
    folders: [...w.folders, { id, name, parent: null, soundIds: [...new Set(ids)] }],
  };
}
/** Takes sounds out of a My Layout folder. */
export function removeFromFolder(
  w: Workspace,
  folderId: string,
  ids: readonly string[],
): Workspace {
  const gone = new Set(ids);
  return {
    ...w,
    folders: w.folders.map((f) =>
      f.id === folderId ? { ...f, soundIds: f.soundIds.filter((x) => !gone.has(x)) } : f,
    ),
  };
}

// ---- Copy a shared layout into My Layout ----

export type CopyChoice = {
  /** The shared layout's folders to copy, by id (a folder's parents come with it). */
  folders: string[];
  favorites: boolean;
  hidden: boolean;
  order: boolean;
  mode: 'merge' | 'replace';
};
export const copyAll = (shared: Organization): CopyChoice => ({
  folders: shared.folders.map((f) => f.id),
  favorites: true,
  hidden: true,
  order: true,
  mode: 'merge',
});
/**
 * My Layout after copying the chosen parts of a guest's shared layout. Merge (the default) keeps what is
 * there and adds: folders with the same identity gain the shared sounds, favourites and hidden sounds are
 * combined, and the shared order comes first with the rest after it. Replace swaps each chosen part for
 * the shared one. Parts not chosen stay as they are.
 */
export function copyShared(
  current: Workspace,
  shared: Organization,
  choice: CopyChoice,
): Workspace {
  const byId = new Map(shared.folders.map((f) => [f.id, f]));
  const chosen = new Set<string>();
  for (const id of choice.folders) {
    let folder = byId.get(id);
    // A chosen folder brings its parents, so the tree stays whole.
    for (let depth = 0; folder && depth < 17; depth++) {
      chosen.add(folder.id);
      folder = folder.parent ? byId.get(folder.parent) : undefined;
    }
  }
  const picked = shared.folders.filter((f) => chosen.has(f.id));
  let folders: Workspace['folders'];
  if (!choice.folders.length) folders = current.folders;
  else if (choice.mode === 'replace') folders = picked;
  else {
    const mine = new Map(current.folders.map((f) => [f.id, f]));
    folders = current.folders.map((f) => {
      const other = byId.get(f.id);
      return other && chosen.has(f.id)
        ? { ...f, soundIds: [...new Set([...f.soundIds, ...other.soundIds])] }
        : f;
    });
    folders = [...folders, ...picked.filter((f) => !mine.has(f.id))];
  }
  const ids = new Set(folders.map((f) => f.id));
  folders = folders
    .map((f) => (f.parent && !ids.has(f.parent) ? { ...f, parent: null } : f))
    .slice(0, 128);
  const union = (a: string[], b: string[]) => [...new Set([...a, ...b])];
  const pick = (
    on: boolean,
    mine: string[],
    theirs: string[],
    merge: (a: string[], b: string[]) => string[],
  ) => (!on ? mine : choice.mode === 'replace' ? theirs : merge(mine, theirs));
  return {
    ...current,
    folders,
    favorites: pick(choice.favorites, current.favorites, shared.favorites, union),
    hidden: pick(choice.hidden, current.hidden, shared.hidden, union),
    order: pick(choice.order, current.order, shared.order, (mine, theirs) => union(theirs, mine)),
    layout: 'mine',
  };
}

/** The category a selection held by id points at now: the same id if its name still matches, else the one with that name. */
export function rebindCategory(id: string, name: string | null, categories: Category[]): string {
  if (id === 'all') return id;
  const flat = flattenCategories(categories);
  const same = flat.find((c) => c.category.id === id);
  if (!name || same?.category.name === name) return same ? id : 'all';
  return flat.find((c) => c.category.name === name)?.category.id ?? 'all';
}

/** Sound keys (all identities) of a list of sounds, for My Layout's id-keyed lists. */
export const keysOf = (sounds: readonly Sound[]) => sounds.flatMap((s) => soundKeys(s));
