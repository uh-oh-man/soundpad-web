import { useRef, useState } from 'react';
import { BookAudio, BookmarkPlus, Check, Play, Rewind, Scissors, Trash2 } from 'lucide-react';
import { controller } from '../../controller';
import type { Snapshot } from '../../protocol';
import { supports } from '../../version';
import { guestHeld, HELD_BY_HOST, intentKey, playback, usePendingIntent } from '../../playback';
import { removeNewTempAudio } from '../../remove-temp';
import { guestOwns, refreshTempAssets, useTempAssets } from '../../temp-assets';
import { guestMayRemoveTemp } from '../../temp-rights';
import { clipDeletable, deleteWithUndo, useDeleted } from '../../mimic/pending-delete';
import { followFocus } from '../../focus-follow';
import { canQuickSave, saveClip } from '../../library/quick-save';
import { canEditLibrary } from '../../library/voices';
import { useVoiceStore } from '../../library/voice-store';

/** Quick effects for a fresh clip (2.18.1): each makes an Edit copy of the whole clip and plays it at once. */
const quickEffects = [
  { value: 'chipmunk', label: 'Chipmunk' },
  { value: 'slowmo', label: 'Slow-mo (deeper)' },
  { value: 'echo', label: 'Echo' },
  { value: 'slow-voice', label: 'Slow, same voice (0.5×)' },
] as const;
type QuickEffect = (typeof quickEffects)[number]['value'];

