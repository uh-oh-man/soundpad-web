import { useState } from 'react';
import { BookmarkPlus, Play, Rewind, Scissors } from 'lucide-react';
import { z } from 'zod';
import { controller } from '../../controller';
import type { Snapshot } from '../../protocol';
import { supports } from '../../version';

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
  // Skip clips already removed from Temp.
  const recent = (state.mimic?.recent ?? []).filter((clip) =>
    state.library.sounds.some((s) => s.assetId === clip.assetId),
  );
  if (!recent.length) return null;
  const play = (assetId: string, orientation: 'normal' | 'reversed') =>
    void controller.run('audioPlay', { assetId, orientation }).catch(() => {});
  // Edit copy needs the companion's effects and, for guests, the host's upload permission.
  const canEffect =
    canPlay &&
    supports(state.companionVersion, 'effects') &&
    (host || state.permissions.guestUploads !== false);
  const keepPitch = supports(state.companionVersion, 'keepPitch');
  // Edit copy: same rules as Temp's own button (the host, or guests the host lets add audio).
  const canEdit =
    !!onTempRequest &&
    supports(state.companionVersion, 'trim') &&
    (host || state.permissions.guestUploads !== false);
  async function playWithEffect(assetId: string, effect: QuickEffect) {
    if (making) return;
    setMaking(assetId);
    try {
      const result = z.object({ assetId: z.string().max(64) }).safeParse(
        await controller.run('trimAudio', {
          assetId,
          positionMs: 0,
          endMs: 1800000, // the companion trims this to the clip's real end
          ...(effect === 'slow-voice'
            ? { action: 'speed', speed: 0.5, keepPitch: true }
            : { action: effect }),
        }),
      );
      if (result.success) play(result.data.assetId, 'normal');
    } catch {
      /* The controller shows the error. */
    } finally {
      setMaking('');
    }
  }
  return (
    <section className="mimic-recent" aria-label="Just mimicked">
      <small>Just mimicked</small>
      {recent.map((clip) => (
        <div className="mimic-recent-item" key={clip.assetId}>
          <button disabled={!canPlay} onClick={() => play(clip.assetId, 'normal')}>
            {/* The spoken name keeps the visible text: "Replay <name> · <who>". */}
            <Play size={13} aria-hidden="true" /> <span className="sr-only">Replay </span>
            <span className="mimic-recent-name">{clip.name}</span> <small>· {clip.by}</small>
          </button>
          {/* Hear it backwards: the same reverse Temp offers, under the same guest permissions. */}
          <button
            className="icon-button"
            disabled={!canPlay || (!host && state.permissions.guestTransforms === false)}
            aria-label={`Play ${clip.name} reversed`}
            title="Play reversed"
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
          {/* Voice Replay Studio keeps every capture; here the host keeps the ones worth it (2.26). */}
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
              aria-disabled={!!making || undefined}
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
        </div>
      ))}
    </section>
  );
}
