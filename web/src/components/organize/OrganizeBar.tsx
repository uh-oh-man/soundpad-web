import { useState } from 'react';
import { CheckSquare, ClipboardList, Undo2, X } from 'lucide-react';
import { controller } from '../../controller';
import { flattenCategories, type Category, type Snapshot, type Sound } from '../../protocol';
import {
  addToFolder,
  addToNewFolder,
  isAppCategory,
  nameProblem,
  planUnavailable,
  removeFromFolder,
  stepsForAdd,
  stepsForRemove,
  type StepInput,
  type Target,
} from '../../organize';
import type { Workspace } from '../../workspace';
import { requestEditor } from '../../shell';
import { AddToMenu } from './AddToMenu';
import {
  addSteps,
  addToDraft,
  clearSelection,
  rememberLayout,
  selectAll,
  setOrganizeMode,
  takeUndo,
  useOrganize,
} from './organize-store';

/** One line saying what a step does, for a guest's draft. */
export function stepLabel(step: StepInput, sounds: readonly Sound[], categories: Category[]) {
  const sound = sounds.find((s) => s.id === step.soundId)?.name ?? 'a sound';
  const category =
    step.categoryName ??
    flattenCategories(categories).find((c) => c.category.id === step.categoryId)?.category.name ??
    (step.targetKey ? 'the new category' : 'a category');
  switch (step.kind) {
    case 'createCategory':
      return `New category “${step.name}”`;
    case 'addSound':
      return `Add “${sound}” to “${category}”`;
    case 'removeFromCategory':
      return `Remove “${sound}” from “${category}”`;
    case 'removeCategory':
      return `Remove the category “${category}”`;
    case 'renameCategory':
      return `Rename “${category}” to “${step.name}”`;
    case 'createNested':
      return `New category “${step.name}” inside “${category}”`;
    case 'reorder':
      return `Reorder “${category}”`;
  }
}

/** Where a host-layout change goes: the host's plan at once, or a guest's draft to propose. */
export function planSteps(
  host: boolean,
  steps: StepInput[],
  sounds: readonly Sound[],
  categories: Category[],
) {
  if (!steps.length) {
    controller.notify('Nothing to change: those sounds are already there.');
    return;
  }
  if (host) {
    void addSteps(steps).then((done) => {
      if (done)
        controller.notify(
          `Added to Review changes. Soundpad changes only when you apply.`,
          'success',
        );
    });
  } else {
    addToDraft(steps, (s) => stepLabel(s, sounds, categories));
    controller.notify('Added to your proposal. Review changes sends it to the host.', 'success');
  }
}

/**
 * The bar over the board in Organize mode (2.44): what is selected, and what to do with it. In My Layout
 * changes apply at once (with Undo); in the Host Layout they go to Review changes, where the host applies
 * them to Soundpad (and a guest proposes them).
 */
