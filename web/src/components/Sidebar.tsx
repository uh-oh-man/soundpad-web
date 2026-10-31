import type { ButtonHTMLAttributes, ReactNode } from 'react';
import { AudioLines, Folder, Music2, ShieldCheck } from 'lucide-react';
import type { Category, Snapshot } from '../protocol';
import { moveSound, type Workspace } from '../workspace';
import type { BoardSelection } from '../arrange';
import { SpotifyPanel } from './SpotifyPanel';

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
}) {
  const { category, myCategory, search } = selection;
  const mine = w?.layout === 'mine';
  return (
    <aside className="sidebar">
      {(state.protocol ?? 1) >= 2 && <SpotifyPanel host={host} />}
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
                const id = e.dataTransfer.getData('application/x-soundpad-id');
                if (state.library.sounds.some((s) => s.id === id))
                  update((current) => moveSound(current, id, f.id));
              }}
              onClick={() => selectMine(f.id)}
            />
          ))}
          <button className="category" onClick={manage}>
            Manage personal layout
          </button>
        </>
      )}
      <div className="sidebar-bottom">
        <span>
          <ShieldCheck size={15} /> Private session
        </span>
        <p>{host ? 'Keep this tab open to host.' : 'Connected directly to your host.'}</p>
      </div>
    </aside>
  );
}
