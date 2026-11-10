import { useEffect, useRef, useState } from 'react';
import { z } from 'zod';
import { controller } from '../controller';
import { supports } from '../version';
import { flattenCategories, type Snapshot } from '../protocol';
import { TrimWave } from './TrimWave';
import { usePreviewAudio } from '../preview-audio';
import { Headphones, Play } from 'lucide-react';
import { onTempRequest, type TempRequest } from '../edit-copy';
import { removeNewTempAudio } from '../remove-temp';
const variant = z.object({
  id: z.string(),
  soundId: z.string().nullable(),
  orientation: z.string(),
  state: z.string(),
  ready: z.boolean(),
  permanent: z.boolean(),
  legacyApproval: z.boolean(),
  current: z.boolean(),
  nativeLabel: z.string().nullable().optional(),
});
const asset = z.object({
  id: z.string(),
  name: z.string(),
  origin: z.string(),
  uploader: z.string(),
  state: z.string(),
  normal: variant.nullable(),
  reversed: variant.nullable(),
  variants: z.array(variant),
  hasTemporary: z.boolean(),
});
const response = z.object({
  manager: z.object({
    revision: z.number(),
    assets: z.array(asset),
    issues: z.array(
      z.object({
        id: z.string(),
        assetId: z.string().nullable(),
        code: z.string().optional(),
        message: z.string(),
        severity: z.string(),
      }),
    ),
    blocked: z.boolean(),
    temporaryBytes: z.number(),
  }),
  locations: z
    .array(
      z.object({
        assetId: z.string(),
        variantId: z.string(),
        path: z.string(),
        storage: z.string(),
      }),
    )
    .nullable(),
});
type Asset = z.infer<typeof asset>;
type Variant = z.infer<typeof variant>;
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

