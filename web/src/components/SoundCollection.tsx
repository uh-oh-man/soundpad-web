import { useEffect, useMemo, useState } from 'react';
import {
  ImagePlus,
  Star,
  FolderPlus,
  BarChart3,
  ArrowUp,
  ArrowDown,
  Rewind,
  SquarePen,
} from 'lucide-react';
import { flattenCategories, soundKeys, hasSound, type Snapshot, type Sound } from '../protocol';
import { moveSound, toggleFavorite, type Workspace } from '../workspace';
import { Waveform, formatTime, usePlayhead } from './Waveform';
import { SoundButton } from './SoundButton';
import { guestHeld, HELD_BY_HOST, intentKey, playback, usePendingIntent } from '../playback';
import type { Totals } from '../arrange';
import { SpeedMenu, canQuickSpeed } from './SpeedMenu';
import { requestEditor } from '../shell';
import { supports } from '../version';
import { DRAG_TYPE, dragIds, dragged, needsAttention } from '../organize';
import { selectRange, toggleSound, useOrganize } from './organize/organize-store';
import { NeedsAttention } from './organize/NeedsAttention';
type Props = {
  sounds: Sound[];
  state: Snapshot;
  host: boolean;
  canPlay: boolean;
  workspace: Workspace | null;
  update: (fn: (w: Workspace) => Workspace) => void;
  edit: (s: Sound) => void;
  organize: (s: Sound) => void;
  stats: (s: Sound) => void;
  folderId?: string;
  totals: Totals;
};
function ActiveProgress({ playback }: { playback: Snapshot['playback'] }) {
  const position = usePlayhead(playback);
  return <progress aria-label="Sound progress" max={playback.durationMs ?? 1} value={position} />;
}
export function SoundCollection({
  sounds,
  state,
  host,
  canPlay,
  workspace: w,
  update,
  edit,
  organize,
  stats,
  folderId,
  totals,
}: Props) {
  const [limit, setLimit] = useState(120);
  // The click this page still waits on (2.35). A newer click anywhere takes the marker with it, so
  // a tile never stays "preparing" for a start that lost.
  const pending = usePendingIntent();
  const held = guestHeld(state, host);
  // Organize mode (2.44): tiles select instead of playing, and drag (the selection) onto a category or folder.
  const { on: organizing, selected } = useOrganize();
  const mode = w?.view ?? 'icons';
  const personal = w?.layout === 'mine';
  // Sounds whose file is missing go to their own group at the end (gate missingFiles).
  const { ok: shown, missing } = useMemo(
    () =>
      supports(state.companionVersion, 'missingFiles')
        ? needsAttention(sounds)
        : { ok: sounds, missing: [] },
    [sounds, state.companionVersion],
  );
  const order = useMemo(() => shown.map((s) => s.id), [shown]);
  useEffect(() => setLimit(mode === 'waveforms' ? 24 : 120), [mode, sounds.length, folderId]);
  async function play(sound: Sound) {
    if (organizing) return;
    await playback.play(sound.id);
  }
  function pick(id: string, range: boolean) {
    if (range) selectRange(order, id);
    else toggleSound(id);
  }
  function drop(id: string, before?: string) {
    if (!w || !state.library.sounds.some((s) => s.id === id)) return;
    update((current) =>
      moveSound(
        {
          ...current,
          order: current.order.length ? current.order : state.library.sounds.map((s) => s.id),
        },
        id,
        folderId,
        before,
      ),
    );
  }
  function nudge(id: string, delta: number) {
    const ids = sounds.map((s) => s.id),
      index = ids.indexOf(id),
      target = index + delta;
    if (target < 0 || target >= ids.length) return;
    [ids[index], ids[target]] = [ids[target], ids[index]];
    update((current) =>
      folderId
        ? {
            ...current,
            folders: current.folders.map((f) => (f.id === folderId ? { ...f, soundIds: ids } : f)),
          }
        : { ...current, order: ids },
    );
  }
  const membership = useMemo(() => {
    const result = new Map<string, string[]>();
    for (const { category } of flattenCategories(state.library.categories)) {
      for (const id of category.soundIds) {
        const names = result.get(id) ?? [];
        if (names.length < 3) names.push(category.name);
        result.set(id, names);
      }
    }
    return result;
  }, [state.library.categories]);
  return (
    <>
      <div
        className={`sound-collection view-${mode} ${mode === 'icons' || mode === 'large' ? 'sound-grid' : ''}`}
        role={mode === 'details' ? 'table' : undefined}
        aria-label={mode === 'details' ? 'Sound details' : undefined}
      >
        {mode === 'details' && (
          <div className="details-header" role="row">
            {[
              ['name', 'Sound'],
              ['duration', 'Duration'],
              ['native', 'Soundpad plays'],
              ['web', 'Web plays'],
              ['last', 'Last web played'],
            ].map(([sort, label]) => (
              <div
                role="columnheader"
                key={sort}
                aria-sort={w?.sort === sort ? (w.descending ? 'descending' : 'ascending') : 'none'}
              >
                <button
                  onClick={() =>
                    update((current) => ({
                      ...current,
                      sort: sort as Workspace['sort'],
                      descending: current.sort === sort ? !current.descending : false,
                    }))
                  }
                >
                  {label}
                  {w?.sort === sort ? (w.descending ? ' ↓' : ' ↑') : ''}
                </button>
              </div>
            ))}
            <span role="columnheader">Actions</span>
          </div>
        )}
        {shown.slice(0, limit).map((sound, i) => {
          const active =
            state.playback.soundId === sound.id &&
            state.playback.identitySource === 'web' &&
            ['PLAYING', 'PAUSED', 'SEEKING'].includes(state.playback.status);
          // Asked for but not yet answered: shown apart from "playing", which only a report sets.
          const starting = pending === intentKey.play(sound.id);
          const reversing = pending === intentKey.reverse(sound.id);
          // A details row is a table row, which an <article> may not be.
          const Entry = mode === 'details' ? 'div' : 'article';
          return (
            <Entry
              key={sound.id}
              role={mode === 'details' ? 'row' : undefined}
              className={`sound-tile sound-entry ${active ? 'now-playing' : ''} ${starting || reversing ? 'pressed intent-pending' : ''} ${organizing && selected.has(sound.id) ? 'is-selected' : ''}`}
              data-intent={starting || reversing ? 'pending' : undefined}
              // Dragging is part of Organize mode (2.44), so a drag never ends in a play.
              draggable={organizing}
              onDragStart={(e) => {
                if (!organizing) return;
                const ids = dragged(selected, sound.id, order);
                e.dataTransfer.setData('application/x-soundpad-id', sound.id);
                e.dataTransfer.setData(DRAG_TYPE, JSON.stringify(ids));
                e.dataTransfer.effectAllowed = 'copyMove';
              }}
              onDragOver={(e) => {
                if (personal && organizing) e.preventDefault();
              }}
              onDrop={(e) => {
                if (!personal || !organizing) return;
                e.preventDefault();
                const ids = dragIds(e.dataTransfer.getData(DRAG_TYPE));
                for (const id of ids.length
                  ? ids
                  : [e.dataTransfer.getData('application/x-soundpad-id')])
                  if (id !== sound.id) drop(id, sound.id);
              }}
            >
              {organizing && (
                // The whole tile is the checkbox in Organize mode: click, Space or Enter toggles, Shift-click a range.
                <label
                  className="tile-select"
                  role={mode === 'details' ? 'cell' : undefined}
                  onClick={(e) => {
                    if (e.target !== e.currentTarget) return;
                    e.preventDefault();
                    pick(sound.id, e.shiftKey);
                  }}
                >
                  <input
                    type="checkbox"
                    aria-label={`Select ${sound.name}`}
                    checked={selected.has(sound.id)}
                    onChange={() => {}}
                    onClick={(e) => {
                      e.stopPropagation();
                      pick(sound.id, e.shiftKey);
                    }}
                    onKeyDown={(e) => {
                      if (e.key !== 'Enter') return;
                      e.preventDefault();
                      pick(sound.id, e.shiftKey);
                    }}
                  />
                </label>
              )}
              <div
                className="sound-play-cell"
                role={mode === 'details' ? 'cell' : undefined}
                inert={organizing || undefined}
              >
                <SoundButton
                  sound={sound}
                  state={state}
                  canPlay={canPlay}
                  canSeek={host || state.permissions.guestSeeking !== false}
                  index={i}
                  pending={starting}
                  held={held}
                  active={active}
                  play={() => play(sound)}
                  choose={(at) => playback.position(sound.id, at)}
                />
                {active && mode === 'details' && <ActiveProgress playback={state.playback} />}
              </div>
              {mode === 'details' && (
                <>
                  <span role="cell" className="detail-number">
                    {formatTime(sound.durationMs)}
                  </span>
                  <span
                    role="cell"
                    className="detail-number"
                    title={
                      sound.nativeLastPlayed
                        ? `Native last played (date): ${sound.nativeLastPlayed}`
                        : 'Native count unavailable'
                    }
                  >
                    {sound.nativePlays ?? '—'}
                  </span>
                  <span role="cell" className="detail-number">
                    {totals[sound.id]?.total ?? 0}
                  </span>
                  <span role="cell" className="detail-date">
                    {totals[sound.id]?.lastPlayedAt
                      ? new Date(totals[sound.id].lastPlayedAt!).toLocaleString()
                      : '—'}
                  </span>
                </>
              )}
              <div
                className="sound-actions"
                role={mode === 'details' ? 'cell' : undefined}
                inert={organizing || undefined}
              >
                {/*
                 * Quick actions (Stage G spec 5.3): over the artwork's top right corner on the grid views,
                 * shown on hover or focus (opacity only, so they stay focusable); in the row elsewhere.
                 */}
                <div className="tile-quick">
                  <button
                    className="icon-button"
                    aria-label={`Reverse and play ${sound.name}`}
                    title={held ? HELD_BY_HOST : 'Reverse & play · temporary copy'}
                    disabled={!canPlay || (!host && state.permissions.guestTransforms === false)}
                    aria-disabled={reversing || held || undefined}
                    onClick={() => {
                      if (!reversing && !held) void playback.reverse(sound.id);
                    }}
                  >
                    <Rewind size={14} aria-hidden="true" />
                  </button>
                  {canQuickSpeed(sound, state, host) && (
                    <SpeedMenu
                      sound={sound}
                      state={state}
                      host={host}
                      disabled={!canPlay}
                      held={held}
                    />
                  )}
                </div>
                {sound.temporary && (
                  <small className="temp-badge" title={`Added by ${sound.uploadedBy ?? 'Guest'}`}>
                    temp
                  </small>
                )}
                {w && (
                  <>
                    <button
                      className="icon-button"
                      aria-label={`Favorite ${sound.name}`}
                      aria-pressed={hasSound(w.favorites, sound)}
                      onClick={() =>
                        update((current) =>
                          toggleFavorite(current, sound.workspaceId ?? sound.id, soundKeys(sound)),
                        )
                      }
                    >
                      <Star
                        size={14}
                        fill={hasSound(w.favorites, sound) ? 'currentColor' : 'none'}
                      />
                    </button>
                    <button
                      className="icon-button"
                      aria-label={`Organize ${sound.name}`}
                      onClick={() => organize(sound)}
                    >
                      <FolderPlus size={14} />
                    </button>
                  </>
                )}
                {(state.protocol ?? 1) >= 2 && (
                  <button
                    className="icon-button"
                    aria-label={`Statistics for ${sound.name}`}
                    onClick={() => stats(sound)}
                  >
                    <BarChart3 size={14} />
                  </button>
                )}
                <button
                  className="icon-button"
                  aria-label={`Open editor for ${sound.name}`}
                  title="Cues, regions, effects, icon and statistics"
                  onClick={() => requestEditor({ kind: 'sound-edit', soundId: sound.id })}
                >
                  <SquarePen size={14} aria-hidden="true" />
                </button>
                {host && (
                  <button
                    className="icon-button"
                    aria-label={`Edit icon for ${sound.name}`}
                    onClick={() => edit(sound)}
                  >
                    <ImagePlus size={14} />
                  </button>
                )}
                {personal && w?.sort === 'original' && (
                  <>
                    <button
                      className="icon-button"
                      aria-label={`Move ${sound.name} up`}
                      disabled={i === 0}
                      onClick={() => nudge(sound.id, -1)}
                    >
                      <ArrowUp size={13} />
                    </button>
                    <button
                      className="icon-button"
                      aria-label={`Move ${sound.name} down`}
                      disabled={i === shown.length - 1}
                      onClick={() => nudge(sound.id, 1)}
                    >
                      <ArrowDown size={13} />
                    </button>
                  </>
                )}
              </div>
              {active && mode !== 'waveforms' && mode !== 'details' && (
                <ActiveProgress playback={state.playback} />
              )}
              {mode === 'waveforms' && (
                <Waveform
                  sound={sound}
                  state={state}
                  disabled={!canPlay || (state.permissions.guestSeeking === false && !host)}
                  held={held}
                  workspace={w}
                  update={update}
                  choose={(at) => playback.position(sound.id, at)}
                />
              )}
              {mode === 'details' && (
                <span className="category-note">{membership.get(sound.id)?.join(' · ')}</span>
              )}
            </Entry>
          );
        })}
      </div>
      {shown.length > limit && (
        <button
          className="load-more"
          onClick={() => setLimit((n) => n + (mode === 'waveforms' ? 24 : 120))}
        >
          Show more sounds ({shown.length - limit} remaining)
        </button>
      )}
      <NeedsAttention sounds={missing} host={host} />
    </>
  );
}
