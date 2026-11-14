import { useEffect, useRef, useState } from 'react';
import { z } from 'zod';
import { Headphones, Play } from 'lucide-react';
import { controller } from '../controller';
import { supports } from '../version';
import type { Snapshot, TrimSource } from '../protocol';
import { TrimWave } from './TrimWave';
import { usePreviewAudio } from '../preview-audio';
import { removeNewTempAudio } from '../remove-temp';
import { guestHeld, HELD_BY_HOST, playback } from '../playback';
import { refreshTempAssets, useTempAssets } from '../temp-assets';
import { takeFocusStart } from '../edit-copy';
import type { EditFields } from '../shell';

/** Voice effects the companion can apply to an edited copy (2.9.0). */
const effects = {
  none: 'None',
  chipmunk: 'Chipmunk (1.5× faster, higher)',
  slowmo: 'Slow-mo (0.7×, deeper)',
  echo: 'Echo',
  speed: 'Any speed',
} as const;
const effectSuffix = { chipmunk: 'chipmunk', slowmo: 'slow-mo', echo: 'echo' } as const;
type Effect = keyof typeof effects;
const isEffect = (value: unknown): value is Effect =>
  typeof value === 'string' && Object.hasOwn(effects, value);
/** Free speed range (companion 2.16): 20 times slower to 20 times faster. */
const MIN_SPEED = 0.05,
  MAX_SPEED = 20;
const clampSpeed = (s: number) =>
  Math.min(MAX_SPEED, Math.max(MIN_SPEED, Number.isFinite(s) ? s : 1));
/** The slider is logarithmic, -100..100 ↔ 0.05×..20×, so slowing down gets as much travel as speeding up. */
const speedToSlider = (s: number) =>
  Math.round((Math.log(clampSpeed(s)) / Math.log(MAX_SPEED)) * 100);
const sliderToSpeed = (v: number) => Number(Math.pow(MAX_SPEED, v / 100).toPrecision(2));
const formatSpeed = (s: number) => `${Number(s.toFixed(2))}×`;

type Fields = Omit<EditFields, 'effect'> & { effect: Effect };

/** The whole sound, plain, unless remembered fields say otherwise (clamped to this sound). */
function startFields(lengthMs: number, initial?: Partial<EditFields>): Fields {
  const length = lengthMs / 1000;
  const from = Math.min(Math.max(0, Number(initial?.from ?? 0) || 0), length);
  const to = Math.min(Math.max(from, Number(initial?.to ?? length) || length), length);
  return {
    from,
    to,
    name: typeof initial?.name === 'string' ? initial.name.slice(0, 100) : '',
    effect: isEffect(initial?.effect) ? initial.effect : 'none',
    speed: clampSpeed(Number(initial?.speed ?? 1)),
    keepPitch: initial?.keepPitch === true,
  };
}

/**
 * The copy editor (moved out of the Temp list into the editor panel in 2.43): cut, effect, speed and
 * name for a new Temp copy, with the host's private Preview and Loop. The original never changes.
 * The source is a Temp asset, or (companion 2.43, gate soundEdit) a library sound.
 */
