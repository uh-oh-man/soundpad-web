import type { Snapshot } from '../../protocol';
import type { Workspace } from '../../workspace';
import { supports } from '../../version';
import { playback } from '../../playback';
import { useTempAssets } from '../../temp-assets';
import { Waveform } from '../Waveform';
import { IconEditor } from '../IconEditor';
import { Statistics } from '../Statistics';
import { EditCopyForm } from '../EditCopyForm';

/**
 * One sound's editor in the panel (2.43): its waveform with cues and regions, effects and speed copies,
 * its icon (host) and its statistics. Effects need a Temp copy of the sound, or a 2.43 companion for a
 * Soundpad sound (gate soundEdit); otherwise one line says so instead of a disabled form.
 */
export function SoundEditor({
  state,
  host,
  canPlay,
  soundId,
  focus,
  workspace,
  update,
  held,
  onDone,
}: {
  state: Snapshot;
  host: boolean;
  canPlay: boolean;
  soundId: string;
  focus?: 'effects' | 'statistics';
  workspace: Workspace | null;
  update: (fn: (w: Workspace) => Workspace) => void;
  held: boolean;
  onDone: () => void;
}) {
  const { data } = useTempAssets();
  const sound = state.library.sounds.find((s) => s.id === soundId);
  if (!sound)
    return (
      <p className="muted" role="status">
        This sound is not in the library any more.
      </p>
    );
  // A Temp-backed tile: its source asset is the copy's source, as in the Temp list.
  const asset = data?.manager.assets.find((a) =>
    a.variants.some((v) => v.soundId === sound.id && v.orientation === 'normal'),
  );
  const mayTrim =
    supports(state.companionVersion, 'trim') && (host || state.permissions.guestUploads !== false);
  const native = !asset;
  const lengthMs = sound.durationMs ?? undefined;
  return (
    <div className="sound-editor">
      <section className="sound-editor-wave" aria-label={`Waveform of ${sound.name}`}>
        <Waveform
          sound={sound}
          state={state}
          disabled={!canPlay || (state.permissions.guestSeeking === false && !host)}
          held={held}
          workspace={workspace}
          update={update}
          choose={(at) => playback.position(sound.id, at)}
        />
      </section>
      {mayTrim && (
        <section className="sound-editor-effects" aria-label="Effects and speed">
          <h3>Effects and speed</h3>
          {native && !supports(state.companionVersion, 'soundEdit') ? (
            <p className="muted">
              Effects on Soundpad sounds need companion 2.43 or later. Cues, regions, icon and
              statistics work here.
            </p>
          ) : lengthMs === undefined ? (
            <p className="muted">Soundpad has not reported this sound&apos;s length yet.</p>
          ) : (
            <EditCopyForm
              key={sound.id}
              state={state}
              host={host}
              source={asset ? { assetId: asset.id } : { soundId: sound.id }}
              name={sound.name}
              lengthMs={lengthMs}
              waveSoundId={sound.id}
              focusStart={focus === 'effects'}
              onDone={onDone}
            />
          )}
        </section>
      )}
      {host && (
        <details className="sound-editor-icon">
          <summary>Icon</summary>
          <IconEditor sound={sound} close={() => {}} />
        </details>
      )}
      <details className="sound-editor-stats" open={focus === 'statistics'}>
        <summary>Statistics</summary>
        <Statistics sound={sound} version={state.statisticsVersion ?? 0} />
      </details>
    </div>
  );
}
