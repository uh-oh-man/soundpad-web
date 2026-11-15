import { useEffect, useRef, useState, useSyncExternalStore } from 'react';
import { Download, X } from 'lucide-react';
import { z } from 'zod';
import { controller } from '../../controller';
import { supports } from '../../version';
import {
  chipShown,
  dismissedVersion,
  downloadUrl,
  openUpdateDialog,
  rememberDismissed,
  shouldOffer,
  updateBodies,
  updateCopy,
  useUpdateDialog,
  type UpdateOp,
} from '../../updates';
import { Modal } from '../Modal';
import { PROJECT_URL } from '../shell/Landing';

const projectUrl = () => (import.meta.env.VITE_REPOSITORY_URL as string | undefined) || PROJECT_URL;

/** Runs an update op with its checked body; a refusal is shown as a notice. */
export function runUpdate<Op extends UpdateOp>(op: Op, body: z.input<(typeof updateBodies)[Op]>) {
  return controller.run(op, { body: updateBodies[op].parse(body) });
}

/** The update state of a host page on a companion with the updater; undefined otherwise. */
export function useUpdate() {
  const view = useSyncExternalStore(controller.subscribe, controller.getSnapshot);
  const host = view.mode === 'host' && !view.demo;
  const update =
    host && supports(view.state.companionVersion, 'updater') ? view.state.update : undefined;
  return { view, host, update };
}

/**
 * The launch offer (2.44, gate updater): "Soundpad Web 2.44.1 is available. Update now / Later /
 * What's new", once per launch. Later (or dismissing it) tells the companion, so the header chip and
 * Settings carry the offer from then on. Never installs by itself.
 */
export function UpdateOffer() {
  const { view, host, update } = useUpdate();
  const [dismissed, setDismissed] = useState(dismissedVersion);
  if (!update || !shouldOffer(update, host, view.demo, dismissed)) return null;
  const later = () => {
    rememberDismissed(update.latest);
    setDismissed(update.latest ?? null);
    void runUpdate('updateLater', {}).catch(() => {});
  };
  return (
    <aside className="update-offer" role="region" aria-label="Update available">
      <Download size={18} aria-hidden="true" />
      <p>
        Soundpad Web <strong>{update.latest}</strong> is available.
      </p>
      <div className="update-offer-actions">
        <button className="primary" onClick={() => openUpdateDialog()}>
          Update now
        </button>
        <button onClick={later}>Later</button>
        <a href={downloadUrl(update, projectUrl())} target="_blank" rel="noreferrer">
          What's new
        </a>
      </div>
      <button className="icon-button" aria-label="Dismiss update offer" onClick={later}>
        <X size={14} />
      </button>
    </aside>
  );
}

/** The header chip "Update" (after Later, while the update is there to take). */
export function UpdateChip() {
  const { view, host, update } = useUpdate();
  if (!chipShown(update, host, view.demo)) return null;
  return (
    <button
      type="button"
      className="update-chip"
      title={`Update Soundpad Web to ${update!.latest}`}
      onClick={() => openUpdateDialog()}
    >
      <Download size={15} aria-hidden="true" /> Update
    </button>
  );
}

const backupSettingsSchema = z.object({
  settings: z.object({ folder: z.string().nullable().optional() }).passthrough(),
});

/** Whether a backup folder is set (2.44, gate backups), for the dialog's first line. Unknown counts as not set. */
function useBackupFolder(open: boolean, version: string | null | undefined) {
  const [set, setSet] = useState<boolean | null>(null);
  useEffect(() => {
    if (!open || !supports(version, 'backups')) return;
    let live = true;
    void controller
      .run('backupSettings', { body: { action: 'get' } }, { quiet: true })
      .then((raw) => {
        const parsed = backupSettingsSchema.safeParse(raw);
        if (live) setSet(parsed.success && !!parsed.data.settings.folder);
      })
      .catch(() => live && setSet(false));
    return () => {
      live = false;
    };
  }, [open, version]);
  return set === true;
}

/**
 * Update now (2.44): what happens, plainly and with no promised duration, then progress in place.
 * The button stays focusable while the update runs (aria-disabled), and presses are ignored then.
 */
export function UpdateDialogHost() {
  const open = useUpdateDialog();
  const { view, update } = useUpdate();
  const backup = useBackupFolder(open, view.state.companionVersion);
  const [sending, setSending] = useState(false);
  const live = useRef(true);
  useEffect(
    () => () => {
      live.current = false;
    },
    [],
  );
  if (!open) return null;
  const close = () => openUpdateDialog(false);
  if (!update) {
    // The companion went away (it is restarting) or lost the updater: the status line says so.
    return (
      <Modal label="Update Soundpad Web" onClose={close}>
        <p role="status">{view.status}</p>
      </Modal>
    );
  }
  const copy = updateCopy(update);
  const busy = copy.busy || sending;
  const install = () => {
    if (busy || !copy.installable || !update.latest) return;
    setSending(true);
    void runUpdate('updateInstall', { version: update.latest, confirm: true })
      .catch(() => {})
      .finally(() => live.current && setSending(false));
  };
  const percent =
    copy.progress?.total && copy.progress.total > 0
      ? Math.min(100, Math.round((copy.progress.bytes / copy.progress.total) * 100))
      : undefined;
  return (
    <Modal
      label={`Update Soundpad Web${update.latest ? ` to ${update.latest}` : ''}`}
      onClose={close}
    >
      <div className="update-dialog">
        <p>
          {backup
            ? 'A backup is made first.'
            : 'No backup folder is set, so no backup is made first.'}
        </p>
        <p>
          The companion closes and opens again by itself. Recording pauses and the gap is marked on
          the timeline. Guests reconnect by themselves with the same invitation. Temp, drafts and
          history stay. Sounds already playing in Soundpad are not stopped by the update.
        </p>
        <p>If the new version doesn't start, the current one comes back.</p>
        <p className="muted">
          Without the companion window, a browser tab opens only if the companion has to use a new
          address.
        </p>
        <p className="update-status" role="status">
          {copy.line}
          {update.state === 'installing' && update.error ? ` ${update.error}` : ''}
        </p>
        {copy.progress && (
          <progress
            className="update-progress"
            aria-label="Download progress"
            max={100}
            value={percent}
          />
        )}
        <div className="update-dialog-actions">
          <button
            className="primary"
            aria-disabled={busy || !copy.installable || undefined}
            onClick={install}
          >
            Update now
          </button>
          <button onClick={close}>{update.state === 'installing' ? 'Close' : 'Cancel'}</button>
          <a href={downloadUrl(update, projectUrl())} target="_blank" rel="noreferrer">
            {copy.downloadYourself ? 'Download it yourself' : "What's new"}
          </a>
        </div>
      </div>
    </Modal>
  );
}
