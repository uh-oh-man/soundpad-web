import { describe, expect, it } from 'vitest';
import {
  UndoStack,
  addToFolder,
  classify,
  copyAll,
  copyShared,
  dragIds,
  dragged,
  groupByTarget,
  nameProblem,
  needsAttention,
  parentsOf,
  planUnavailable,
  rangeSelect,
  rebindCategory,
  removeFromFolder,
  stepInputSchema,
  stepsForAdd,
  stepsForRemove,
  toggleSelected,
  withParents,
} from '../../web/src/organize';
import type { Category, Sound } from '../../web/src/protocol';
import { blankWorkspace, type Workspace } from '../../web/src/workspace';

const id = (n: number) => n.toString(16).padStart(64, '0');
const folder = (n: number) => `00000000-0000-4000-8000-${n.toString().padStart(12, '0')}`;
const sound = (n: number, extra: Partial<Sound> = {}): Sound => ({
  id: id(n),
  name: `Sound ${n}`,
  icon: null,
  ...extra,
});
const categories: Category[] = [
  {
    id: '2',
    name: 'Reactions',
    icon: null,
    soundIds: [id(1)],
    children: [{ id: '3', name: 'Deep cuts', icon: null, soundIds: [id(2)], children: [] }],
  },
  { id: '4', name: 'Empty', icon: null, soundIds: [], children: [] },
  { id: '5', name: 'Temp - Soundpad Web [abcdef-123456]', icon: null, soundIds: [], children: [] },
];
const host = id(999);

describe('selection', () => {
  const order = [id(1), id(2), id(3), id(4), id(5)];
  it('toggles, and Shift selects the range from the anchor in the order shown', () => {
    let s = toggleSelected(new Set(), id(2));
    expect([...s]).toEqual([id(2)]);
    s = rangeSelect(order, s, id(2), id(4));
    expect([...s].sort()).toEqual([id(2), id(3), id(4)]);
    // Backwards, added to what was there.
    s = rangeSelect(order, toggleSelected(new Set(), id(5)), id(5), id(3));
    expect([...s].sort()).toEqual([id(3), id(4), id(5)]);
    // No anchor (or one no longer shown): only the target toggles.
    expect([...rangeSelect(order, new Set(), null, id(1))]).toEqual([id(1)]);
    expect([...rangeSelect(order, new Set(), id(99), id(1))]).toEqual([id(1)]);
    expect(toggleSelected(new Set([id(1)]), id(1)).size).toBe(0);
  });
  it('drags the selection when the dragged sound is in it, else that sound alone', () => {
    const selected = new Set([id(3), id(1)]);
    expect(dragged(selected, id(3), order)).toEqual([id(1), id(3)]);
    expect(dragged(selected, id(2), order)).toEqual([id(2)]);
    expect(dragIds(JSON.stringify([id(1), 'C:\\path.wav', 4]))).toEqual([id(1)]);
    expect(dragIds('not json')).toEqual([]);
  });
});

