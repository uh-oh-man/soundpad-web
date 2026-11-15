import { useCallback, useEffect, useState } from 'react';
import { RotateCcw, X } from 'lucide-react';
import { useStageH } from '../../stage-h';
import {
  applyLayout,
  backupCommand,
  detailSchema,
  kindLabel,
  layoutSchema,
  restoreMessage,
  restoreSchema,
  when,
  type BackupDetail,
  type RestoreKind,
  type RestoreResult,
} from '../../backups';
import { refreshLibrary } from '../../library/voice-store';
import { refreshSentences } from '../../library/sentence-store';

type Tab = 'clips' | 'recordings' | 'voices' | 'sentences' | 'layouts' | 'library';
const TABS: [Tab, string][] = [
  ['clips', 'Clips'],
  ['recordings', 'Recordings'],
  ['voices', 'Voices'],
  ['sentences', 'Sentences'],
  ['layouts', 'Layouts'],
  ['library', 'Whole library'],
];
const KIND: Record<Exclude<Tab, 'layouts' | 'library'>, RestoreKind> = {
  clips: 'clip',
  recordings: 'recording',
  voices: 'voice',
  sentences: 'sentence',
};

/**
 * One backup (Stage H, 2.44): what it holds, each item's status against now, and Restore. Recordings,
 * voices and sentences come back as one action with Undo; the whole library needs a confirmation.
 */
