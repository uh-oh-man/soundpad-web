import { flattenCategories, selectSounds, soundKeys, type Snapshot, type Sound } from './protocol';
import type { Workspace } from './workspace';

export type Totals = Record<string, { total: number; lastPlayedAt: number | null }>;

export type BoardSelection = {
  /** Native Soundpad category id, or 'all'. Used by the host layout. */
  category: string;
  /** Personal folder id, 'favorites' or 'all'. Used by the personal layout. */
  myCategory: string;
  search: string;
};

/** Lowest workspace order index across all of a sound's identities. */
function rankOf(sound: Sound, rank: Map<string, number>) {
  let best = Number.POSITIVE_INFINITY;
  for (const key of soundKeys(sound)) best = Math.min(best, rank.get(key) ?? best);
  return best;
}

function personalSounds(
  state: Snapshot,
  workspace: Workspace,
  { myCategory, search }: BoardSelection,
) {
  const query = search.trim().toLocaleLowerCase();
  const visible = state.library.sounds.filter((s) => !s.internalVariant);
  const ids = query
    ? null
    : myCategory === 'favorites'
      ? workspace.favorites
      : workspace.folders.find((f) => f.id === myCategory)?.soundIds;
  let list: Sound[];
  if (ids) {
    const byKey = new Map(visible.flatMap((s) => soundKeys(s).map((key) => [key, s] as const)));
    list = ids.map((id) => byKey.get(id)).filter((s): s is Sound => !!s);
  } else list = visible;
  const hidden = new Set(workspace.hidden);
  list = list.filter(
    (s) =>
      !soundKeys(s).some((key) => hidden.has(key)) &&
      (!query || s.name.toLocaleLowerCase().includes(query)),
  );
  if (!ids && workspace.order.length) {
    const rank = new Map(workspace.order.map((id, i) => [id, i]));
    const ranks = new Map(list.map((s) => [s, rankOf(s, rank)]));
    list = [...list].sort((a, b) => ranks.get(a)! - ranks.get(b)!);
  }
  return list;
}

function sortValue(sort: Workspace['sort'], a: Sound, b: Sound, totals: Totals) {
  switch (sort) {
    case 'name':
      return a.name.localeCompare(b.name);
    case 'duration':
      return (a.durationMs ?? -1) - (b.durationMs ?? -1);
    case 'native':
      return (a.nativePlays ?? -1) - (b.nativePlays ?? -1);
    case 'web':
      return (totals[a.id]?.total ?? 0) - (totals[b.id]?.total ?? 0);
    case 'last':
      return (totals[a.id]?.lastPlayedAt ?? 0) - (totals[b.id]?.lastPlayedAt ?? 0);
    default:
      return 0;
  }
}

/** The sounds shown on the board for the current layout, selection, search and sort. */
export function arrangeSounds(
  state: Snapshot,
  workspace: Workspace | null | undefined,
  selection: BoardSelection,
  totals: Totals,
): Sound[] {
  let list =
    workspace?.layout === 'mine'
      ? personalSounds(state, workspace, selection)
      : selectSounds(state, selection.category, selection.search);
  if (workspace && workspace.sort !== 'original') {
    const direction = workspace.descending ? -1 : 1;
    list = [...list].sort((a, b) => direction * sortValue(workspace.sort, a, b, totals));
  }
  return [...new Map(list.map((s) => [s.id, s])).values()];
}

/** Heading for the board, matching the selection that produced the sound list. */
export function boardTitle(
  state: Snapshot,
  workspace: Workspace | null | undefined,
  { category, myCategory, search }: BoardSelection,
) {
  if (search.trim()) return 'Search results';
  if (workspace?.layout === 'mine') {
    if (myCategory === 'favorites') return 'Favorites';
    return workspace.folders.find((f) => f.id === myCategory)?.name ?? 'My sounds';
  }
  if (category === 'all') return 'All sounds';
  return (
    flattenCategories(state.library.categories).find((c) => c.category.id === category)?.category
      .name || 'All sounds'
  );
}