export function TempManager({
  state,
  host,
  connected,
}: {
  state: Snapshot;
  host: boolean;
  connected: boolean;
}) {
  const [open, setOpen] = useState(false),
    [data, setData] = useState<z.infer<typeof response>>(),
    [error, setError] = useState(''),
    [pending, setPending] = useState('');
  const [saving, setSaving] = useState<{ asset: Asset; variant: Variant }>(),
    [directory, setDirectory] = useState(''),
    [category, setCategory] = useState('');
  const [trimming, setTrimming] = useState<{
    id: string;
    from: number;
    to: number;
    name: string;
    effect: Effect;
    speed: number;
    keepPitch: boolean;
  }>();
  const [refresh, setRefresh] = useState(0);
  // Search (2.25), like the Voice Replay Studio list: a long session's Temp holds many sources.
  const [find, setFind] = useState('');
  // A clip whose Edit copy (2.21) or Keep (2.26) was asked for from Mimic, opened once Temp lists it.
  // It is dropped if it can't be met within 20 s (2.29.3), so it never opens long after the press.
  const [wanted, setWanted] = useState<TempRequest & { at: number }>();
  useEffect(
    () =>
      onTempRequest((request) => {
        setOpen(true);
        setWanted({ ...request, at: Date.now() });
      }),
    [],
  );
  useEffect(() => {
    if (!connected) return;
    let live = true;
    // Processing one clip bumps the version several times within a second; fetch once it settles, so a
    // guest doesn't send a burst of requests (9 in 0.4 s were measured) and hit the host's rate limit.
    const timer = setTimeout(() => {
      void controller
        .run('audioAssets')
        .then((raw) => {
          if (live) {
            setData(response.parse(raw));
            setError('');
          }
        })
        .catch((e) => {
          if (live) setError(String(e.message));
        });
    }, 300);
    return () => {
      live = false;
      clearTimeout(timer);
    };
  }, [connected, state.managedAudioVersion, refresh]);
  async function run(
    op:
      | 'audioPlay'
      | 'keepAudio'
      | 'removeAudio'
      | 'retryAudio'
      | 'retryCleanup'
      | 'cleanupCategories'
      | 'clearTemp'
      | 'trimAudio',
    fields: Parameters<typeof controller.run>[1] = {},
    { play = false } = {},
  ) {
    // Busy buttons stay enabled (aria-disabled) so they keep keyboard focus; ignore presses until done.
    if (pending) return;
    setPending(fields.assetId ?? 'manager');
    setError('');
    try {
      const result = await controller.run(op, fields);
      if (op === 'clearTemp') {
        const cleared = z.object({ cleared: z.number().int() }).safeParse(result);
        controller.notify(
          `Cleared ${cleared.success ? cleared.data.cleared : 'all'} temporary audio file${cleared.success && cleared.data.cleared === 1 ? '' : 's'}; Soundpad's Temp category is gone too.`,
          'success',
        );
      }
      if (op === 'cleanupCategories') {
        const removed = z.object({ removed: z.number().int() }).safeParse(result);
        controller.notify(
          `Removed ${removed.success ? removed.data.removed : 'the'} duplicate Temp categories from Soundpad.`,
          'success',
        );
      }
      if (op === 'keepAudio') {
        setSaving(undefined);
        controller.notify('Permanent file and Soundpad entry verified.', 'success');
      }
      if (op === 'trimAudio') {
        setTrimming(undefined);
        // Save & play (2.27), like Mimic & play: the new copy plays through Soundpad straight away.
        const copy = z.object({ assetId: z.string().max(64) }).safeParse(result);
        if (play && copy.success)
          await controller.run('audioPlay', { assetId: copy.data.assetId, orientation: 'normal' });
        // Undo (2.28, host): like Voice Replay Studio's Undo trim, take a copy made by mistake straight back out.
        const undo =
          host && copy.success
            ? { label: 'Undo', run: () => void undoCopy(copy.data.assetId) }
            : undefined;
        controller.notify(
          play && copy.success
            ? 'Copy added to Temp and playing. The original is unchanged.'
            : 'Copy added to Temp. The original is unchanged.',
          'success',
          undo,
        );
      }
      setRefresh((n) => n + 1);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Temp action failed.');
    } finally {
      setPending('');
    }
  }
  /**
   * Undo after Edit copy (2.28): removes the new copy from Temp. The companion never removes a temporary
   * file that is playing, so a copy still playing after Save & play is stopped first.
   */
  async function undoCopy(assetId: string) {
    try {
      await removeNewTempAudio(assetId);
      controller.notify('Copy removed from Temp.', 'success');
      setRefresh((n) => n + 1);
    } catch {
      /* The controller shows the error. */
    }
  }
  // Host-only Preview of an edited copy (2.20): heard on this PC only, never through Soundpad.
  const { previewing, play: preview, stop: stopPreview, setLooping } = usePreviewAudio();
  // Loop (2.23), like Voice Replay Studio's Loop selection: the preview repeats, and changing the cut or
  // effect restarts it with the change, so edges can be tuned by ear.
  const [loop, setLoop] = useState(false);
  /** What Save copy and Preview send: the chosen part, clamped to the sound, and its effect. */
  const trimFields = (assetId: string, length: number, t: NonNullable<typeof trimming>) => ({
    assetId,
    positionMs: Math.round(Math.min(Math.max(0, t.from * 1000), length)),
    endMs: Math.round(Math.min(Math.max(0, t.to * 1000), length)),
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
  const byWords = (t: NonNullable<typeof trimming>) =>
    t.effect === 'none' &&
    !!state.mimic?.transcribing &&
    supports(state.companionVersion, 'trimWords') &&
    t.to - t.from <= 30;
  /** Length of a source's normal variant, from the native library entry Soundpad reports. */
  const durationOf = (a: { normal?: { soundId?: string | null } | null }) =>
    state.library.sounds.find((s) => s.id === a.normal?.soundId)?.durationMs ?? undefined;
  useEffect(() => {
    if (!wanted || pending) return;
    if (Date.now() - wanted.at > 20000) {
      setWanted(undefined);
      return;
    }
    const a = data?.manager.assets.find((x) => x.id === wanted.assetId);
    if (wanted.kind === 'keep') {
      // The clip's own (normal) version, once Soundpad lists it, exactly as its "Keep normal
      // permanently" button in Temp would offer it.
      const v = a?.variants.find(
        (x) =>
          x.orientation === 'normal' &&
          x.current &&
          !x.permanent &&
          x.state === 'ready' &&
          x.nativeLabel,
      );
      if (a?.variants.some((x) => x.orientation === 'normal' && x.current && x.permanent)) {
        setWanted(undefined);
        controller.notify(`${a.name} is already kept permanently.`);
        return;
      }
      if (!a || !v) return; // not ready yet: try again when Temp refreshes
      setWanted(undefined);
      setSaving({ asset: a, variant: v });
      setCategory('');
      setDirectory('');
      setTimeout(() => {
        const first = document.querySelector<HTMLElement>('#temp-manager .temp-save input');
        first?.scrollIntoView({ block: 'center' });
        first?.focus({ preventScroll: true });
      }, 50);
      return;
    }
    const length = a && durationOf(a);
    if (length === undefined) return; // not listed yet: try again when Temp refreshes
    setWanted(undefined);
    setTrimming({
      id: wanted.assetId,
      from: 0,
      to: length / 1000,
      name: '',
      effect: 'none',
      speed: 1,
      keepPitch: false,
    });
    // After the Mimic window has closed and handed focus back, bring the form into view and focus Start.
    setTimeout(() => {
      const start = document.querySelector<HTMLInputElement>('#temp-manager form.temp-trim input');
      start?.scrollIntoView({ block: 'center' });
      start?.focus({ preventScroll: true });
    }, 50);
  });
  const loopKey =
    trimming && previewing && loop
      ? JSON.stringify([
          trimming.id,
          trimming.from,
          trimming.to,
          trimming.effect,
          trimming.speed,
          trimming.keepPitch,
        ])
      : '';
  const loopedKey = useRef('');
  useEffect(() => {
    if (!loopKey || !trimming) {
      loopedKey.current = '';
      return;
    }
    if (!loopedKey.current) loopedKey.current = loopKey; // the Preview press itself started this one
    if (loopedKey.current === loopKey) return;
    loopedKey.current = loopKey;
    // Wait for a pause in dragging or typing, then hear the new cut.
    const timer = setTimeout(() => {
      const a = data?.manager.assets.find((x) => x.id === trimming.id);
      const length = a && durationOf(a);
      if (length !== undefined && trimming.to - trimming.from >= 0.1 && trimming.from >= 0)
        void preview('trimPreview', trimFields(trimming.id, length, trimming), { loop: true });
    }, 400);
    return () => clearTimeout(timer);
  }, [loopKey]);
  const listed = (data?.manager.assets ?? []).filter(
    (a) =>
      a.state !== 'removed' &&
      (a.origin === 'upload' || a.variants.some((v) => v.orientation === 'reversed')),
  );
  const findWords = listed.length > 6 ? find.trim().toLowerCase() : '';
  // The source being edited stays listed, so a search never hides an open Edit copy form.
  const shown = listed.filter(
    (a) =>
      !findWords ||
      a.id === trimming?.id ||
      `${a.name} ${a.uploader}`.toLowerCase().includes(findWords),
  );
  const categories = flattenCategories(state.library.categories).filter(
    (c) => c.category.id !== undefined,
  );
  return (
    <details
      className="temp-manager"
      open={open}
      onToggle={(e) => setOpen(e.currentTarget.open)}
      id="temp-manager"
    >
      <summary>
        Temp{' '}
        <small>
          {data?.manager.assets.filter(
            (a) => a.hasTemporary || a.state === 'accepted-needs-migration',
          ).length ?? 0}{' '}
          sources · {((data?.manager.temporaryBytes ?? 0) / 1048576).toFixed(1)} MiB
          {data?.manager.issues.length ? ' · needs attention' : ''}
        </small>
      </summary>
      {open && (
        <div className="temp-manager-body">
          <p className="muted">
            Session audio uses Soundpad’s normal mic playback. Only the host can save a permanent
            copy.
          </p>
          {error && (
            <p className="warning" role="alert">
              {error}
            </p>
          )}
          {data?.manager.blocked && (
            <p className="warning">
              Managed audio is paused. Files are preserved; resolve the recovery issue below.
            </p>
          )}
          {listed.length > 6 && (
            <input
              type="search"
              className="temp-search"
              aria-label="Search Temp"
              placeholder={`Search ${listed.length} sources`}
              title="Matches the name or who added it"
              value={find}
              onChange={(e) => setFind(e.target.value)}
            />
          )}
          {!shown.length && !!listed.length && (
            <p className="muted">No sources match “{find.trim()}”.</p>
          )}
          {shown.map((a) => (
            <article className="temp-source" key={a.id}>
              <div>
                <strong>{a.name}</strong>
                <small>
                  {a.uploader} · {a.origin === 'native' ? 'Native source' : 'Upload'} · {a.state}
                </small>
              </div>
              <div className="temp-actions">
                {(['normal', 'reversed'] as const).map((orientation) => (
                  <button
                    key={orientation}
                    aria-disabled={!!pending || undefined}
                    disabled={
                      !state.available ||
                      !connected ||
                      (!host && !state.permissions.remotePlayback) ||
                      (!host &&
                        !a[orientation]?.ready &&
                        state.permissions.guestTransforms === false)
                    }
                    onClick={() => void run('audioPlay', { assetId: a.id, orientation })}
                  >
                    Play {orientation}
                  </button>
                ))}
                {(host || state.permissions.guestUploads !== false) &&
                  durationOf(a) !== undefined &&
                  supports(state.companionVersion, 'trim') && (
                    <button
                      disabled={!connected}
                      aria-disabled={!!pending || undefined}
                      onClick={() =>
                        // A running command closes open forms when it finishes; don't open one now.
                        !pending &&
                        setTrimming({
                          id: a.id,
                          from: 0,
                          to: durationOf(a)! / 1000,
                          name: '',
                          effect: 'none',
                          speed: 1,
                          keepPitch: false,
                        })
                      }
                    >
                      Edit copy
                    </button>
                  )}
              </div>
              {trimming?.id === a.id && (
                <form
                  className="temp-trim"
                  aria-label={`Edit a copy of ${a.name}`}
                  // The exact length (e.g. 2.614 s) is rarely a multiple of the 0.05 s step, which
                  // would make the browser silently refuse to submit. Clamp here instead.
                  noValidate
                  onSubmit={(e) => {
                    e.preventDefault();
                    stopPreview();
                    // The submitter says which button: Save copy, or Save & play.
                    const play =
                      (e.nativeEvent as SubmitEvent).submitter?.getAttribute('value') === 'play';
                    void run(
                      'trimAudio',
                      {
                        ...trimFields(a.id, durationOf(a)!, trimming),
                        ...(trimming.name.trim() && { name: trimming.name.trim() }),
                      },
                      { play },
                    );
                  }}
                >
                  {a.normal?.soundId && (
                    <TrimWave
                      state={state}
                      soundId={a.normal.soundId}
                      lengthMs={durationOf(a)!}
                      from={trimming.from}
                      to={trimming.to}
                      onChange={(range) => setTrimming({ ...trimming, ...range })}
                    />
                  )}
                  <label>
                    Start (s)
                    <input
                      type="number"
                      min={0}
                      max={durationOf(a)! / 1000}
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
                      max={durationOf(a)! / 1000}
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
                        onChange={(e) =>
                          setTrimming({ ...trimming, effect: e.target.value as Effect })
                        }
                      >
                        {Object.entries(effects)
                          .filter(
                            ([value]) =>
                              value !== 'speed' || supports(state.companionVersion, 'speed'),
                          )
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
                            setTrimming({
                              ...trimming,
                              speed: sliderToSpeed(Number(e.target.value)),
                            })
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
                          onChange={(e) =>
                            setTrimming({ ...trimming, speed: Number(e.target.value) })
                          }
                          onBlur={() =>
                            setTrimming({ ...trimming, speed: clampSpeed(trimming.speed) })
                          }
                        />
                      </label>
                      {supports(state.companionVersion, 'keepPitch') && (
                        // Time-stretch (2.17): voices keep their pitch instead of going deep or squeaky.
                        <label className="temp-keep-pitch">
                          <input
                            type="checkbox"
                            checked={trimming.keepPitch}
                            onChange={(e) =>
                              setTrimming({ ...trimming, keepPitch: e.target.checked })
                            }
                          />
                          Keep pitch
                        </label>
                      )}
                      <span className="muted">
                        {formatSpeed(clampSpeed(trimming.speed))}: the copy lasts{' '}
                        {(
                          Math.max(0, trimming.to - trimming.from) / clampSpeed(trimming.speed)
                        ).toFixed(2)}{' '}
                        s.{' '}
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
                      placeholder={`${byWords(trimming) ? 'Its words' : a.name} (${
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
                    {Math.max(0, trimming.to - trimming.from).toFixed(2)} s of{' '}
                    {(durationOf(a)! / 1000).toFixed(2)} s · saved as a new Temp sound
                    {byWords(trimming) &&
                      ' · left unnamed, it is named after its words (transcripts are on)'}
                  </span>
                  <button
                    className="primary"
                    type="submit"
                    disabled={trimming.to - trimming.from < 0.1 || trimming.from < 0}
                    aria-disabled={!!pending || undefined}
                  >
                    Save copy
                  </button>
                  {(host || state.permissions.remotePlayback) && (
                    <button
                      type="submit"
                      value="play"
                      title="Make the copy and play it through Soundpad straight away"
                      disabled={
                        trimming.to - trimming.from < 0.1 || trimming.from < 0 || !state.available
                      }
                      aria-disabled={!!pending || undefined}
                    >
                      <Play size={15} /> Save &amp; play
                    </button>
                  )}
                  {host && supports(state.companionVersion, 'trimPreview') && (
                    <button
                      type="button"
                      title="Hear the copy on this PC only (its first 20 s); nobody else hears it"
                      disabled={trimming.to - trimming.from < 0.1 || trimming.from < 0}
                      onClick={() =>
                        previewing
                          ? stopPreview()
                          : void preview(
                              'trimPreview',
                              trimFields(a.id, durationOf(a)!, trimming),
                              { loop },
                            )
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
                      setTrimming(undefined);
                    }}
                  >
                    Cancel
                  </button>
                </form>
              )}
              {a.variants
                .filter((v) => v.current || v.legacyApproval)
                .map((v) => (
                  <div className="temp-variant" key={v.id}>
                    <span>
                      {v.orientation} ·{' '}
                      {v.permanent
                        ? 'permanent'
                        : v.legacyApproval
                          ? 'accepted — move to permanent storage'
                          : v.state}
                    </span>
                    {host && !v.permanent && v.state === 'ready' && v.nativeLabel && (
                      <button
                        aria-disabled={!!pending || undefined}
                        onClick={() => {
                          if (pending) return;
                          setSaving({ asset: a, variant: v });
                          setCategory('');
                          setDirectory('');
                        }}
                      >
                        Keep {v.orientation} permanently
                      </button>
                    )}
                  </div>
                ))}
              {host && (
                <div className="temp-actions">
                  <button
                    aria-disabled={!!pending || undefined}
                    onClick={() => void run('retryAudio', { assetId: a.id })}
                  >
                    Retry preparation
                  </button>
                  {a.hasTemporary && (
                    <button
                      aria-disabled={!!pending || undefined}
                      onClick={() => {
                        if (
                          !pending &&
                          confirm(
                            `Remove temporary audio for “${a.name}”? Permanent copies and native originals are preserved.`,
                          )
                        )
                          void run('removeAudio', { assetId: a.id });
                      }}
                    >
                      Remove temporary audio
                    </button>
                  )}
                </div>
              )}
              {host && (
                <details>
                  <summary>File locations</summary>
                  {data?.locations
                    ?.filter((l) => l.assetId === a.id)
                    .map((l) => (
                      <p className="temp-path" key={l.variantId}>
                        {l.storage}: {l.path}
                      </p>
                    ))}
                </details>
              )}
            </article>
          ))}
          {!data?.manager.assets.length && (
            <p className="muted">No managed audio yet. Upload a sound or reverse one to start.</p>
          )}
          {data?.manager.issues.map((i) => (
            <p className="warning" key={i.id}>
              {i.message}
              {host && i.code === 'category-duplicates' && (
                <>
                  {' '}
                  <button
                    aria-disabled={!!pending || undefined}
                    onClick={() => {
                      if (
                        !pending &&
                        confirm(
                          'Remove the empty duplicate Temp categories from Soundpad? Only empty categories carrying this app’s tag are removed; your own categories and sounds are not touched. This can take a few minutes on a large library.',
                        )
                      )
                        void run('cleanupCategories');
                    }}
                  >
                    Remove duplicate categories
                  </button>
                </>
              )}
            </p>
          ))}
          {host && (
            <div className="temp-actions">
              <button
                aria-disabled={!!pending || undefined}
                onClick={() => void run('retryCleanup')}
              >
                Retry recovery & cleanup
              </button>
              {supports(state.companionVersion, 'clearTemp') && (
                <button
                  aria-disabled={!!pending || undefined}
                  onClick={() => {
                    if (
                      !pending &&
                      confirm(
                        'Clear all temporary audio now? Reversed sounds, uploads and edited copies are removed from Soundpad and deleted, as when hosting ends. Audio you kept permanently stays. Hosting continues.',
                      )
                    )
                      void run('clearTemp');
                  }}
                >
                  Clear all temporary audio
                </button>
              )}
            </div>
          )}
          {saving && (
            <section className="temp-save" aria-label="Confirm permanent audio save">
              <h3>
                Keep {saving.asset.name} ({saving.variant.orientation})
              </h3>
              <p>
                Creates and verifies a durable file outside session storage and its Soundpad entry
                before retiring the temporary copy.
              </p>
              <label>
                Permanent folder (host only)
                <input
                  placeholder="Default: companion permanent library"
                  value={directory}
                  onChange={(e) => setDirectory(e.target.value)}
                  maxLength={1024}
                />
              </label>
              <label>
                Permanent category
                <select value={category} onChange={(e) => setCategory(e.target.value)}>
                  <option value="">App-managed Saved category</option>
                  {categories
                    .filter(
                      (c) =>
                        /^\d+$/.test(c.category.id) &&
                        !/^All$|^Temp .{1,3} Soundpad Web \[/.test(c.category.name), // "-" since 2.15.9; "·" or garbled before
                    )
                    .map((c) => (
                      <option key={c.category.id} value={c.category.id}>
                        {c.category.name}
                      </option>
                    ))}
                </select>
              </label>
              <div className="temp-actions">
                <button
                  aria-disabled={!!pending || undefined}
                  onClick={() => {
                    const chosen = categories.find((c) => c.category.id === category)?.category;
                    void run('keepAudio', {
                      assetId: saving.asset.id,
                      variantId: saving.variant.id,
                      confirmed: true,
                      destinationDirectory: directory || undefined,
                      ...(chosen
                        ? { categoryId: Number(chosen.id), categoryName: chosen.name }
                        : {}),
                    });
                  }}
                >
                  Confirm permanent save
                </button>
                <button
                  aria-disabled={!!pending || undefined}
                  onClick={() => !pending && setSaving(undefined)}
                >
                  Cancel
                </button>
              </div>
            </section>
          )}
        </div>
      )}
    </details>
  );
}