export function BackupDetailView({ id, close }: { id: string; close: () => void }) {
  const { controller, notify } = useStageH();
  const [detail, setDetail] = useState<BackupDetail | null>(null);
  const [tab, setTab] = useState<Tab>('recordings');
  const [busy, setBusy] = useState<string | null>(null);
  const [result, setResult] = useState<RestoreResult | null>(null);
  const [confirming, setConfirming] = useState(false);
  const [adopt, setAdopt] = useState(false);
  const [error, setError] = useState('');
  const load = useCallback(async () => {
    try {
      setDetail(
        await backupCommand('backupDetail', { snapshot: id }, detailSchema, { quiet: true }),
      );
      setError('');
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }, [id]);
  useEffect(() => {
    void load();
  }, [load]);
  const restore = (kind: RestoreKind, ids?: string[]) => {
    if (busy) return; // aria-disabled while a restore runs
    setBusy(ids?.[0] ?? kind);
    void backupCommand(
      'backupRestore',
      {
        snapshot: id,
        kind,
        ...(ids && { ids }),
        ...(kind === 'library' && adopt && { adoptIdentity: true }),
      },
      restoreSchema,
    )
      .then(async (r) => {
        setResult(r);
        setConfirming(false);
        const message = restoreMessage(kind, r);
        if (r.auditGroup)
          notify(message, 'success', {
            label: 'Undo',
            run: () =>
              void controller
                .run('libraryUndo', { auditId: r.auditGroup! })
                .then(() => {
                  notify('Undone.', 'success');
                  return Promise.all([refreshLibrary(), load()]);
                })
                .catch(() => {}),
          });
        else if (r.undoToken)
          notify(message, 'success', {
            label: 'Undo',
            run: () =>
              void backupCommand(
                'backupRestore',
                { kind: 'sentence', undo: r.undoToken! },
                restoreSchema,
              )
                .then(() => {
                  notify('Undone.', 'success');
                  return Promise.all([refreshSentences(), load()]);
                })
                .catch(() => {}),
          });
        else notify(message, r.conflicts.length > 0 ? 'info' : 'success');
        await Promise.all([refreshLibrary(), refreshSentences(), load()]);
      })
      .catch(() => {})
      .finally(() => setBusy(null));
  };
  const restoreLayout = (source: 'window' | 'browser', mode: 'replace' | 'merge') => {
    if (busy) return;
    setBusy(source);
    void backupCommand('backupLayout', { snapshot: id, source }, layoutSchema)
      .then(({ workspace }) => {
        const previous = applyLayout(workspace, mode);
        notify(
          mode === 'replace'
            ? 'Your layout was replaced with the backup’s.'
            : 'The backup’s layout was merged into yours.',
          'success',
          {
            label: 'Undo',
            run: () => {
              try {
                applyLayout(previous, 'replace');
                notify('Undone.', 'success');
              } catch (e) {
                notify(e instanceof Error ? e.message : String(e), 'error');
              }
            },
          },
        );
      })
      .catch((e) => notify(e instanceof Error ? e.message : String(e), 'error'))
      .finally(() => setBusy(null));
  };
  const label = detail
    ? `${kindLabel(detail.kind)} backup, ${when(detail.createdUtcMs)}`
    : 'Backup';
  return (
    <div className="backup-detail" role="region" aria-label={label}>
      <div className="backup-detail-head">
        <strong>{label}</strong>
        <button aria-label="Close backup" onClick={close}>
          <X size={15} />
        </button>
      </div>
      {error && <p className="inline-error">{error}</p>}
      {detail && (
        <>
          {detail.fuzzy && (
            <p className="muted">
              The library kept changing while this backup was made; some files may be from moments
              apart.
            </p>
          )}
          <div className="backup-tabs" role="tablist" aria-label="What to restore">
            {TABS.map(([key, text]) => (
              <button
                key={key}
                role="tab"
                aria-selected={tab === key}
                aria-controls={`backup-${id}-${key}`}
                onClick={() => setTab(key)}
              >
                {text}
              </button>
            ))}
          </div>
          <div
            className="backup-panel"
            role="tabpanel"
            id={`backup-${id}-${tab}`}
            aria-label={TABS.find(([k]) => k === tab)![1]}
          >
            {tab === 'layouts' ? (
              detail.layouts.length === 0 ? (
                <p className="muted">No layout in this backup.</p>
              ) : (
                <ul className="backup-items">
                  {detail.layouts.map((l) => (
                    <li key={l.id}>
                      <span>{l.name}</span>
                      <span className="backup-state">{l.status}</span>
                      <button
                        aria-disabled={!!busy || undefined}
                        onClick={() => restoreLayout(l.id as 'window' | 'browser', 'merge')}
                      >
                        Merge into my layout
                      </button>
                      <button
                        aria-disabled={!!busy || undefined}
                        onClick={() => restoreLayout(l.id as 'window' | 'browser', 'replace')}
                      >
                        Replace my layout
                      </button>
                    </li>
                  ))}
                </ul>
              )
            ) : tab === 'library' ? (
              <WholeLibrary
                detail={detail}
                confirming={confirming}
                adopt={adopt}
                busy={!!busy}
                setAdopt={setAdopt}
                ask={() => setConfirming(true)}
                cancel={() => setConfirming(false)}
                restore={() => restore('library')}
              />
            ) : detail[tab].length === 0 ? (
              <p className="muted">None in this backup.</p>
            ) : (
              <ul
                className="backup-items"
                aria-label={`${TABS.find(([k]) => k === tab)![1]} in this backup`}
              >
                {detail[tab].map((entry) => (
                  <li key={entry.id}>
                    <span>{entry.name}</span>
                    <span className="backup-state">{entry.status}</span>
                    {entry.status !== 'same' && (
                      <button
                        aria-label={`Restore ${entry.name}`}
                        aria-disabled={!!busy || undefined}
                        onClick={() => restore(KIND[tab], [entry.id])}
                      >
                        <RotateCcw size={14} /> {busy === entry.id ? 'Restoring…' : 'Restore'}
                      </button>
                    )}
                  </li>
                ))}
              </ul>
            )}
          </div>
          {result &&
            (result.conflicts.length > 0 ||
              result.attention.length > 0 ||
              result.warnings.length > 0) && (
              <div className="backup-result" role="status">
                {result.attention.map((a) => (
                  <p key={a.path}>
                    {a.message} <code>{a.path}</code>
                  </p>
                ))}
                {[...result.conflicts, ...result.warnings].map((line) => (
                  <p key={line}>{line}</p>
                ))}
              </div>
            )}
        </>
      )}
    </div>
  );
}

function WholeLibrary({
  detail,
  confirming,
  adopt,
  busy,
  setAdopt,
  ask,
  cancel,
  restore,
}: {
  detail: BackupDetail;
  confirming: boolean;
  adopt: boolean;
  busy: boolean;
  setAdopt: (on: boolean) => void;
  ask: () => void;
  cancel: () => void;
  restore: () => void;
}) {
  const l = detail.library;
  return (
    <div className="backup-whole">
      <p>
        {l.recordings} recordings, {l.voices} voices, {l.sentences} sentences, {l.clips} kept clips,{' '}
        {l.icons} icons
        {l.counter !== null && `, Kick ${l.counter}`}.
      </p>
      {!confirming ? (
        <button onClick={ask}>Restore the whole library…</button>
      ) : (
        <div className="backup-confirm" role="alertdialog" aria-label="Restore the whole library">
          <p>
            This replaces your voice library, sentences, statistics, icons, Kick count, guest
            permissions and settings with the backup&apos;s. Kept audio that is missing comes back;
            nothing that is there now is overwritten.
          </p>
          <p>A “Before restore” backup of how things are now is made first.</p>
          <p>Stop playback first: nothing plays while the library is restored.</p>
          {l.identity === 'different' && (
            <label>
              <input type="checkbox" checked={adopt} onChange={(e) => setAdopt(e.target.checked)} />{' '}
              Use this backup&apos;s identity (guests&apos; saved layouts move with it)
            </label>
          )}
          <div className="backup-confirm-actions">
            <button className="primary" aria-disabled={busy || undefined} onClick={restore}>
              {busy ? 'Restoring…' : 'Restore the whole library'}
            </button>
            <button onClick={cancel}>Cancel</button>
          </div>
        </div>
      )}
    </div>
  );
}