export function EditCopyForm({
  state,
  host,
  source,
  name,
  lengthMs,
  waveSoundId,
  initial,
  focusStart = false,
  onFields,
  onDone,
}: {
  state: Snapshot;
  host: boolean;
  source: TrimSource;
  /** The source's name, as the form is named: "Edit a copy of <name>". */
  name: string;
  lengthMs: number;
  /** The library sound whose waveform is drawn (a Temp source's normal variant). */
  waveSoundId?: string | null;
  initial?: Partial<EditFields>;
  /** Put the keyboard on Start once shown (the Mimic flow, 2.21). */
  focusStart?: boolean;
  /** Called with the fields as they change (the panel remembers them). */
  onFields?: (fields: EditFields) => void;
  /** Saved or cancelled: the editor is done. */
  onDone: () => void;
}) {
  const [trimming, setTrimming] = useState<Fields>(() => startFields(lengthMs, initial));
  const [pending, setPending] = useState(false);
  const [error, setError] = useState('');
  const held = guestHeld(state, host);
  const fieldsSeen = useRef(onFields);
  fieldsSeen.current = onFields;
  useEffect(() => fieldsSeen.current?.(trimming), [trimming]);
  const startInput = useRef<HTMLInputElement>(null);
  useEffect(() => {
    if (!focusStart) return;
    // After the panel has put focus on its title, bring Start into view and give it the keyboard.
    const timer = setTimeout(() => {
      startInput.current?.scrollIntoView({ block: 'center' });
      startInput.current?.focus({ preventScroll: true });
    }, 50);
    return () => clearTimeout(timer);
  }, [focusStart]);

  /** What Save copy and Preview send: the chosen part, clamped to the sound, and its effect. */
  const trimFields = (t: Fields) => ({
    ...source,
    positionMs: Math.round(Math.min(Math.max(0, t.from * 1000), lengthMs)),
    endMs: Math.round(Math.min(Math.max(0, t.to * 1000), lengthMs)),
    action: t.effect,
    ...(t.effect === 'speed' && { speed: clampSpeed(t.speed) }),
    ...(t.effect === 'speed' &&
      t.keepPitch &&
      supports(state.companionVersion, 'keepPitch') && { keepPitch: true }),
  });
  /**
   * Whether an unnamed copy will be named after its own words (2.24, like Voice Replay Studio's "Save +
   * retranscribe"): a plain trim of up to 30 s while the host has Mimic transcripts on.
   */
  const byWords = (t: Fields) =>
    t.effect === 'none' &&
    !!state.mimic?.transcribing &&
    supports(state.companionVersion, 'trimWords') &&
    t.to - t.from <= 30;

  /**
   * Undo after Edit copy (2.28): removes the new copy from Temp. The companion never removes a temporary
   * file that is playing, so a copy still playing after Save & play is stopped first.
   */
  async function undoCopy(assetId: string) {
    try {
      await removeNewTempAudio(assetId);
      controller.notify('Copy removed from Temp.', 'success');
      refreshTempAssets();
    } catch {
      /* The controller shows the error. */
    }
  }

  async function save(play: boolean) {
    // Busy buttons stay enabled (aria-disabled) so they keep keyboard focus; ignore presses until done.
    if (pending) return;
    setPending(true);
    setError('');
    const fields = {
      ...trimFields(trimming),
      ...(trimming.name.trim() && { name: trimming.name.trim() }),
    };
    const key = `make:${'assetId' in source && source.assetId ? source.assetId : source.soundId}`;
    try {
      // Save & play (2.27), like Mimic & play: the copy plays through Soundpad as soon as it is made.
      // With a 2.35 companion that is one command, and the copy plays only if nothing newer was asked
      // for while it was being made (2.35).
      const { data: result, played } = play
        ? await playback.make('trimAudio', fields, key)
        : { data: await controller.run('trimAudio', fields), played: false };
      const copy = z.object({ assetId: z.string().max(64) }).safeParse(result);
      // Undo (2.28, host): like Voice Replay Studio's Undo trim, take a copy made by mistake straight back out.
      const undo =
        (host || supports(state.companionVersion, 'guestUndo')) && copy.success
          ? { label: 'Undo', run: () => void undoCopy(copy.data.assetId) }
          : undefined;
      // Says "playing" only when it did start; a copy that lost to a newer click is just added.
      controller.notify(
        played
          ? 'Copy added to Temp and playing. The original is unchanged.'
          : 'Copy added to Temp. The original is unchanged.',
        'success',
        undo,
      );
      refreshTempAssets();
      onDone();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Temp action failed.');
    } finally {
      setPending(false);
    }
  }

  // Host-only Preview of an edited copy (2.20): heard on this PC only, never through Soundpad.
  const { previewing, play: preview, stop: stopPreview, setLooping } = usePreviewAudio();
  // Loop (2.23), like Voice Replay Studio's Loop selection: the preview repeats, and changing the cut or
  // effect restarts it with the change, so edges can be tuned by ear.
  const [loop, setLoop] = useState(false);
  const loopKey =
    previewing && loop
      ? JSON.stringify([
          trimming.from,
          trimming.to,
          trimming.effect,
          trimming.speed,
          trimming.keepPitch,
        ])
      : '';
  const loopedKey = useRef('');
  useEffect(() => {
    if (!loopKey) {
      loopedKey.current = '';
      return;
    }
    if (!loopedKey.current) loopedKey.current = loopKey; // the Preview press itself started this one
    if (loopedKey.current === loopKey) return;
    loopedKey.current = loopKey;
    // Wait for a pause in dragging or typing, then hear the new cut.
    const timer = setTimeout(() => {
      if (trimming.to - trimming.from >= 0.1 && trimming.from >= 0)
        void preview('trimPreview', trimFields(trimming), { loop: true });
    }, 400);
    return () => clearTimeout(timer);
  }, [loopKey]);

  const tooShort = trimming.to - trimming.from < 0.1 || trimming.from < 0;
  return (
    <form
      className="temp-trim"
      aria-label={`Edit a copy of ${name}`}
      // The exact length (e.g. 2.614 s) is rarely a multiple of the 0.05 s step, which
      // would make the browser silently refuse to submit. Clamp here instead.
      noValidate
      onSubmit={(e) => {
        e.preventDefault();
        stopPreview();
        // The submitter says which button: Save copy, or Save & play.
        const play = (e.nativeEvent as SubmitEvent).submitter?.getAttribute('value') === 'play';
        if (play && held) return;
        void save(play);
      }}
    >
      {waveSoundId && (
        <TrimWave
          state={state}
          soundId={waveSoundId}
          lengthMs={lengthMs}
          from={trimming.from}
          to={trimming.to}
          onChange={(range) => setTrimming({ ...trimming, ...range })}
        />
      )}
      <label>
        Start (s)
        <input
          ref={startInput}
          type="number"
          min={0}
          max={lengthMs / 1000}
          step={0.05}
          value={trimming.from}
          onChange={(e) => setTrimming({ ...trimming, from: Number(e.target.value) })}
        />
      </label>
      <label>
        End (s)
        <input
          type="number"
          min={0}
          max={lengthMs / 1000}
          step={0.05}
          value={trimming.to}
          onChange={(e) => setTrimming({ ...trimming, to: Number(e.target.value) })}
        />
      </label>
      {supports(state.companionVersion, 'effects') && (
        <label>
          Effect
          <select
            value={trimming.effect}
            onChange={(e) => setTrimming({ ...trimming, effect: e.target.value as Effect })}
          >
            {Object.entries(effects)
              .filter(([value]) => value !== 'speed' || supports(state.companionVersion, 'speed'))
              .map(([value, label]) => (
                <option key={value} value={value}>
                  {label}
                </option>
              ))}
          </select>
        </label>
      )}
      {trimming.effect === 'speed' && (
        // Any speed: tape style, so pitch moves with it (slower is deeper, faster is higher).
        <div className="temp-speed" role="group" aria-label="Speed">
          <label>
            Speed
            <input
              type="range"
              min={-100}
              max={100}
              step={1}
              aria-valuetext={formatSpeed(trimming.speed)}
              value={speedToSlider(trimming.speed)}
              onChange={(e) =>
                setTrimming({ ...trimming, speed: sliderToSpeed(Number(e.target.value)) })
              }
            />
          </label>
          <label>
            Exact speed (×)
            <input
              type="number"
              min={MIN_SPEED}
              max={MAX_SPEED}
              step={0.05}
              value={trimming.speed}
              onChange={(e) => setTrimming({ ...trimming, speed: Number(e.target.value) })}
              onBlur={() => setTrimming({ ...trimming, speed: clampSpeed(trimming.speed) })}
            />
          </label>
          {supports(state.companionVersion, 'keepPitch') && (
            // Time-stretch (2.17): voices keep their pitch instead of going deep or squeaky.
            <label className="temp-keep-pitch">
              <input
                type="checkbox"
                checked={trimming.keepPitch}
                onChange={(e) => setTrimming({ ...trimming, keepPitch: e.target.checked })}
              />
              Keep pitch
            </label>
          )}
          <span className="muted">
            {formatSpeed(clampSpeed(trimming.speed))}: the copy lasts{' '}
            {(Math.max(0, trimming.to - trimming.from) / clampSpeed(trimming.speed)).toFixed(2)} s.{' '}
            {trimming.keepPitch && supports(state.companionVersion, 'keepPitch')
              ? 'Same pitch.'
              : 'Pitch changes with speed.'}
          </span>
        </div>
      )}
      <label>
        Name
        <input
          maxLength={100}
          placeholder={`${byWords(trimming) ? 'Its words' : name} (${
            trimming.effect === 'none'
              ? 'trimmed'
              : trimming.effect === 'speed'
                ? `${Number(clampSpeed(trimming.speed).toFixed(2))}x speed${trimming.keepPitch && supports(state.companionVersion, 'keepPitch') ? ', same pitch' : ''}`
                : effectSuffix[trimming.effect]
          })`}
          value={trimming.name}
          onChange={(e) => setTrimming({ ...trimming, name: e.target.value })}
        />
      </label>
      <span className="muted">
        {Math.max(0, trimming.to - trimming.from).toFixed(2)} s of {(lengthMs / 1000).toFixed(2)} s
        · saved as a new Temp sound
        {byWords(trimming) && ' · left unnamed, it is named after its words (transcripts are on)'}
      </span>
      {error && (
        <p className="warning" role="alert">
          {error}
        </p>
      )}
      <button
        className="primary"
        type="submit"
        disabled={tooShort}
        aria-disabled={pending || undefined}
      >
        Save copy
      </button>
      {(host || state.permissions.remotePlayback) && (
        <button
          type="submit"
          value="play"
          title={held ? HELD_BY_HOST : 'Make the copy and play it through Soundpad straight away'}
          disabled={tooShort || !state.available}
          aria-disabled={pending || held || undefined}
        >
          <Play size={15} /> Save &amp; play
        </button>
      )}
      {host && supports(state.companionVersion, 'trimPreview') && (
        <button
          type="button"
          title="Hear the copy on this PC only (its first 20 s); nobody else hears it"
          disabled={tooShort}
          onClick={() =>
            previewing ? stopPreview() : void preview('trimPreview', trimFields(trimming), { loop })
          }
        >
          <Headphones size={15} /> {previewing ? 'Stop preview' : 'Preview'}
        </button>
      )}
      {host && supports(state.companionVersion, 'trimPreview') && (
        <label
          className="temp-loop"
          title="Repeat the preview; changing the cut or effect restarts it"
        >
          <input
            type="checkbox"
            checked={loop}
            onChange={(e) => {
              setLoop(e.target.checked);
              setLooping(e.target.checked);
            }}
          />
          Loop
        </label>
      )}
      <button
        type="button"
        onClick={() => {
          stopPreview();
          onDone();
        }}
      >
        Cancel
      </button>
    </form>
  );
}

