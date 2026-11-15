import { useCallback, useEffect, useRef, useState } from 'react';
import { Archive, FolderCheck, History } from 'lucide-react';
import { z } from 'zod';
import { useStageH } from '../../stage-h';
import { supports } from '../../version';
import {
  backupCommand,
  formatBytes,
  kindLabel,
  listSchema,
  statusLine,
  statusSchema,
  when,
  type BackupEntry,
  type BackupStatus,
} from '../../backups';
import { BackupDetailView } from '../backups/BackupDetail';

type Draft = {
  folder: string;
  keepCount: number;
  keepDays: number;
  intervalHours: number;
  afterChangeMinutes: number;
  includeNativeFiles: boolean;
};
const draftOf = (s: BackupStatus): Draft => ({
  folder: s.settings.folder ?? '',
  keepCount: s.settings.keepCount,
  keepDays: s.settings.keepDays,
  intervalHours: s.settings.intervalHours,
  afterChangeMinutes: s.settings.afterChangeMinutes,
  includeNativeFiles: s.settings.includeNativeFiles,
});
const whole = (value: string, min: number, max: number, fallback: number) => {
  const n = z.coerce.number().int().min(min).max(max).safeParse(value);
  return n.success ? n.data : fallback;
};

/**
 * Settings → Backups (Stage H, 2.44, gate backups). Host only, never in the example. Off until the host
 * chooses a folder; then the companion backs up on its schedule, and Back up now makes one at once.
 * The list opens a backup's detail, where single items or the whole library can be restored.
 */
export function BackupsSection() {
  const { state, mode, demo } = useStageH();
  const show = mode === 'host' && !demo && supports(state.companionVersion, 'backups');
  if (!show) return null;
  return <Backups revision={state.backups ?? 0} />;
}

