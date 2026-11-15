import { useState } from 'react';
import { RefreshCw } from 'lucide-react';
import { controller } from '../../controller';
import type { Snapshot } from '../../protocol';
import { soundpadAdvice } from './guide-state';
import { loadSetup } from './setup-store';

/**
 * What to do when Soundpad can't be reached (2.44): the fix for the reason the companion found, and
 * Retry (a library refresh). Busy Retry is aria-disabled, never disabled, so it keeps the keyboard.
 */
export function SoundpadFix({
  problem,
  elevated,
  connected,
}: {
  problem: NonNullable<Snapshot['soundpadProblem']>;
  elevated: boolean;
  connected: boolean;
}) {
  const [busy, setBusy] = useState(false);
  return (
    <span className="soundpad-fix">
      <span data-problem={problem}>{soundpadAdvice(problem, elevated)}</span>{' '}
      <button
        type="button"
        className="soundpad-retry"
        disabled={!connected}
        aria-disabled={busy || undefined}
        onClick={() => {
          if (busy) return;
          setBusy(true);
          void controller
            .run('refresh', {}, { quiet: true })
            .catch(() => {})
            .finally(() => {
              setBusy(false);
              void loadSetup();
            });
        }}
      >
        <RefreshCw size={14} aria-hidden="true" /> Retry
      </button>
    </span>
  );
}
