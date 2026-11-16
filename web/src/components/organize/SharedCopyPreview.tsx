import { useState } from 'react';
import { copyAll, copyShared, type CopyChoice } from '../../organize';
import type { SharedLayout } from '../../shared-layout';
import type { Workspace } from '../../workspace';
import { arrangeSounds } from '../../arrange';
import { soundKeys } from '../../protocol';
import { useStageH } from '../../stage-h';
import '../../diagnostics.css';

/** Sound tiles the miniature shows before "and N more". */
export const MINIATURE_TILES = 12;
const MINIATURE_HIDDEN = 4;

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
      <LayoutMiniature next={next} current={workspace} />
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

/**
 * The board this copy makes, in miniature (2.45, D12): My Layout's folders as a tree (new ones marked), then the
 * first sounds of All sounds in the order they will have, favourites starred, and the sounds it hides struck
 * through. It is drawn from the same arrangement the board uses, and changes as boxes are ticked.
 */
function LayoutMiniature({ next, current }: { next: Workspace; current: Workspace }) {
  const { state } = useStageH();
  const mine = { ...next, layout: 'mine' as const };
  const shown = arrangeSounds(state, mine, { category: 'all', myCategory: 'all', search: '' }, {});
  const favorite = new Set(next.favorites);
  const hiddenIds = new Set(next.hidden);
  const hidden = state.library.sounds.filter(
    (s) => !s.internalVariant && soundKeys(s).some((k) => hiddenIds.has(k)),
  );
  const old = new Map(current.folders.map((f) => [f.id, f.soundIds.length]));
  const depth = (id: string | null) => {
    let d = 0;
    for (let parent = id; parent && d < 16; d++)
      parent = next.folders.find((f) => f.id === parent)?.parent ?? null;
    return d;
  };
  const tiles = shown.slice(0, MINIATURE_TILES);
  return (
    <section className="copy-miniature" aria-label="My Layout after copying">
      <h4>My Layout after copying</h4>
      {next.folders.length > 0 && (
        <ul className="copy-miniature-folders" aria-label="Folders">
          {next.folders.map((f) => {
            const before = old.get(f.id);
            const note =
              before === undefined ? 'new' : before !== f.soundIds.length ? `was ${before}` : '';
            return (
              <li
                key={f.id}
                className={before === undefined ? 'is-new' : undefined}
                style={{ paddingInlineStart: `${depth(f.parent) * 14}px` }}
              >
                <span>{f.name}</span>
                <small>
                  {f.soundIds.length}
                  {note && ` · ${note}`}
                </small>
              </li>
            );
          })}
        </ul>
      )}
      <ul className="copy-miniature-tiles" aria-label="All sounds, in order">
        {tiles.map((s) => {
          const star = soundKeys(s).some((k) => favorite.has(k));
          return (
            <li
              key={s.id}
              className={star ? 'is-favorite' : undefined}
              aria-label={star ? `${s.name}, favorite` : s.name}
            >
              <span>{s.name}</span>
            </li>
          );
        })}
        {hidden.slice(0, MINIATURE_HIDDEN).map((s) => (
          <li key={`hidden-${s.id}`} className="is-hidden" aria-label={`${s.name}, hidden`}>
            <span>{s.name}</span>
          </li>
        ))}
      </ul>
      {(shown.length > tiles.length || hidden.length > MINIATURE_HIDDEN) && (
        <p className="muted">
          {shown.length > tiles.length && `and ${shown.length - tiles.length} more`}
          {shown.length > tiles.length && hidden.length > MINIATURE_HIDDEN && ', '}
          {hidden.length > MINIATURE_HIDDEN && `${hidden.length - MINIATURE_HIDDEN} more hidden`}
        </p>
      )}
    </section>
  );
}