function Backups({ revision }: { revision: number }) {
  const [status, setStatus] = useState<BackupStatus | null>(null);
  const [draft, setDraft] = useState<Draft | null>(null);
  const [list, setList] = useState<BackupEntry[]>([]);
  const [saving, setSaving] = useState(false);
  const [starting, setStarting] = useState(false);
  const [open, setOpen] = useState<string | null>(null);
  const [error, setError] = useState('');
  const edited = useRef(false);
  const load = useCallback(async () => {
    try {
      const next = await backupCommand('backupSettings', { action: 'get' }, statusSchema, {
        quiet: true,
      });
      setStatus(next);
      if (!edited.current) setDraft(draftOf(next));
      setList(
        next.settings.folder
          ? (await backupCommand('backupList', { limit: 50 }, listSchema, { quiet: true }))
              .snapshots
          : [],
      );
      setError('');
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }, []);
  // The companion's revision rises when the settings, a run or a restore changed.
  useEffect(() => {
    void load();
  }, [load, revision]);
  const running = !!status?.status.running || starting;
  if (!status || !draft)
    return (
      <section className="backups-setting" aria-label="Backups">
        <h3>Backups</h3>
        <p className="muted">{error || 'Loading…'}</p>
      </section>
    );
  const save = (fields: { useSuggested?: true }) => {
    if (saving) return;
    setSaving(true);
    void backupCommand(
      'backupSettings',
      {
        action: 'set',
        ...(fields.useSuggested ? { useSuggested: true } : { folder: draft.folder.trim() }),
        keepCount: draft.keepCount,
        keepDays: draft.keepDays,
        intervalHours: draft.intervalHours,
        afterChangeMinutes: draft.afterChangeMinutes,
        includeNativeFiles: draft.includeNativeFiles,
      },
      statusSchema,
    )
      .then((next) => {
        edited.current = false;
        setStatus(next);
        setDraft(draftOf(next));
        return load();
      })
      .catch(() => {})
      .finally(() => setSaving(false));
  };
  const backUpNow = () => {
    if (running) return; // aria-disabled: the button keeps focus and ignores presses meanwhile
    setStarting(true);
    void backupCommand('backupNow', {}, z.object({ started: z.boolean() }))
      .then(({ started }) => {
        if (!started) setStarting(false);
        return load();
      })
      .catch(() => {})
      .finally(() => setStarting(false));
  };
  const change = (next: Partial<Draft>) => {
    edited.current = true;
    setDraft({ ...draft, ...next });
  };
  return (
    <section className="backups-setting" aria-label="Backups">
      <h3>Backups</h3>
      <p className="backups-status" role="status">
        {statusLine(status)}
      </p>
      {status.settings.problem && <p className="inline-error">{status.settings.problem}</p>}
      <form
        className="backups-form"
        aria-label="Backup settings"
        onSubmit={(e) => {
          e.preventDefault();
          save({});
        }}
      >
        <label>
          Backup folder
          <input
            value={draft.folder}
            maxLength={1024}
            placeholder="Off. Type a folder such as D:\Backups"
            onChange={(e) => change({ folder: e.target.value })}
          />
        </label>
        <div className="backups-suggested">
          <button type="button" onClick={() => save({ useSuggested: true })}>
            <FolderCheck size={15} /> Use suggested folder
          </button>
          <small>{status.settings.suggested}</small>
        </div>
        <fieldset className="backups-keep">
          <legend>Keep</legend>
          <label>
            Newest backups
            <input
              type="number"
              min={1}
              max={200}
              value={draft.keepCount}
              onChange={(e) =>
                change({ keepCount: whole(e.target.value, 1, 200, draft.keepCount) })
              }
            />
          </label>
          <label>
            Everything from the last (days)
            <input
              type="number"
              min={1}
              max={3650}
              value={draft.keepDays}
              onChange={(e) => change({ keepDays: whole(e.target.value, 1, 3650, draft.keepDays) })}
            />
          </label>
          <small>And one a week before that.</small>
        </fieldset>
        <fieldset className="backups-keep">
          <legend>Back up</legend>
          <label>
            Every (hours)
            <input
              type="number"
              min={1}
              max={168}
              value={draft.intervalHours}
              onChange={(e) =>
                change({ intervalHours: whole(e.target.value, 1, 168, draft.intervalHours) })
              }
            />
          </label>
          <label>
            Minutes after kept sounds change (0: off)
            <input
              type="number"
              min={0}
              max={1440}
              value={draft.afterChangeMinutes}
              onChange={(e) =>
                change({
                  afterChangeMinutes: whole(e.target.value, 0, 1440, draft.afterChangeMinutes),
                })
              }
            />
          </label>
        </fieldset>
        <label className="backups-native">
          <input
            type="checkbox"
            checked={draft.includeNativeFiles}
            onChange={(e) => change({ includeNativeFiles: e.target.checked })}
          />
          Include Soundpad files used by the voice library (
          {formatBytes(status.settings.nativeBytes)})
        </label>
        <button type="submit" aria-disabled={saving || undefined}>
          Save backup settings
        </button>
      </form>
      {status.settings.folder && (
        <button className="backups-now" aria-disabled={running || undefined} onClick={backUpNow}>
          <Archive size={15} /> {running ? 'Backing up…' : 'Back up now'}
        </button>
      )}
      <p className="muted">
        Backups contain your companion&apos;s identity. Keep the folder private; don&apos;t share
        it.
      </p>
      {error && <p className="inline-error">{error}</p>}
      {list.length > 0 && (
        <>
          <h4>
            <History size={14} /> Your backups
          </h4>
          <ul className="backups-list" aria-label="Backups list">
            {list.map((b) => (
              <li key={b.id}>
                <button
                  aria-expanded={open === b.id}
                  disabled={b.kind === 'unreadable'}
                  onClick={() => setOpen(open === b.id ? null : b.id)}
                >
                  <strong>{kindLabel(b.kind)}</strong>
                  <span>{when(b.createdUtcMs)}</span>
                  <span>{formatBytes(b.bytes)}</span>
                  {b.other && <span className="backups-tag">another install</span>}
                  {b.fuzzy && <span className="backups-tag">taken while things changed</span>}
                </button>
                {b.kind === 'unreadable' && <small>{b.warnings[0]}</small>}
                {open === b.id && <BackupDetailView id={b.id} close={() => setOpen(null)} />}
              </li>
            ))}
          </ul>
        </>
      )}
    </section>
  );
}
