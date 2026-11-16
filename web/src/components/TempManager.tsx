import { useEffect, useState } from 'react';
import { z } from 'zod';
import { controller } from '../controller';
import { supports } from '../version';
import { flattenCategories, type Snapshot } from '../protocol';
import { onTempRequest, takeTempRequest, type TempRequest } from '../edit-copy';
import { guestHeld, HELD_BY_HOST, intentKey, playback, usePendingIntent } from '../playback';
import { recall, remember } from '../sections';
import { useDraftStore } from '../mimic/draft-store';
import { formatMb } from '../mimic/drafts';
import { refreshTempAssets, useTempAssets, type TempAsset, type TempVariant } from '../temp-assets';
import { requestEditor } from '../shell';
import { HostOnlyTag } from './SimulatedBadge';
type Asset = TempAsset;
type Variant = TempVariant;

export function TempManager({
  state,
  host,
  connected,
}: {
  state: Snapshot;
  host: boolean;
  connected: boolean;
}) {
  // Temp remembers whether it was open and what was searched (2.40), across a reload too.
  // A Keep asked for from Mimic before the list was on the page opens it too.
  const [wanted, setWanted] = useState<(TempRequest & { at: number }) | undefined>(() => {
    const waiting = takeTempRequest();
    return waiting ? { ...waiting, at: Date.now() } : undefined;
  });
  const [open, setOpen] = useState(() => !!wanted || recall('temp').open === true),
    [error, setError] = useState(''),
    [pending, setPending] = useState('');
  // The list is fetched once for the page (2.43: the copy editor in the panel reads it too). An error from
  // loading it is shown until a later load works; one from an action the person took stays until their
  // next action, so a refresh landing just after a refused Keep never hides the refusal.
  const { data, loadError } = useTempAssets();
  const [saving, setSaving] = useState<{ asset: Asset; variant: Variant }>(),
    [directory, setDirectory] = useState(''),
    [category, setCategory] = useState('');
  // Search (2.25), like the Voice Replay Studio list: a long session's Temp holds many sources.
  const [find, setFind] = useState(() => recall('temp').find ?? '');
  useEffect(() => remember('temp', { open, find }), [open, find]);
  // Guest Keep and storage (2.40): a guest keeps clips into the host's destination, within their budget,
  // and only while connected; they may remove their own Temp clips.
  const drafts = supports(state.companionVersion, 'drafts') && (host || controller.relaysDrafts());
  const { budget } = useDraftStore();
  const own = !host ? budget?.guest?.own : undefined;
  const guestKeeps = !host && drafts && state.permissions.guestKeep === true;
  // The voice library (2.41): a kept clip can be added by hand (idempotent: one recording per file).
  const libraryAdds =
    supports(state.companionVersion, 'voiceLibrary') &&
    (host || (controller.relaysLibrary() && state.permissions.guestLibraryEdit !== false));
  async function addToLibrary(soundId: string, name: string) {
    try {
      const result = z
        .object({ recordingId: z.string(), added: z.boolean() })
        .parse(await controller.run('libraryAdd', { soundId }));
      controller.notify(
        result.added
          ? `“${name}” is in the voice library; its words are found in the background.`
          : `“${name}” was already in the voice library.`,
        'success',
      );
    } catch {
      /* Controller shows the error. */
    }
  }
  const me = controller.getSnapshot().profile.name;
  const demo = controller.getSnapshot().demo;
  const keepBlocked = !connected
    ? 'Reconnecting: Keep waits until you are connected again.'
    : own?.waiting === 'kept'
      ? 'Waiting for the host to allow more kept audio.'
      : '';
  // A clip whose Keep (2.26) was asked for from Mimic (`wanted`) opens once Temp lists it. (Edit copy opens
  // in the editor panel since 2.43.) It is dropped if it can't be met within 20 s (2.29.3), so it never
  // opens long after the press.
  // Play buttons answer to the playback path, not to `pending` (2.35): a slow Save copy or Keep must
  // not hold up playing something.
  const intent = usePendingIntent();
  const held = guestHeld(state, host);
  useEffect(
    () =>
      onTempRequest((request) => {
        if (request.kind !== 'keep') return;
        setOpen(true);
        setWanted({ ...request, at: Date.now() });
      }),
    [],
  );
  async function run(
    op:
      | 'keepAudio'
      | 'removeAudio'
      | 'retryAudio'
      | 'retryCleanup'
      | 'cleanupCategories'
      | 'clearTemp',
    fields: Parameters<typeof controller.run>[1] = {},
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
        controller.notify(
          host
            ? 'Permanent file and Soundpad entry verified.'
            : 'Kept: it is in the host\u2019s Soundpad for good.',
          'success',
        );
      }
      refreshTempAssets();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Temp action failed.');
    } finally {
      setPending('');
    }
  }
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
  });
  const listed = (data?.manager.assets ?? []).filter(
    (a) =>
      a.state !== 'removed' &&
      (a.origin === 'upload' || a.variants.some((v) => v.orientation === 'reversed')),
  );
  const findWords = listed.length > 6 ? find.trim().toLowerCase() : '';
  const shown = listed.filter(
    (a) => !findWords || `${a.name} ${a.uploader}`.toLowerCase().includes(findWords),
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
      <summary title="Temporary session audio: sources and size">
        Temp{' '}
        <small>
          ·{' '}
          {data?.manager.assets.filter(
            (a) => a.hasTemporary || a.state === 'accepted-needs-migration',
          ).length ?? 0}
          <span className="sr-only"> sources</span> ·{' '}
          {((data?.manager.temporaryBytes ?? 0) / 1048576).toFixed(1)} MiB
          {data?.manager.issues.length ? ' · needs attention' : ''}
        </small>
      </summary>
      {open && (
        <div className="temp-manager-body">
          <p className="muted">
            Session audio uses Soundpad’s normal mic playback.{' '}
            {guestKeeps
              ? 'You can keep clips permanently; they go where the host chose.'
              : drafts && state.permissions.guestKeep
                ? 'The host and guests the host allows can save a permanent copy.'
                : 'Only the host can save a permanent copy.'}{' '}
            {drafts && 'Temp stays until the companion quits.'}
          </p>
          {own && (
            <p className={own.paused ? 'warning' : 'muted'} role="status">
              {supports(state.companionVersion, 'noLimits') ? (
                // 2.45: no storage limits; usage only.
                <>
                  Your Temp: {formatMb(own.tempBytes)} · kept {formatMb(own.keptBytes)}
                </>
              ) : (
                <>
                  Your Temp: {formatMb(own.tempBytes, own.tempLimitBytes)} · kept{' '}
                  {formatMb(own.keptBytes, own.keptLimitBytes)}
                  {own.waiting ? ' · waiting for the host to allow more' : ''}
                </>
              )}
            </p>
          )}
          {(error || loadError) && (
            <p className="warning" role="alert">
              {error || loadError}
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
                {(['normal', 'reversed'] as const).map((orientation) => {
                  const starting = intent === intentKey.asset(a.id, orientation);
                  return (
                    <button
                      key={orientation}
                      // Busy only while its own start is unanswered; held while the host's Panic lasts.
                      aria-disabled={starting || held || undefined}
                      aria-busy={starting || undefined}
                      data-intent={starting ? 'pending' : undefined}
                      title={held ? HELD_BY_HOST : undefined}
                      disabled={
                        !state.available ||
                        !connected ||
                        (!host && !state.permissions.remotePlayback) ||
                        (!host &&
                          !a[orientation]?.ready &&
                          state.permissions.guestTransforms === false)
                      }
                      onClick={() => {
                        if (starting || held) return;
                        void playback.audioPlay(a.id, orientation).then(() => refreshTempAssets());
                      }}
                    >
                      Play {orientation}
                    </button>
                  );
                })}
                {(host || state.permissions.guestUploads !== false) &&
                  durationOf(a) !== undefined &&
                  supports(state.companionVersion, 'trim') && (
                    <button
                      disabled={!connected}
                      aria-haspopup="dialog"
                      onClick={() =>
                        // The copy editor opens in the editor panel (2.43); the list stays as it is.
                        requestEditor({ kind: 'temp-edit', sourceId: a.id })
                      }
                    >
                      Edit copy
                    </button>
                  )}
              </div>
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
                    {guestKeeps && !v.permanent && v.state === 'ready' && v.nativeLabel && (
                      <button
                        aria-disabled={!!pending || !!keepBlocked || undefined}
                        title={keepBlocked || 'Kept into the host\u2019s chosen folder'}
                        onClick={() => {
                          if (pending || keepBlocked) return;
                          void run('keepAudio', { assetId: a.id, variantId: v.id });
                        }}
                      >
                        Keep {v.orientation} permanently
                      </button>
                    )}
                    {host && !v.permanent && v.state === 'ready' && v.nativeLabel && (
                      <>
                        <button
                          aria-disabled={!!pending || undefined}
                          aria-describedby={demo ? `keep-host-only-${v.id}` : undefined}
                          onClick={() => {
                            if (pending) return;
                            setSaving({ asset: a, variant: v });
                            setCategory('');
                            setDirectory('');
                          }}
                        >
                          Keep {v.orientation} permanently
                        </button>
                        {demo && <HostOnlyTag id={`keep-host-only-${v.id}`} />}
                      </>
                    )}
                    {/* 2.41: clips kept before the voice library existed are added by hand (no automatic migration). */}
                    {libraryAdds && v.permanent && v.soundId && (
                      <button
                        aria-disabled={!!pending || undefined}
                        title="Make it a voice-library recording: its words are found in the background"
                        onClick={() => {
                          if (pending) return;
                          void addToLibrary(v.soundId!, a.name);
                        }}
                      >
                        Add to voice library
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
              {!host &&
                drafts &&
                a.hasTemporary &&
                a.uploader === me &&
                state.permissions.guestUploads !== false && (
                  <div className="temp-actions">
                    <button
                      aria-disabled={!!pending || undefined}
                      onClick={() => {
                        if (
                          !pending &&
                          confirm(`Remove your clip \u201c${a.name}\u201d from Temp?`)
                        )
                          void run('removeAudio', { assetId: a.id, action: 'stopIfPlaying' });
                      }}
                    >
                      Remove my clip
                    </button>
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
                        'Clear all temporary audio now? Reversed sounds, uploads and edited copies are removed from Soundpad and deleted. Audio you kept permanently stays. Hosting continues.',
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