export function OrganizeBar({
  state,
  host,
  workspace: w,
  update,
  visible,
  category,
  folder,
  canPlan,
  pendingCount,
}: {
  state: Snapshot;
  host: boolean;
  workspace: Workspace | null;
  update: (fn: (w: Workspace) => Workspace) => void;
  /** The sounds shown, in order (Ctrl+A and Select all take these). */
  visible: readonly Sound[];
  /** The host category shown (Host Layout), or 'all'. */
  category: string;
  /** The My Layout folder shown, if any. */
  folder?: string;
  /** Host Layout changes are possible (the gate, and for guests the host's permission). */
  canPlan: boolean;
  /** What waits in Review changes. */
  pendingCount: number;
}) {
  const { selected, undo } = useOrganize();
  const [renaming, setRenaming] = useState(false);
  const [newName, setNewName] = useState('');
  const mine = w?.layout === 'mine';
  const ids = visible.filter((s) => selected.has(s.id)).map((s) => s.id);
  const count = ids.length;
  const sounds = state.library.sounds;
  const categories = state.library.categories;
  const flat = flattenCategories(categories).filter(({ category: c }) => !isAppCategory(c.name));
  const current = flat.find((c) => c.category.id === category)?.category;
  const hostItems = flat.map(({ category: c, depth }) => ({
    id: c.id,
    label: c.name || 'Unnamed category',
    depth,
  }));
  function toCategory(target: Target, move = false) {
    planSteps(
      host,
      stepsForAdd(ids, target, categories, move ? current : undefined),
      sounds,
      categories,
    );
  }
  function categoryById(id: string) {
    const c = flat.find((x) => x.category.id === id)!.category;
    return { kind: 'category' as const, category: { id: c.id, name: c.name } };
  }
  function changeLayout(change: (w: Workspace) => Workspace, message: string) {
    if (!w) return;
    rememberLayout(w);
    update(change);
    controller.notify(message, 'success');
  }
  return (
    <div className="organize-bar" role="toolbar" aria-label="Organize">
      <span className="organize-count" role="status" aria-live="polite">
        {count} selected
      </span>
      <button type="button" onClick={() => selectAll(visible.map((s) => s.id))}>
        <CheckSquare size={15} aria-hidden="true" /> Select all
      </button>
      <button
        type="button"
        aria-disabled={!count || undefined}
        onClick={() => count && clearSelection()}
      >
        Clear
      </button>
      {mine && w ? (
        <>
          <AddToMenu
            label="Add selected to…"
            disabled={!count}
            items={w.folders.map((f) => ({ id: f.id, label: f.name }))}
            onPick={(id) =>
              changeLayout(
                (x) => addToFolder(x, id, ids),
                `Added ${count} to ${w.folders.find((f) => f.id === id)?.name}.`,
              )
            }
            newLabel="New folder…"
            onNew={(name) =>
              changeLayout((x) => addToNewFolder(x, name, ids), `Added ${count} to ${name}.`)
            }
          />
          {folder && (
            <button
              type="button"
              aria-disabled={!count || undefined}
              onClick={() =>
                count &&
                changeLayout(
                  (x) => removeFromFolder(x, folder, ids),
                  `Removed ${count} from this folder.`,
                )
              }
            >
              Remove from this folder
            </button>
          )}
          <button
            type="button"
            aria-disabled={!undo || undefined}
            onClick={() => {
              const previous = takeUndo();
              if (previous) update(() => previous);
            }}
          >
            <Undo2 size={15} aria-hidden="true" /> Undo{undo ? ` (${undo})` : ''}
          </button>
        </>
      ) : canPlan ? (
        <>
          <AddToMenu
            label="Add selected to…"
            disabled={!count}
            items={hostItems}
            onPick={(id) => toCategory(categoryById(id))}
            newLabel="New category…"
            onNew={(name) => toCategory({ kind: 'new', name })}
          />
          {current && (
            <>
              <AddToMenu
                label="Move selected to…"
                disabled={!count}
                items={hostItems.filter((x) => x.id !== current.id)}
                onPick={(id) => toCategory(categoryById(id), true)}
              />
              <button
                type="button"
                aria-disabled={!count || undefined}
                onClick={() =>
                  count && planSteps(host, stepsForRemove(ids, current), sounds, categories)
                }
              >
                Remove from this category
              </button>
              <button
                type="button"
                onClick={() =>
                  planSteps(
                    host,
                    [
                      {
                        kind: 'removeCategory',
                        categoryId: current.id,
                        categoryName: current.name,
                      },
                    ],
                    sounds,
                    categories,
                  )
                }
              >
                Remove this category
              </button>
              {renaming ? (
                <form
                  className="organize-rename"
                  onSubmit={(e) => {
                    e.preventDefault();
                    if (nameProblem(newName)) return;
                    planSteps(
                      host,
                      [
                        {
                          kind: 'renameCategory',
                          categoryId: current.id,
                          categoryName: current.name,
                          name: newName,
                        },
                      ],
                      sounds,
                      categories,
                    );
                    setRenaming(false);
                    setNewName('');
                  }}
                >
                  <input
                    aria-label="New category name"
                    value={newName}
                    maxLength={80}
                    autoFocus
                    onChange={(e) => setNewName(e.target.value)}
                  />
                  <button type="submit" aria-disabled={!!nameProblem(newName) || undefined}>
                    Rename
                  </button>
                  <button type="button" onClick={() => setRenaming(false)}>
                    Cancel
                  </button>
                </form>
              ) : (
                <button type="button" onClick={() => setRenaming(true)}>
                  Rename category…
                </button>
              )}
            </>
          )}
          <button
            type="button"
            className="primary"
            onClick={() => requestEditor({ kind: 'organize' })}
          >
            <ClipboardList size={15} aria-hidden="true" /> Review changes ({pendingCount})
          </button>
        </>
      ) : (
        <span className="organize-note">
          {planUnavailable(host, controller.getSnapshot().demo, state.companionVersion, 'bar')}
        </span>
      )}
      <button type="button" onClick={() => setOrganizeMode(false)} aria-label="Done organizing">
        <X size={15} aria-hidden="true" /> Done
      </button>
    </div>
  );
}
