import { useState } from 'react';
import { copyAll, copyShared, type CopyChoice } from '../../organize';
import type { SharedLayout } from '../../shared-layout';
import type { Workspace } from '../../workspace';

/**
 * Copy into My Layout (2.44): a preview of a guest's shared layout, where the person chooses which folders,
 * and whether favourites, hidden sounds and the order come along, and whether to merge them with their own
 * layout (the default) or replace those parts. The caller keeps the previous layout for Undo.
 */
export function SharedCopyPreview({
  layout,
  workspace,
  onCopy,
  onClose,
}: {
  layout: SharedLayout;
  workspace: Workspace;
  onCopy: (next: Workspace) => void;
  onClose: () => void;
}) {
  const shared = layout.organization;
  const [choice, setChoice] = useState<CopyChoice>(() => copyAll(shared));
  const next = copyShared(workspace, shared, choice);
  const depth = (id: string | null) => {
    let d = 0;
    for (let parent = id; parent && d < 16; d++)
      parent = shared.folders.find((f) => f.id === parent)?.parent ?? null;
    return d;
  };
  const toggle = (key: 'favorites' | 'hidden' | 'order') =>
    setChoice((c) => ({ ...c, [key]: !c[key] }));
  return (
    <div className="shared-copy">
      <p>
        Choose what to copy from {layout.name}&rsquo;s layout into yours. Nothing of theirs changes.
      </p>
      <fieldset>
        <legend>Folders</legend>
        {shared.folders.length ? (
          shared.folders.map((f) => (
            <label
              key={f.id}
              className="checkbox-row"
              style={{ paddingInlineStart: `${depth(f.parent) * 16}px` }}
            >
              <input
                type="checkbox"
                checked={choice.folders.includes(f.id)}
                onChange={(e) =>
                  setChoice((c) => ({
                    ...c,
                    folders: e.target.checked
                      ? [...c.folders, f.id]
                      : c.folders.filter((x) => x !== f.id),
                  }))
                }
              />
              {f.name} <small>({f.soundIds.length})</small>
            </label>
          ))
        ) : (
          <p className="muted">No folders.</p>
        )}
      </fieldset>
      <fieldset>
        <legend>Also</legend>
        <label className="checkbox-row">
          <input type="checkbox" checked={choice.favorites} onChange={() => toggle('favorites')} />{' '}
          Favorites ({shared.favorites.length})
        </label>
        <label className="checkbox-row">
          <input type="checkbox" checked={choice.hidden} onChange={() => toggle('hidden')} /> Hidden
          sounds ({shared.hidden.length})
        </label>
        <label className="checkbox-row">
          <input type="checkbox" checked={choice.order} onChange={() => toggle('order')} /> Sound
          order
        </label>
      </fieldset>
      <fieldset>
        <legend>How</legend>
        <label className="checkbox-row">
          <input
            type="radio"
            name="copy-mode"
            checked={choice.mode === 'merge'}
            onChange={() => setChoice((c) => ({ ...c, mode: 'merge' }))}
          />{' '}
          Merge with my layout
        </label>
        <label className="checkbox-row">
          <input
            type="radio"
            name="copy-mode"
            checked={choice.mode === 'replace'}
            onChange={() => setChoice((c) => ({ ...c, mode: 'replace' }))}
          />{' '}
          Replace those parts of my layout
        </label>
      </fieldset>
      <p className="muted" role="status">
        After copying: {next.folders.length} folder{next.folders.length === 1 ? '' : 's'},{' '}
        {next.favorites.length} favorite{next.favorites.length === 1 ? '' : 's'},{' '}
        {next.hidden.length} hidden.
      </p>
      <div className="dialog-actions">
        <button type="button" onClick={onClose}>
          Cancel
        </button>
        <button type="button" className="primary" onClick={() => onCopy(next)}>
          Copy
        </button>
      </div>
    </div>
  );
}
