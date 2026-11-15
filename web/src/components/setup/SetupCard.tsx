import { useState, useSyncExternalStore } from 'react';
import { Wrench, X } from 'lucide-react';
import { controller } from '../../controller';
import type { Snapshot } from '../../protocol';
import { openGuide, setupEligible, useSetupStore, useSetupSync } from './setup-store';

/**
 * "Check your setup" (2.44): an upgraded install (kept data from before) gets this card on the board
 * instead of the setup dialog. Open setup shows the guide; the close button dismisses the card for
 * good (setup.json). A busy dismiss is aria-disabled, never disabled.
 */
export function SetupCard({ state }: { state: Snapshot }) {
  const view = useSyncExternalStore(controller.subscribe, controller.getSnapshot);
  const connected = view.status === 'Host ready';
  const eligible = setupEligible(state, view.mode, view.demo) && connected;
  const { info, open } = useSetupStore();
  const [busy, setBusy] = useState(false);
  useSetupSync(state, eligible);
  if (
    !eligible ||
    state.setupDone !== false ||
    !info?.existing ||
    info.done ||
    info.dismissed ||
    open
  )
    return null;
  return (
    <aside className="setup-card" aria-label="Check your setup">
      <Wrench size={18} aria-hidden="true" />
      <div>
        <strong>Check your setup</strong>
        <p>
          A short guide checks Soundpad, remembers the output Capture listens to, explains
          invitations and offers shortcuts. Nothing changes until you choose.
        </p>
      </div>
      <button type="button" className="primary" onClick={openGuide}>
        Open setup
      </button>
      <button
        type="button"
        className="icon-button"
        aria-label="Dismiss Check your setup"
        aria-disabled={busy || undefined}
        onClick={() => {
          if (busy) return;
          setBusy(true);
          void controller
            .run('setupSet', { body: { action: 'dismiss' } })
            .catch(() => {})
            .finally(() => setBusy(false));
        }}
      >
        <X size={16} aria-hidden="true" />
      </button>
    </aside>
  );
}
