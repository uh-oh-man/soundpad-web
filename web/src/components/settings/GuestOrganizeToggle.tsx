import { supports } from '../../version';
import type { Snapshot } from '../../protocol';
import { setGuestOrganize, useOrganize } from '../organize/organize-store';

/**
 * Settings → "Guests may propose organization changes" (2.44, gate organize; host only, off by default).
 * Guests then suggest category changes in Review changes; only the host applies them.
 */
export function GuestOrganizeToggle({ state, demo }: { state: Snapshot; demo: boolean }) {
  const { busy } = useOrganize();
  if (demo || !supports(state.companionVersion, 'organize')) return null;
  const on = state.permissions.guestOrganize === true;
  return (
    <div className="setting-row">
      <div>
        <strong>Guest organization proposals</strong>
        <p>
          Guests may propose category changes in Review changes. Only you apply them to Soundpad.
        </p>
      </div>
      <input
        type="checkbox"
        aria-label="Guests may propose organization changes"
        checked={on}
        aria-disabled={busy || undefined}
        onChange={(e) => {
          if (busy) return;
          void setGuestOrganize(e.target.checked);
        }}
      />
    </div>
  );
}
