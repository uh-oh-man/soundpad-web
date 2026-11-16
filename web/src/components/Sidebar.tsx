import type { ButtonHTMLAttributes, DragEvent, ReactNode } from 'react';
import {
  AudioLines,
  Folder,
  Music2,
  PanelLeftClose,
  PanelLeftOpen,
  ShieldCheck,
} from 'lucide-react';
import type { Category, Snapshot } from '../protocol';
import { moveSound, type Workspace } from '../workspace';
import type { BoardSelection } from '../arrange';
import { SpotifyPanel } from './SpotifyPanel';
import { DRAG_TYPE, addToFolder, dragIds, isAppCategory, stepsForAdd } from '../organize';
import { rememberLayout, useOrganize } from './organize/organize-store';
import { planSteps } from './organize/OrganizeBar';

type Update = (change: (current: Workspace) => Workspace) => void;

function NavButton({
  selected,
  depth = 0,
  icon,
  label,
  count,
  ...rest
}: {
  selected: boolean;
  depth?: number;
  icon: ReactNode;
  label: string;
  count?: number;
} & Omit<ButtonHTMLAttributes<HTMLButtonElement>, 'children'>) {
  return (
    <button
      className={`category ${selected ? 'selected' : ''}`}
      style={depth ? { paddingLeft: 14 + depth * 16 } : undefined}
      // The name when the sidebar is a strip of icons (2.43), where the label is not shown.
      title={label}
      {...rest}
    >
      {icon}
      <span>{label}</span>
      {count !== undefined && <small>{count}</small>}
    </button>
  );
}

function folderDepth(workspace: Workspace, parent: string | null) {
  let depth = 0;
  while (parent && depth < 16) {
    depth++;
    parent = workspace.folders.find((x) => x.id === parent)?.parent ?? null;
  }
  return depth;
}

export function Sidebar({
  state,
  host,
  categories,
  librarySize,
  workspace: w,
  selection,
  selectHost,
  selectMine,
  update,
  manage,
  hostNote = 'Keep this tab open to host.',
  nav,
  library = true,
  rail,
  inert,
  canPlan = false,
}: {
  state: Snapshot;
  host: boolean;
  categories: { category: Category; depth: number }[];
  librarySize: number;
  workspace: Workspace | null | undefined;
  selection: BoardSelection;
  selectHost: (category: string) => void;
  selectMine: (folder: string) => void;
  update: Update;
  manage: () => void;
  /** What keeps the session running (2.36): this tab, or the companion window. */
  hostNote?: string;
  /** The section list, first, in sidebar navigation style (2.43). */
  nav?: ReactNode;
  /** The Soundboard's library and workspace lists; Voices and Sentences show none (2.43). */
  library?: boolean;
  /** Top tabs style: the sidebar can fold to a strip of icons. */
  rail?: { collapsed: boolean; toggle: () => void };
  /** Out of reach while the editor sheet covers the board (narrow screens). */
  inert?: boolean;
  /** Organize mode (2.44): Soundpad categories take dropped sounds into Review changes when planning is possible. */
  canPlan?: boolean;
}) {
  const { category, myCategory, search } = selection;
  const mine = w?.layout === 'mine';
  const { on: organizing } = useOrganize();
  /** The sounds a drop carries: the dragged selection (Organize mode), else the one dragged sound. */
  const dropped = (e: DragEvent) => {
    const ids = dragIds(e.dataTransfer.getData(DRAG_TYPE));
    const one = e.dataTransfer.getData('application/x-soundpad-id');
    return (ids.length ? ids : [one]).filter((id) => state.library.sounds.some((s) => s.id === id));
  };
  const takesDrops = (c: Category) => organizing && canPlan && !isAppCategory(c.name);
  return (
    <aside className="sidebar" inert={inert}>
      {rail && (
        <button
          type="button"
          className="icon-button rail-toggle"
          aria-expanded={!rail.collapsed}
          aria-label={rail.collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
          title={rail.collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
          onClick={rail.toggle}
        >
          {rail.collapsed ? <PanelLeftOpen size={18} /> : <PanelLeftClose size={18} />}
        </button>
      )}
      {nav}
      {(state.protocol ?? 1) >= 2 && (
        <SpotifyPanel host={host} guestAllowed={state.permissions.guestSpotify !== false} />
      )}
      {library && (
        <>
          <div className="sidebar-heading">
            LIBRARY <span>{librarySize}</span>
          </div>
          <NavButton
            selected={category === 'all' && !mine}
            icon={<AudioLines size={18} />}
            label="All sounds"
            count={librarySize}
            onClick={() => selectHost('all')}
          />
          <div className="sidebar-label">SOUNDPAD CATEGORIES</div>
          <nav aria-label="Sound categories">
            {categories.map(({ category: c, depth }) => (
              <NavButton
                key={c.id}
                depth={depth}
                selected={category === c.id && !search && !mine}
                icon={c.icon ? <img src={c.icon} alt="" /> : <Folder size={17} />}
                label={c.name || 'Unnamed category'}
                count={c.soundIds.length}
                onClick={() => selectHost(c.id)}
                data-drop-target={takesDrops(c) || undefined}
                onDragOver={(e) => {
                  if (takesDrops(c)) e.preventDefault();
                }}
                onDrop={(e) => {
                  if (!takesDrops(c)) return;
                  e.preventDefault();
                  planSteps(
                    host,
                    stepsForAdd(
                      dropped(e),
                      { kind: 'category', category: { id: c.id, name: c.name } },
                      state.library.categories,
                    ),
                    state.library.sounds,
                    state.library.categories,
                  );
                }}
              />
            ))}
          </nav>
          {categories.length === 0 && (
            <p className="sidebar-empty">Your Soundpad categories will appear here.</p>
          )}
          {w && (
            <>
              <div className="sidebar-label">MY WORKSPACE</div>
              <NavButton
                selected={mine && myCategory === 'all'}
                icon={<Folder size={17} />}
                label="My sounds"
                onClick={() => selectMine('all')}
              />
              <NavButton
                selected={mine && myCategory === 'favorites'}
                icon={<Music2 size={17} />}
                label="Favorites"
                count={w.favorites.length}
                onClick={() => selectMine('favorites')}
              />
              {w.folders.map((f) => (
                <NavButton
                  key={f.id}
                  depth={folderDepth(w, f.parent)}
                  selected={mine && myCategory === f.id}
                  icon={<Folder size={17} />}
                  label={f.name}
                  count={f.soundIds.length}
                  onDragOver={(e) => e.preventDefault()}
                  onDrop={(e) => {
                    e.preventDefault();
                    const ids = dropped(e);
                    if (!ids.length) return;
                    // Organize mode (2.44): the whole selection, with Undo.
                    if (organizing) {
                      rememberLayout(w);
                      update((current) => addToFolder(current, f.id, ids));
                    } else update((current) => moveSound(current, ids[0], f.id));
                  }}
                  onClick={() => selectMine(f.id)}
                />
              ))}
              <button className="category" onClick={manage}>
                Manage personal layout
              </button>
            </>
          )}
        </>
      )}
      <div className="sidebar-bottom">
        <span>
          <ShieldCheck size={15} /> Private session
        </span>
        <p>{host ? hostNote : 'Connected directly to your host.'}</p>
      </div>
    </aside>
  );
}
