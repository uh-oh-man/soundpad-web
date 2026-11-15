import { useState, useSyncExternalStore } from 'react';
import { Wrench } from 'lucide-react';
import { controller } from '../../controller';
import { useStageH } from '../../stage-h';
import { supports } from '../../version';
import { HostHotkeyPickers } from '../setup/HostHotkeyPickers';
import { openGuide, setupEligible, useHotkeysSync } from '../setup/setup-store';

/**
 * Settings → Setup (2.44, host, gate setup): run the guided setup again, and the host-wide hotkeys
 * (gate hostHotkeys). Run setup again marks setup as not done, so the guide also returns at the next
 * launch until Finish.
 */
export function SetupSection() {
  const { state, mode, demo } = useStageH();
  const view = useSyncExternalStore(controller.subscribe, controller.getSnapshot);
  const connected = view.status === 'Host ready';
  const eligible = setupEligible(state, mode, demo);
  const hostWide = eligible && supports(state.companionVersion, 'hostHotkeys');
  const [busy, setBusy] = useState(false);
  useHotkeysSync(state, hostWide && connected);
  if (!eligible) return null;
  return (
    <section className="appearance-setting setup-settings" aria-label="Setup">
      <h3>Setup</h3>
      <div className="setup-again">
        <p>Check Soundpad, the capture output, invitations and shortcuts step by step.</p>
        <button
          type="button"
          disabled={!connected}
          aria-disabled={busy || undefined}
          onClick={() => {
            if (busy) return;
            setBusy(true);
            void controller
              .run('setupSet', { body: { action: 'reset' } })
              .then(openGuide)
              .catch(() => {})
              .finally(() => setBusy(false));
          }}
        >
          <Wrench size={15} aria-hidden="true" /> Run setup again
        </button>
      </div>
      {hostWide && (
        <>
          <h4>Host-wide hotkeys</h4>
          <HostHotkeyPickers connected={connected} />
        </>
      )}
    </section>
  );
}
