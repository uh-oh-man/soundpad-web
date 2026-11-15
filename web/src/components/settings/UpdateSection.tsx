import { useEffect, useState } from 'react';
import { controller } from '../../controller';
import { downloadUrl, openUpdateDialog, statusReplySchema, updateCopy } from '../../updates';
import { PROJECT_URL } from '../shell/Landing';
import { runUpdate, useUpdate } from '../updates/UpdateOffer';

/**
 * Settings → companion: updates (2.44, gate updater). Update to vX (the Update now dialog), Check
 * again, and whether the companion looks for updates when it starts. Host only; hidden for an older
 * companion and in the example. Its markup follows the Appearance section.
 */
export function UpdateSection() {
  const { update } = useUpdate();
  const [checkAtLaunch, setCheckAtLaunch] = useState<boolean | null>(null);
  const [checking, setChecking] = useState(false);
  const known = !!update;
  useEffect(() => {
    if (!known) return;
    let live = true;
    void controller
      .run('updateStatus', { body: {} }, { quiet: true })
      .then((raw) => {
        const parsed = statusReplySchema.safeParse(raw);
        if (live && parsed.success) setCheckAtLaunch(parsed.data.checkAtLaunch);
      })
      .catch(() => {});
    return () => {
      live = false;
    };
  }, [known]);
  if (!update) return null;
  const copy = updateCopy(update);
  const cannot =
    update.state === 'unsupported' &&
    (update.reason === 'noRepository' || update.reason === 'noExe');
  const busy = copy.busy || checking;
  const projectUrl = (import.meta.env.VITE_REPOSITORY_URL as string | undefined) || PROJECT_URL;
  return (
    <section className="appearance-setting update-setting" aria-label="Updates">
      <h3>Updates</h3>
      <p role="status">{copy.line}</p>
      <div className="update-setting-actions">
        {copy.installable && update.latest && (
          <button className="primary" onClick={() => openUpdateDialog()}>
            Update to v{update.latest}
          </button>
        )}
        {!cannot && (
          <button
            aria-disabled={busy || undefined}
            onClick={() => {
              if (busy) return;
              setChecking(true);
              void runUpdate('updateCheck', {})
                .catch(() => {})
                .finally(() => setChecking(false));
            }}
          >
            Check again
          </button>
        )}
        {copy.downloadYourself && (
          <a href={downloadUrl(update, projectUrl)} target="_blank" rel="noreferrer">
            Download it yourself
          </a>
        )}
      </div>
      {!cannot && checkAtLaunch !== null && (
        <label className="update-setting-launch">
          <input
            type="checkbox"
            checked={checkAtLaunch}
            onChange={(e) => {
              const next = e.target.checked;
              setCheckAtLaunch(next);
              void runUpdate('updateSettings', { checkAtLaunch: next }).catch(() =>
                setCheckAtLaunch(!next),
              );
            }}
          />{' '}
          Check for updates when the companion starts
        </label>
      )}
      <p className="muted">
        Updates come from this project's GitHub releases and are checked against the hash published
        with each release. Soundpad Web never installs one without your Update now.
      </p>
    </section>
  );
}
