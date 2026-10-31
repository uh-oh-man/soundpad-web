import { useEffect, useMemo, useRef, useState } from 'react';
import { ImagePlus, Star, FolderPlus, BarChart3, ArrowUp, ArrowDown, Rewind } from 'lucide-react';
import { controller } from '../controller';
import { flattenCategories, soundKeys, hasSound, type Snapshot, type Sound } from '../protocol';
import { moveSound, toggleFavorite, type Workspace } from '../workspace';
import { Waveform, formatTime, usePlayhead } from './Waveform';
import { SoundButton } from './SoundButton';
import { seekQueue } from '../seek';
import type { Totals } from '../arrange';
const resumes = new Map<string, Promise<unknown>>();
async function resumeAfterSeek(soundId: string, epoch: string) {
  const current = controller.getSnapshot().state.playback;
  if (current.soundId !== soundId || current.epoch !== epoch || current.status !== 'PAUSED') return;
  const key = epoch + ':' + (current.positionRevision ?? 0);
  let task = resumes.get(key);
  if (!task) {
    task = controller.run('pause', { soundId, epoch });
    resumes.set(key, task);
    void task.catch(() => resumes.delete(key));
    if (resumes.size > 128) resumes.delete(resumes.keys().next().value!);
  }
  await task;
}
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
export async function choosePosition(sound: Sound, state: Snapshot, at: number) {
  if (
    state.playback.soundId === sound.id &&
    state.playback.epoch &&
    state.playback.status !== 'STOPPED'
  ) {
    await seekQueue.seek(sound.id, state.playback.epoch, at);
    if (state.playback.status === 'PAUSED') await resumeAfterSeek(sound.id, state.playback.epoch);
  } else
    await controller.run('startAt', {
      soundId: sound.id,
      libraryVersion: state.library.version,
      positionMs: at,
    });
}
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
  const [pending, setPending] = useState('');
  const dragging = useRef(false);
  const mode = w?.view ?? 'icons';
  const personal = w?.layout === 'mine';
  useEffect(() => setLimit(mode === 'waveforms' ? 24 : 120), [mode, sounds.length, folderId]);
  async function play(sound: Sound) {
    if (dragging.current) return;
    setPending(sound.id);
    try {
      await controller.run('play', { soundId: sound.id, libraryVersion: state.library.version });
    } catch {
      /* Controller reports failure. */
    } finally {
      setPending('');
    }
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
        {sounds.slice(0, limit).map((sound, i) => {
          const active =
            state.playback.soundId === sound.id &&
            state.playback.identitySource === 'web' &&
            ['PLAYING', 'PAUSED', 'SEEKING'].includes(state.playback.status);
          // A details row is a table row, which an <article> may not be.
          const Entry = mode === 'details' ? 'div' : 'article';
          return (
            <Entry
              key={sound.id}
              role={mode === 'details' ? 'row' : undefined}
              className={`sound-tile sound-entry ${active ? 'now-playing' : ''} ${pending === sound.id ? 'pressed' : ''}`}
              draggable={personal}
              onDragStart={(e) => {
                dragging.current = true;
                e.dataTransfer.setData('application/x-soundpad-id', sound.id);
                e.dataTransfer.effectAllowed = 'copyMove';
              }}
              onDragEnd={() =>
                setTimeout(() => {
                  dragging.current = false;
                }, 100)
              }
              onDragOver={(e) => {
                if (personal) e.preventDefault();
              }}
              onDrop={(e) => {
                if (!personal) return;
                e.preventDefault();
                drop(e.dataTransfer.getData('application/x-soundpad-id'), sound.id);
              }}
            >
              <div className="sound-play-cell" role={mode === 'details' ? 'cell' : undefined}>
                <SoundButton
                  sound={sound}
                  state={state}
                  canPlay={canPlay}
                  canSeek={host || state.permissions.guestSeeking !== false}
                  index={i}
                  pending={pending === sound.id}
                  active={active}
                  play={() => play(sound)}
                  choose={(at) => choosePosition(sound, state, at)}
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
              <div className="sound-actions" role={mode === 'details' ? 'cell' : undefined}>
                <button
                  className="icon-button"
                  aria-label={`Reverse and play ${sound.name}`}
                  title="Reverse & play · temporary copy"
                  disabled={
                    !canPlay ||
                    pending === sound.id ||
                    (!host && state.permissions.guestTransforms === false)
                  }
                  onClick={() => {
                    setPending(sound.id);
                    void controller
                      .run('reverse', { soundId: sound.id, libraryVersion: state.library.version })
                      .catch(() => {})
                      .finally(() => setPending(''));
                  }}
                >
                  <Rewind size={14} />
                </button>
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
                      disabled={i === sounds.length - 1}
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
                  workspace={w}
                  update={update}
                  choose={(at) => choosePosition(sound, state, at)}
                />
              )}
              {mode === 'details' && (
                <span className="category-note">{membership.get(sound.id)?.join(' · ')}</span>
              )}
            </Entry>
          );
        })}
      </div>
      {sounds.length > limit && (
        <button
          className="load-more"
          onClick={() => setLimit((n) => n + (mode === 'waveforms' ? 24 : 120))}
        >
          Show more sounds ({sounds.length - limit} remaining)
        </button>
      )}
    </>
  );
}