/**
 * The copy editor for a Temp source, in the panel (2.43): waits for the source to be listed (a fresh
 * Mimic clip takes a moment), then shows its form. A source that never shows within 20 s is reported
 * instead of opening long after the press (2.29.3).
 */
export function TempCopyEditor({
  state,
  host,
  sourceId,
  fields,
  onFields,
  onDone,
}: {
  state: Snapshot;
  host: boolean;
  sourceId: string;
  fields?: Partial<EditFields>;
  onFields?: (fields: EditFields) => void;
  onDone: () => void;
}) {
  const { data } = useTempAssets();
  const asset = data?.manager.assets.find((a) => a.id === sourceId);
  const soundId = asset?.normal?.soundId;
  const lengthMs = state.library.sounds.find((s) => s.id === soundId)?.durationMs ?? undefined;
  const [late, setLate] = useState(false);
  // Opened from a fresh Mimic clip: Start gets the keyboard once the form shows (2.21).
  const [focusStart] = useState(() => takeFocusStart(sourceId));
  const ready = !!asset && lengthMs !== undefined;
  useEffect(() => {
    if (ready) return;
    const timer = setTimeout(() => setLate(true), 20000);
    return () => clearTimeout(timer);
  }, [ready, sourceId]);
  if (!ready)
    return (
      <p className="muted" role="status">
        {late
          ? 'This Temp source is not available any more. Choose Edit copy in Temp again.'
          : 'Waiting for the sound to be ready in Temp…'}
      </p>
    );
  return (
    <EditCopyForm
      key={sourceId}
      state={state}
      host={host}
      source={{ assetId: sourceId }}
      name={asset.name}
      lengthMs={lengthMs}
      waveSoundId={soundId}
      initial={fields}
      focusStart={focusStart}
      onFields={onFields}
      onDone={onDone}
    />
  );
}