describe('steps', () => {
  it('adds only sounds not already in the category, and a move adds the guided removal', () => {
    const steps = stepsForAdd(
      [id(1), id(2)],
      { kind: 'category', category: { id: '2', name: 'Reactions' } },
      categories,
    );
    expect(steps).toEqual([
      { kind: 'addSound', soundId: id(2), categoryId: '2', categoryName: 'Reactions' },
    ]);
    const move = stepsForAdd(
      [id(1)],
      { kind: 'category', category: { id: '4', name: 'Empty' } },
      categories,
      { id: '2', name: 'Reactions' },
    );
    expect(move.map((s) => s.kind)).toEqual(['addSound', 'removeFromCategory']);
    expect(move[1]).toMatchObject({ categoryId: '2', soundId: id(1) });
    // Into a new category: created first, then added by key.
    const created = stepsForAdd([id(1), id(1)], { kind: 'new', name: 'Loud' }, categories);
    expect(created).toEqual([
      { kind: 'createCategory', name: 'Loud', key: 'new' },
      { kind: 'addSound', soundId: id(1), targetKey: 'new' },
    ]);
    expect(stepsForRemove([id(1)], { id: '2', name: 'Reactions' })).toEqual([
      { kind: 'removeFromCategory', soundId: id(1), categoryId: '2', categoryName: 'Reactions' },
    ]);
    // Never a path: the schema has no field for one.
    expect(
      stepInputSchema.safeParse({ kind: 'addSound', path: 'C:\\x.wav', categoryId: '4' }).success,
    ).toBe(false);
    for (const s of [...steps, ...move, ...created]) expect(stepInputSchema.parse(s)).toEqual(s);
  });
  it('says what Soundpad can do and what is done in Soundpad, and why', () => {
    const sounds = [sound(1, { name: 'Airhorn' })];
    expect(classify({ kind: 'createCategory', name: 'X' }, categories).support).toBe('native');
    expect(
      classify({ kind: 'addSound', soundId: id(1), categoryId: '4' }, categories).support,
    ).toBe('native');
    expect(classify({ kind: 'removeCategory', categoryId: '4' }, categories)).toMatchObject({
      support: 'native',
    });
    expect(classify({ kind: 'removeCategory', categoryId: '2' }, categories).reason).toContain(
      'inside',
    );
    expect(classify({ kind: 'removeCategory', categoryId: '3' }, categories).reason).toContain(
      'only top-level',
    );
    const removeFrom = classify(
      { kind: 'removeFromCategory', soundId: id(1), categoryId: '2' },
      categories,
      sounds,
    );
    expect(removeFrom.support).toBe('manual');
    expect(removeFrom.reason).toContain('\u201cAirhorn\u201d');
    for (const kind of ['renameCategory', 'createNested', 'reorder'] as const)
      expect(classify({ kind, categoryId: '2', name: 'New' }, categories).support).toBe('manual');
  });
  it('groups steps by the category they act on, a new one by its name', () => {
    const view = (over: Record<string, unknown>) => ({
      id: String(Math.random()),
      kind: 'addSound',
      name: null,
      targetStepId: null,
      target: null,
      ...over,
    });
    const make = view({ id: 'm', kind: 'createCategory', name: 'Loud' });
    const groups = groupByTarget([
      make,
      view({ targetStepId: 'm' }),
      view({ target: { name: 'Empty', chain: [] } }),
      view({ target: { name: 'Deep cuts', chain: ['Reactions'] } }),
      view({ target: { name: 'Empty', chain: [] } }),
    ]);
    expect(groups.map((g) => [g.label, g.steps.length])).toEqual([
      ['Loud', 2],
      ['Empty', 2],
      ['Reactions › Deep cuts', 1],
    ]);
  });
  it('checks names as the companion does', () => {
    expect(nameProblem('Memes (2024), loud')).toBeNull();
    expect(nameProblem('')).not.toBeNull();
    expect(nameProblem(' padded')).not.toBeNull();
    expect(nameProblem('say "hi"')).not.toBeNull();
    expect(nameProblem('x'.repeat(81))).not.toBeNull();
  });
});

describe('Needs attention and the selected category', () => {
  it('puts missing sounds last, in their own group, keeping the order of the rest', () => {
    const list = [sound(1), sound(2, { missing: true }), sound(3), sound(4, { missing: true })];
    const { ok, missing } = needsAttention(list);
    expect(ok.map((s) => s.name)).toEqual(['Sound 1', 'Sound 3']);
    expect(missing.map((s) => s.name)).toEqual(['Sound 2', 'Sound 4']);
  });
  it('follows a category by name when its id now belongs to another', () => {
    expect(rebindCategory('4', 'Empty', categories)).toBe('4');
    const shifted: Category[] = [
      { ...categories[1], id: '2' },
      { ...categories[0], id: '3' },
    ];
    expect(rebindCategory('2', 'Reactions', shifted)).toBe('3');
    expect(rebindCategory('9', 'Gone', categories)).toBe('all');
    expect(rebindCategory('all', null, categories)).toBe('all');
  });
});

describe('My Layout', () => {
  it('keeps the last 20 layouts for Undo', () => {
    const stack = new UndoStack<number>(20);
    for (let i = 0; i < 25; i++) stack.push(i);
    expect(stack.size).toBe(20);
    expect(stack.pop()).toBe(24);
    for (let i = 0; i < 18; i++) stack.pop();
    expect(stack.pop()).toBe(5);
    expect(stack.pop()).toBeUndefined();
  });
  it('adds to and removes from a folder, each sound once', () => {
    let w: Workspace = {
      ...blankWorkspace(host),
      folders: [{ id: folder(1), name: 'Mine', parent: null, soundIds: [id(1)] }],
    };
    w = addToFolder(w, folder(1), [id(1), id(2), id(2)]);
    expect(w.folders[0].soundIds).toEqual([id(1), id(2)]);
    w = removeFromFolder(w, folder(1), [id(1)]);
    expect(w.folders[0].soundIds).toEqual([id(2)]);
  });
});