/** The session's last clips, from anyone (window or F6), with Replay, Reverse and quick effects. */
export function MimicRecent({
  state,
  host,
  canPlay,
  onTempRequest,
}: {
  state: Snapshot;
  host: boolean;
  canPlay: boolean;
  onTempRequest?: (kind: 'edit' | 'keep', assetId: string) => void;
}) {
  const [making, setMaking] = useState('');
  const [filter, setFilter] = useState('');
  // One-press Save (2.46): the clip goes into the voice library at once, under its own name.
  const [saving, setSaving] = useState('');
  const [saved, setSaved] = useState<string[]>([]);
  const { status: library } = useVoiceStore();
  // The replay this page asked for and has not heard back about (2.35).
  const intent = usePendingIntent();
  const deleted = useDeleted();
  const { data: temp } = useTempAssets();
  const list = useRef<HTMLElement>(null);
  // Skip clips already removed from Temp, and those deleted here (2.46).
  const visibleAssets = new Set(state.library.sounds.map((sound) => sound.assetId));
  const clips = [...(state.mimic?.recent ?? []), ...(state.mimic?.moreRecent ?? [])].filter(
    (clip) => !deleted.has(clip.assetId) && visibleAssets.has(clip.assetId),
  );
  if (!clips.length) return <p className="muted">Clips you make appear here.</p>;
  const query = filter.trim().toLocaleLowerCase();
  const recent = clips.filter(
    (clip) => !query || `${clip.name} ${clip.by}`.toLocaleLowerCase().includes(query),
  );
  const me = controller.getSnapshot().profile.name;
  // The host may delete any clip; a guest their own, while the host lets guests add audio or use Mimic
  // (Temp's rules since 2.46; the companion checks it really is theirs).
  const canDelete = (clip: (typeof recent)[number]) => {
    const asset = temp?.manager.assets.find((a) => a.id === clip.assetId);
    return (
      clipDeletable(asset) &&
      (host ||
        ((asset ? guestOwns(temp, asset, me) : clip.by === me) &&
          supports(state.companionVersion, 'guestUndo') &&
          guestMayRemoveTemp(state)))
    );
  };
  function remove(index: number, from: Element) {
    const clip = recent[index];
    const next = recent[index + 1] ?? recent[index - 1];
    deleteWithUndo(clip.assetId, `Deleted “${clip.name}” from Temp.`, () =>
      removeNewTempAudio(clip.assetId).finally(refreshTempAssets),
    );
    // The row's buttons go with it: the keyboard moves to the next clip (or the one before), else the heading.
    followFocus(
      from,
      () =>
        (next &&
          list.current?.querySelector<HTMLElement>(
            `[data-clip="${CSS.escape(next.assetId)}"] button`,
          )) ||
        document.getElementById('mimic-title'),
    );
  }
  // Held by the host's Panic (2.35): play controls look unavailable, keep focus and ignore presses.
  const held = guestHeld(state, host);
  const play = (assetId: string, orientation: 'normal' | 'reversed') =>
    !held && void playback.audioPlay(assetId, orientation);
  // Edit copy needs the companion's effects and, for guests, the host's upload permission.
  const canEffect =
    canPlay &&
    supports(state.companionVersion, 'effects') &&
    (host || state.permissions.guestUploads !== false);
  const keepPitch = supports(state.companionVersion, 'keepPitch');
  const canSave = canQuickSave(state, host, controller.hostSite());
  async function save(assetId: string, name: string) {
    if (saving) return;
    setSaving(assetId);
    try {
      await saveClip(assetId, name, canEditLibrary(library, state.permissions, host));
      setSaved((list) => [...list.filter((id) => id !== assetId), assetId]);
    } catch {
      /* The controller shows the refusal. */
    } finally {
      setSaving('');
    }
  }
  // Edit copy: same rules as Temp's own button (the host, or guests the host lets add audio).
  const canEdit =
    !!onTempRequest &&
    supports(state.companionVersion, 'trim') &&
    (host || state.permissions.guestUploads !== false);
  async function playWithEffect(assetId: string, effect: QuickEffect) {
    if (making || held) return;
    setMaking(assetId);
    try {
      // Made and played through the playback path (2.35): if something newer was asked for while
      // the copy was being made, the copy is still added but does not cut in.
      await playback.make(
        'trimAudio',
        {
          assetId,
          positionMs: 0,
          endMs: 1800000, // the companion trims this to the clip's real end
          ...(effect === 'slow-voice'
            ? { action: 'speed', speed: 0.5, keepPitch: true }
            : { action: effect }),
        },
        `make:${assetId}`,
      );
    } catch {
      /* The controller shows the error. */
    } finally {
      setMaking('');
    }
  }
  return (
    <section className="mimic-recent" aria-label="Just mimicked" ref={list}>
      <small>Just mimicked · {clips.length}</small>
      {(clips.length > 8 || filter) && (
        <input
          type="search"
          aria-label="Filter Mimic clips"
          placeholder="Find a clip"
          value={filter}
          onChange={(event) => setFilter(event.target.value)}
        />
      )}
      {!recent.length && <p className="muted">No clips match.</p>}
      {recent.map((clip, index) => (
        <div className="mimic-recent-item" key={clip.assetId} data-clip={clip.assetId}>
          <button
            disabled={!canPlay}
            aria-disabled={held || undefined}
            data-intent={intent === intentKey.asset(clip.assetId, 'normal') ? 'pending' : undefined}
            title={held ? HELD_BY_HOST : undefined}
            onClick={() => play(clip.assetId, 'normal')}
          >
            {/* The spoken name keeps the visible text: "Replay <name> · <who>". */}
            <Play size={13} aria-hidden="true" /> <span className="sr-only">Replay </span>
            <span className="mimic-recent-name">{clip.name}</span> <small>· {clip.by}</small>
          </button>
          {/* Hear it backwards: the same reverse Temp offers, under the same guest permissions. */}
          <button
            className="icon-button"
            disabled={!canPlay || (!host && state.permissions.guestTransforms === false)}
            aria-disabled={held || undefined}
            data-intent={
              intent === intentKey.asset(clip.assetId, 'reversed') ? 'pending' : undefined
            }
            aria-label={`Play ${clip.name} reversed`}
            title={held ? HELD_BY_HOST : 'Play reversed'}
            onClick={() => play(clip.assetId, 'reversed')}
          >
            <Rewind size={14} />
          </button>
          {canEdit && (
            <button
              className="icon-button"
              aria-label={`Edit a copy of ${clip.name}`}
              title="Edit a copy: trim it, add an effect, change its speed"
              onClick={() => onTempRequest?.('edit', clip.assetId)}
            >
              <Scissors size={14} />
            </button>
          )}
          {canSave && (
            <button
              className="mimic-save"
              aria-label={`Save ${clip.name} to the voice library`}
              title="Save it to the voice library now, under this name (kept where Keep puts files)"
              aria-busy={saving === clip.assetId || undefined}
              aria-disabled={!!saving || undefined}
              onClick={() => void save(clip.assetId, clip.name)}
            >
              {saved.includes(clip.assetId) ? (
                <Check size={14} aria-hidden="true" />
              ) : (
                <BookAudio size={14} aria-hidden="true" />
              )}{' '}
              {saving === clip.assetId
                ? 'Saving…'
                : saved.includes(clip.assetId)
                  ? 'Saved'
                  : 'Save'}
            </button>
          )}
          {/* Voice Replay Studio keeps every capture; here the host keeps the ones worth it (2.26).
              With one-press Save (2.46) this stays for choosing another folder or category. */}
          {host && onTempRequest && (
            <button
              className="icon-button"
              aria-label={`Keep ${clip.name} permanently`}
              title="Keep permanently: save it to your Soundpad library for good"
              onClick={() => onTempRequest('keep', clip.assetId)}
            >
              <BookmarkPlus size={14} />
            </button>
          )}
          {canEffect && (
            <select
              className="mimic-recent-effect"
              aria-label={`Play ${clip.name} with an effect`}
              aria-busy={making === clip.assetId || undefined}
              aria-disabled={!!making || held || undefined}
              title={held ? HELD_BY_HOST : undefined}
              value=""
              onChange={(e) => void playWithEffect(clip.assetId, e.target.value as QuickEffect)}
            >
              <option value="" disabled>
                {making === clip.assetId ? 'Making…' : 'Effect…'}
              </option>
              {quickEffects
                .filter((q) => q.value !== 'slow-voice' || keepPitch)
                .map((q) => (
                  <option key={q.value} value={q.value}>
                    {q.label}
                  </option>
                ))}
            </select>
          )}
          {/* One press (2.46): it goes at once, and Undo on the notice brings it back. */}
          {canDelete(clip) && (
            <button
              className="icon-button mimic-delete"
              aria-label={`Delete ${clip.name}`}
              title="Delete: take it out of Temp (Undo brings it back)"
              onClick={(e) => remove(index, e.currentTarget)}
            >
              <Trash2 size={14} />
            </button>
          )}
        </div>
      ))}
    </section>
  );
}
