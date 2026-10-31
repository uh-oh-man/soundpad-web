import { useEffect, useState } from 'react';
import { z } from 'zod';
import { controller } from '../controller';
import { flattenCategories, type Snapshot } from '../protocol';
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
} as const;
const effectSuffix = { chipmunk: 'chipmunk', slowmo: 'slow-mo', echo: 'echo' } as const;
type Effect = keyof typeof effects;

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
  }>();
  const [refresh, setRefresh] = useState(0);
  useEffect(() => {
    if (!connected) return;
    let live = true;
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
    return () => {
      live = false;
    };
  }, [connected, state.managedAudioVersion, refresh]);
  async function run(
    op: 'audioPlay' | 'keepAudio' | 'removeAudio' | 'retryAudio' | 'retryCleanup' | 'trimAudio',
    fields: Parameters<typeof controller.run>[1] = {},
  ) {
    setPending(fields.assetId ?? 'manager');
    setError('');
    try {
      await controller.run(op, fields);
      if (op === 'keepAudio') {
        setSaving(undefined);
        controller.notify('Permanent file and Soundpad entry verified.', 'success');
      }
      if (op === 'trimAudio') {
        setTrimming(undefined);
        controller.notify('Copy added to Temp. The original is unchanged.', 'success');
      }
      setRefresh((n) => n + 1);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Temp action failed.');
    } finally {
      setPending('');
    }
  }
  /** Length of a source's normal variant, from the native library entry Soundpad reports. */
  const durationOf = (a: { normal?: { soundId?: string | null } | null }) =>
    state.library.sounds.find((s) => s.id === a.normal?.soundId)?.durationMs ?? undefined;
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
          {data?.manager.assets
            .filter(
              (a) =>
                a.state !== 'removed' &&
                (a.origin === 'upload' || a.variants.some((v) => v.orientation === 'reversed')),
            )
            .map((a) => (
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
                      disabled={
                        !!pending ||
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
                    durationOf(a) !== undefined && (
                      <button
                        disabled={!!pending || !connected}
                        onClick={() =>
                          setTrimming({
                            id: a.id,
                            from: 0,
                            to: durationOf(a)! / 1000,
                            name: '',
                            effect: 'none',
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
                      const length = durationOf(a)!;
                      void run('trimAudio', {
                        assetId: a.id,
                        positionMs: Math.round(Math.min(Math.max(0, trimming.from * 1000), length)),
                        endMs: Math.round(Math.min(Math.max(0, trimming.to * 1000), length)),
                        action: trimming.effect,
                        ...(trimming.name.trim() && { name: trimming.name.trim() }),
                      });
                    }}
                  >
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
                    <label>
                      Effect
                      <select
                        value={trimming.effect}
                        onChange={(e) =>
                          setTrimming({ ...trimming, effect: e.target.value as Effect })
                        }
                      >
                        {Object.entries(effects).map(([value, label]) => (
                          <option key={value} value={value}>
                            {label}
                          </option>
                        ))}
                      </select>
                    </label>
                    <label>
                      Name
                      <input
                        maxLength={100}
                        placeholder={`${a.name} (${trimming.effect === 'none' ? 'trimmed' : effectSuffix[trimming.effect]})`}
                        value={trimming.name}
                        onChange={(e) => setTrimming({ ...trimming, name: e.target.value })}
                      />
                    </label>
                    <span className="muted">
                      {Math.max(0, trimming.to - trimming.from).toFixed(2)} s of{' '}
                      {(durationOf(a)! / 1000).toFixed(2)} s · saved as a new Temp sound
                    </span>
                    <button
                      className="primary"
                      type="submit"
                      disabled={!!pending || trimming.to - trimming.from < 0.1 || trimming.from < 0}
                    >
                      Save copy
                    </button>
                    <button type="button" onClick={() => setTrimming(undefined)}>
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
                          disabled={!!pending}
                          onClick={() => {
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
                      disabled={!!pending}
                      onClick={() => void run('retryAudio', { assetId: a.id })}
                    >
                      Retry preparation
                    </button>
                    {a.hasTemporary && (
                      <button
                        disabled={!!pending}
                        onClick={() => {
                          if (
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
                    {data.locations
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
            </p>
          ))}
          {host && (
            <button disabled={!!pending} onClick={() => void run('retryCleanup')}>
              Retry recovery & cleanup
            </button>
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
                        !/^All$|^Temp · Soundpad Web/.test(c.category.name),
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
                  disabled={!!pending}
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
                <button disabled={!!pending} onClick={() => setSaving(undefined)}>
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
