import { useStageH } from '../../stage-h';
import { supports } from '../../version';
import { ShortcutList } from '../setup/ShortcutList';
import { useHotkeysSync, useSetupStore } from '../setup/setup-store';

/**
 * Settings → Keyboard (2.44, everyone, no gate): this browser's shortcuts, with Record, Turn off and
 * Reset, and the keys that work in one place. The companion window and each browser keep their own.
 */
export function KeyboardSection() {
  const { state, mode, demo } = useStageH();
  const host = mode === 'host';
  const hostWide = host && !demo && supports(state.companionVersion, 'hostHotkeys');
  useHotkeysSync(state, hostWide);
  const { hotkeys } = useSetupStore();
  return (
    <section className="appearance-setting shortcut-settings" aria-label="Keyboard">
      <h3>Keyboard</h3>
      <ShortcutList
        host={host}
        panic={host && supports(state.companionVersion, 'intents')}
        hostKeys={hostWide && hotkeys ? Object.values(hotkeys.bindings) : []}
      />
      <p>
        These keys are kept by this browser only: the companion window and each browser have their
        own.
      </p>
    </section>
  );
}
