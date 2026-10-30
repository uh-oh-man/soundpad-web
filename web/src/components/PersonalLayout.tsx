import { useRef, useState } from 'react';
import { ArrowUp, ArrowDown, FolderPlus, Download, Upload, Trash2, Star } from 'lucide-react';
import { blankWorkspace, parseWorkspace, toggleFavorite, type Workspace } from '../workspace';
import type { Sound } from '../protocol';
type Props = {
  workspace: Workspace;
  update: (fn: (w: Workspace) => Workspace) => void;
  replace: (w: Workspace) => void;
  sounds: Sound[];
  status: string;
};
export function PersonalLayout({ workspace: w, update, replace, sounds, status }: Props) {
  const [name, setName] = useState(''),
    [parent, setParent] = useState(''),
    [error, setError] = useState('');
  const input = useRef<HTMLInputElement>(null);
  const known = new Set(sounds.map((s) => s.id));
  const missing = new Set(
    [...w.favorites, ...w.hidden, ...w.folders.flatMap((f) => f.soundIds)].filter(
      (id) => !known.has(id),
    ),
  );
  function exportLayout() {
    const url = URL.createObjectURL(
      new Blob([JSON.stringify(w, null, 2)], { type: 'application/json' }),
    );
    const link = document.createElement('a');
    link.href = url;
    link.download = 'soundpad-personal-layout.json';
    link.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }
  async function importLayout(file: File) {
    setError('');
    try {
      if (file.size > 2 * 1024 * 1024) throw Error('Import must be under 2 MiB.');
      replace(parseWorkspace(await file.text(), w.hostId));
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Invalid layout.');
    }
  }
  function reorder(id: string, delta: number) {
    update((current) => {
      const folders = [...current.folders],
        index = folders.findIndex((f) => f.id === id),
        target = folders.findIndex(
          (f, i) =>
            f.parent === folders[index].parent &&
            (delta < 0
              ? i < index && !folders.slice(i + 1, index).some((x) => x.parent === f.parent)
              : i > index),
        );
      if (target < 0 || target >= folders.length) return current;
      [folders[index], folders[target]] = [folders[target], folders[index]];
      return { ...current, folders };
    });
  }
  return (
    <div className="layout-manager">
      <p className="muted">{status}. Host categories and audio files are never changed.</p>
      <div className="folder-create">
        <input
          aria-label="New personal category name"
          maxLength={80}
          value={name}
          onChange={(e) => setName(e.target.value)}
          placeholder="New category"
        />
        <select
          aria-label="Parent category"
          value={parent}
          onChange={(e) => setParent(e.target.value)}
        >
          <option value="">Top level</option>
          {w.folders.map((f) => (
            <option key={f.id} value={f.id}>
              {f.name}
            </option>
          ))}
        </select>
        <button
          disabled={!name.trim() || w.folders.length >= 128}
          onClick={() => {
            update((current) => ({
              ...current,
              folders: [
                ...current.folders,
                {
                  id: crypto.randomUUID(),
                  name: name.trim(),
                  parent: parent || null,
                  soundIds: [],
                },
              ],
            }));
            setName('');
          }}
        >
          <FolderPlus size={15} /> Create category
        </button>
      </div>
      {w.folders.map((f, index) => (
        <div className="folder-edit" key={f.id}>
          <input
            aria-label={`Rename category ${f.name}`}
            defaultValue={f.name}
            maxLength={80}
            onBlur={(e) => {
              const name = e.target.value.trim();
              if (name)
                update((current) => ({
                  ...current,
                  folders: current.folders.map((x) => (x.id === f.id ? { ...x, name } : x)),
                }));
            }}
          />
          <small>{f.soundIds.length} sounds</small>
          <select
            aria-label={`Parent of ${f.name}`}
            value={f.parent ?? ''}
            onChange={(e) => {
              const parent = e.target.value || null;
              update((current) => ({
                ...current,
                folders: current.folders.map((x) => (x.id === f.id ? { ...x, parent } : x)),
              }));
            }}
          >
            <option value="">Top level</option>
            {w.folders
              .filter((candidate) => {
                let current: typeof candidate | undefined = candidate;
                while (current) {
                  if (current.id === f.id) return false;
                  current = w.folders.find((x) => x.id === current!.parent);
                }
                return true;
              })
              .map((candidate) => (
                <option key={candidate.id} value={candidate.id}>
                  {candidate.name}
                </option>
              ))}
          </select>
          <button
            aria-label={`Move category ${f.name} up`}
            disabled={index === 0}
            onClick={() => reorder(f.id, -1)}
          >
            <ArrowUp size={14} />
          </button>
          <button
            aria-label={`Move category ${f.name} down`}
            disabled={index === w.folders.length - 1}
            onClick={() => reorder(f.id, 1)}
          >
            <ArrowDown size={14} />
          </button>
          <button
            aria-label={`Delete category ${f.name}`}
            onClick={() => {
              if (
                confirm(
                  `Delete personal category “${f.name}”? Sounds stay in Soundpad. Nested categories move to the top level.`,
                )
              )
                update((current) => ({
                  ...current,
                  folders: current.folders
                    .filter((x) => x.id !== f.id)
                    .map((x) => (x.parent === f.id ? { ...x, parent: null } : x)),
                }));
            }}
          >
            <Trash2 size={14} />
          </button>
        </div>
      ))}
      {missing.size > 0 && (
        <p className="warning">
          {missing.size} referenced sounds are unavailable. Their shortcuts are retained so they can
          return when the host restores the same files.
        </p>
      )}
      {w.hidden.length > 0 && (
        <button onClick={() => update((current) => ({ ...current, hidden: [] }))}>
          Unhide {w.hidden.length} sounds
        </button>
      )}
      <div className="editor-actions">
        <button onClick={exportLayout}>
          <Download size={15} /> Export layout
        </button>
        <button onClick={() => input.current?.click()}>
          <Upload size={15} /> Import layout
        </button>
        <input
          ref={input}
          type="file"
          accept="application/json,.json"
          className="sr-only"
          aria-label="Import personal layout"
          onChange={(e) => {
            if (e.target.files?.[0]) void importLayout(e.target.files[0]);
          }}
        />
        <button
          className="danger-link"
          onClick={() => {
            if (confirm('Reset your personal layout, favorites, cues and regions for this host?'))
              replace(blankWorkspace(w.hostId));
          }}
        >
          Reset personal layout
        </button>
      </div>
      {error && (
        <p role="alert" className="warning">
          {error}
        </p>
      )}
    </div>
  );
}
export function OrganizeSound({
  sound,
  workspace: w,
  update,
}: {
  sound: Sound;
  workspace: Workspace;
  update: Props['update'];
}) {
  return (
    <div className="organize-sound">
      <p>{sound.name}</p>
      <button onClick={() => update((current) => toggleFavorite(current, sound.id))}>
        <Star size={15} fill={w.favorites.includes(sound.id) ? 'currentColor' : 'none'} />
        {w.favorites.includes(sound.id) ? 'Remove favorite' : 'Add favorite'}
      </button>
      <h3>Personal categories</h3>
      {w.folders.length ? (
        w.folders.map((f) => (
          <label className="folder-membership" key={f.id}>
            <input
              type="checkbox"
              checked={f.soundIds.includes(sound.id)}
              onChange={(e) => {
                const checked = e.target.checked;
                update((current) => ({
                  ...current,
                  folders: current.folders.map((x) =>
                    x.id === f.id
                      ? {
                          ...x,
                          soundIds: checked
                            ? [...new Set([...x.soundIds, sound.id])]
                            : x.soundIds.filter((id) => id !== sound.id),
                        }
                      : x,
                  ),
                }));
              }}
            />
            {f.name}
          </label>
        ))
      ) : (
        <p className="muted">Create a category in My Layout first.</p>
      )}
      <button
        onClick={() =>
          update((current) => ({
            ...current,
            hidden: current.hidden.includes(sound.id)
              ? current.hidden.filter((id) => id !== sound.id)
              : [...current.hidden, sound.id],
          }))
        }
      >
        {w.hidden.includes(sound.id) ? 'Unhide in My Layout' : 'Hide in My Layout'}
      </button>
      <p className="muted">
        A sound can be in several personal categories. This never edits Soundpad.
      </p>
    </div>
  );
}
