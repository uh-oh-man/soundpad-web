import { Play, Rewind } from 'lucide-react';
import { controller } from '../../controller';
import type { Snapshot } from '../../protocol';

/** The session's last clips, from anyone (window or F6), with Replay and Reverse. */
export function MimicRecent({
  state,
  host,
  canPlay,
}: {
  state: Snapshot;
  host: boolean;
  canPlay: boolean;
}) {
  // Skip clips already removed from Temp.
  const recent = (state.mimic?.recent ?? []).filter((clip) =>
    state.library.sounds.some((s) => s.assetId === clip.assetId),
  );
  if (!recent.length) return null;
  const play = (assetId: string, orientation: 'normal' | 'reversed') =>
    void controller.run('audioPlay', { assetId, orientation }).catch(() => {});
  return (
    <section className="mimic-recent" aria-label="Just mimicked">
      <small>Just mimicked</small>
      {recent.map((clip) => (
        <div className="mimic-recent-item" key={clip.assetId}>
          <button
            disabled={!canPlay}
            aria-label={`Replay ${clip.name}`}
            onClick={() => play(clip.assetId, 'normal')}
          >
            <Play size={13} /> <span className="mimic-recent-name">{clip.name}</span>{' '}
            <small>· {clip.by}</small>
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
        </div>
      ))}
    </section>
  );
}
