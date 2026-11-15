import { useEffect, useSyncExternalStore } from 'react';
import { controller } from '../../controller';
import type { Command, Snapshot } from '../../protocol';
import {
  applyBodySchema,
  classify,
  editBodySchema,
  organizeViewSchema,
  proposeBodySchema,
  rangeSelect,
  resolveBodySchema,
  toggleSelected,
  UndoStack,
  withParents,
  type OrganizeView,
  type StepInput,
} from '../../organize';
import type { Workspace } from '../../workspace';
import { focusedControl, followFocus } from '../../focus-follow';

/**
 * Organize mode's page state (2.44): whether the board is in Organize mode and what is selected, the
 * companion's plan (fetched once per change it announces, the snapshot's `organize` revision), a guest's
 * own draft before they propose it, and My Layout's Undo. Shared by the board, the sidebar and the panel.
 */
type Store = {
  on: boolean;
  selected: ReadonlySet<string>;
  anchor: string | null;
  view: OrganizeView | null;
  /** A guest's changes not yet proposed, with what to show for each. */
  draft: { input: StepInput; label: string }[];
  undo: number;
  busy: boolean;
};
let store: Store = {
  on: false,
  selected: new Set(),
  anchor: null,
  view: null,
  draft: [],
  undo: 0,
  busy: false,
};
const listeners = new Set<() => void>();
function set(next: Partial<Store>) {
  store = { ...store, ...next };
  for (const listener of listeners) listener();
}
const subscribe = (listener: () => void) => {
  listeners.add(listener);
  return () => listeners.delete(listener);
};
export const useOrganize = () => useSyncExternalStore(subscribe, () => store);
export const organizeState = () => store;

export function setOrganizeMode(on: boolean) {
  const from = focusedControl();
  const leaving = !on && store.on && !!from?.closest('#board, .organize-bar');
  set({ on, ...(on ? {} : { selected: new Set<string>(), anchor: null }) });
  // Leaving takes away the control that had focus (a tile's checkbox, Done): the Organize toggle takes it.
  if (leaving) followFocus(from, () => document.querySelector<HTMLElement>('.organize-toggle'));
}
export function toggleSound(id: string) {
  set({ selected: toggleSelected(store.selected, id), anchor: id });
}
export function selectRange(order: readonly string[], id: string) {
  set({ selected: rangeSelect(order, store.selected, store.anchor, id), anchor: id });
}
export function selectAll(ids: readonly string[]) {
  set({ selected: new Set(ids), anchor: ids[ids.length - 1] ?? null });
}
export function clearSelection() {
  set({ selected: new Set(), anchor: null });
}

// ---- My Layout's Undo (20 steps, in memory) ----
const layoutUndo = new UndoStack<Workspace>(20);
/** Records the layout before an Organize change in My Layout. */
export function rememberLayout(w: Workspace) {
  layoutUndo.push(w);
  set({ undo: layoutUndo.size });
}
export function takeUndo(): Workspace | undefined {
  const w = layoutUndo.pop();
  set({ undo: layoutUndo.size });
  return w;
}

// ---- The companion's plan ----
let fetching: Promise<void> | null = null;
let again = false;
export function refreshOrganize(): Promise<void> {
  if (fetching) {
    again = true;
    return fetching;
  }
  fetching = (async () => {
    try {
      do {
        again = false;
        try {
          set({
            view: organizeViewSchema.parse(
              await controller.run('organizeGet', {}, { quiet: true }),
            ),
          });
        } catch {
          set({ view: null });
        }
      } while (again);
    } finally {
      fetching = null;
    }
  })();
  return fetching;
}
/** Fetches the plan whenever the companion says it changed (and when the library changed: ids move). */
export function useOrganizeSync(state: Snapshot, enabled: boolean) {
  useEffect(() => {
    if (enabled) void refreshOrganize();
    else set({ view: null });
  }, [enabled, state.organize, state.library.version]);
}

async function send(
  op:
    | 'organizeEdit'
    | 'organizeApply'
    | 'organizeResolve'
    | 'organizeDiscard'
    | 'organizePropose'
    | 'organizeWithdraw'
    | 'organizeSettings',
  body: NonNullable<Command['body']>,
) {
  if (store.busy) return null;
  set({ busy: true });
  try {
    const data = await controller.run(op, { body });
    void refreshOrganize();
    return data;
  } catch {
    // The controller shows the failure.
    return null;
  } finally {
    set({ busy: false });
  }
}
/** Host: adds steps to the plan at once (Soundpad changes only on Apply). */
export async function addSteps(steps: StepInput[]) {
  if (!steps.length) return null;
  const revision = store.view?.revision ?? 0;
  const categories = controller.getSnapshot().state.library.categories;
  return send(
    'organizeEdit',
    editBodySchema.parse({ planRevision: revision, add: withParents(steps, categories) }),
  );
}
export const removeSteps = (ids: string[]) =>
  send(
    'organizeEdit',
    editBodySchema.parse({ planRevision: store.view?.revision ?? 0, remove: ids }),
  );
export const acceptProposal = (id: string) =>
  send(
    'organizeEdit',
    editBodySchema.parse({ planRevision: store.view?.revision ?? 0, acceptProposal: id }),
  );
export const rejectProposal = (id: string) =>
  send(
    'organizeEdit',
    editBodySchema.parse({ planRevision: store.view?.revision ?? 0, rejectProposal: id }),
  );
export function applyPlan() {
  const plan = store.view?.plan;
  if (!plan) return Promise.resolve(null);
  return send(
    'organizeApply',
    applyBodySchema.parse({ planId: plan.id, planRevision: plan.revision }),
  );
}
export const resolveStep = (body: Parameters<typeof resolveBodySchema.parse>[0]) =>
  send('organizeResolve', resolveBodySchema.parse(body));
export const discardPlan = () => send('organizeDiscard', {});
export const setGuestOrganize = (guestOrganize: boolean) =>
  send('organizeSettings', { guestOrganize });

// ---- A guest's draft ----
export function addToDraft(steps: StepInput[], label: (step: StepInput) => string) {
  const known = new Set(store.draft.map((d) => JSON.stringify(d.input)));
  // Each category with its parents as shown now, so a proposal sent later can't land on a renumbered one.
  const categories = controller.getSnapshot().state.library.categories;
  const fresh = withParents(steps, categories).filter((s) => !known.has(JSON.stringify(s)));
  // A new category's key must stay unique across the draft.
  const keyed = fresh.map((s) => ({ ...s }));
  const suffix = String(store.draft.length);
  for (const s of keyed) {
    if (s.key) s.key = s.key + suffix;
    if (s.targetKey) s.targetKey = s.targetKey + suffix;
  }
  set({
    draft: [...store.draft, ...keyed.map((input) => ({ input, label: label(input) }))].slice(0, 50),
  });
}
export function dropFromDraft(index: number) {
  const gone = store.draft[index];
  set({
    draft: store.draft.filter(
      (d, i) => i !== index && !(gone?.input.key && d.input.targetKey === gone.input.key),
    ),
  });
}
export const clearDraft = () => set({ draft: [] });
export async function proposeDraft() {
  if (!store.draft.length) return;
  const result = await send(
    'organizePropose',
    proposeBodySchema.parse({ steps: store.draft.map((d) => d.input) }),
  );
  if (result !== null) {
    set({ draft: [] });
    controller.notify('Proposed to the host. They review it before anything changes.', 'success');
  }
}
export const withdrawProposal = (proposalId: string) => send('organizeWithdraw', { proposalId });
export { classify };