describe('Copy into My Layout', () => {
  const shared = {
    folders: [
      { id: folder(1), name: 'Theirs', parent: null, soundIds: [id(1)] },
      { id: folder(2), name: 'Inside', parent: folder(1), soundIds: [id(2)] },
      { id: folder(3), name: 'Other', parent: null, soundIds: [id(3)] },
    ],
    favorites: [id(1)],
    hidden: [id(4)],
    order: [id(3), id(1)],
  };
  const mine: Workspace = {
    ...blankWorkspace(host),
    folders: [
      { id: folder(1), name: 'Theirs', parent: null, soundIds: [id(5)] },
      { id: folder(9), name: 'Mine', parent: null, soundIds: [] },
    ],
    favorites: [id(5)],
    order: [id(5), id(1)],
  };
  it('merges by default: folders gain sounds, lists combine, the shared order first', () => {
    const next = copyShared(mine, shared, copyAll(shared));
    expect(next.layout).toBe('mine');
    expect(next.folders.map((f) => f.name)).toEqual(['Theirs', 'Mine', 'Inside', 'Other']);
    expect(next.folders[0].soundIds).toEqual([id(5), id(1)]);
    expect(next.favorites).toEqual([id(5), id(1)]);
    expect(next.hidden).toEqual([id(4)]);
    expect(next.order).toEqual([id(3), id(1), id(5)]);
  });
  it('copies only the chosen folder (with its parents) and only the chosen parts', () => {
    const next = copyShared(mine, shared, {
      folders: [folder(2)],
      favorites: false,
      hidden: false,
      order: false,
      mode: 'merge',
    });
    expect(next.folders.map((f) => f.name)).toEqual(['Theirs', 'Mine', 'Inside']);
    expect(next.favorites).toEqual(mine.favorites);
    expect(next.order).toEqual(mine.order);
    const other = copyShared(mine, shared, {
      folders: [folder(3)],
      favorites: false,
      hidden: false,
      order: false,
      mode: 'merge',
    });
    expect(other.folders.map((f) => f.name)).toEqual(['Theirs', 'Mine', 'Other']);
    expect(other.folders[0].soundIds).toEqual([id(5)]);
  });
  it('replace swaps each chosen part for the shared one', () => {
    const next = copyShared(mine, shared, {
      folders: [folder(3)],
      favorites: true,
      hidden: false,
      order: true,
      mode: 'replace',
    });
    expect(next.folders.map((f) => f.name)).toEqual(['Other']);
    expect(next.favorites).toEqual([id(1)]);
    expect(next.hidden).toEqual([]);
    expect(next.order).toEqual(shared.order);
    // No folders chosen: the person's folders stay.
    expect(
      copyShared(mine, shared, { ...copyAll(shared), folders: [], mode: 'replace' }).folders,
    ).toEqual(mine.folders);
  });
});

describe('2.44 review', () => {
  it('sends each category with its parents, so a renumbered id is refused', () => {
    expect(parentsOf(categories, '2')).toEqual([]);
    expect(parentsOf(categories, '3')).toEqual(['Reactions']);
    expect(parentsOf(categories, '99')).toBeUndefined();
    const steps = withParents(
      [
        { kind: 'addSound', soundId: id(1), categoryId: '3', categoryName: 'Deep cuts' },
        { kind: 'createCategory', name: 'New', key: 'new' },
        { kind: 'removeCategory', categoryId: '4', categoryName: 'Empty' },
      ],
      categories,
    );
    expect(steps.map((s) => s.categoryParents)).toEqual([['Reactions'], undefined, []]);
    for (const s of steps) expect(stepInputSchema.parse(s)).toEqual(s);
  });
  it('says why Host Layout changes cannot be planned, truthfully', () => {
    expect(planUnavailable(true, true, '2.43.0', 'bar')).toBe(
      "The example doesn't change Soundpad categories. My Layout works here.",
    );
    expect(planUnavailable(true, false, '2.43.0', 'bar')).toContain('needs companion 2.44');
    expect(planUnavailable(false, false, '2.43.0', 'bar')).toContain(
      "The host's companion can't take organization proposals yet.",
    );
    expect(planUnavailable(false, false, '2.44.0', 'review')).toBe(
      'The host has not allowed organization proposals.',
    );
  });
});
