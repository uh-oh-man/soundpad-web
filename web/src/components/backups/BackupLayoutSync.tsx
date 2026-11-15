import { useEffect, useRef } from 'react';
import { useStageH } from '../../stage-h';
import { supports } from '../../version';
import {
  backupCommand,
  layoutPayload,
  layoutSource,
  nextAttach,
  setLayoutTarget,
  type AttachState,
} from '../../backups';
import type { Workspace } from '../../workspace';
import { z } from 'zod';

/**
 * Backs up this page's layout (Stage H, 2.44, gate backups): the host's window and the host's browser
 * each send theirs to the companion once when they load and then 30 s after it last changed, so a
 * backup holds both (backup-workspace.v1.json). Guests and the example send nothing. It also keeps the
 * live layout reachable for a restore (backups.ts applyLayout). Renders nothing.
 */
export function BackupLayoutSync({
  workspace,
  ready,
  replace,
}: {
  workspace: Workspace | null;
  ready: boolean;
  replace: (w: Workspace) => void;
}) {
  const { state, mode, demo, controller } = useStageH();
  const on =
    mode === 'host' && !demo && state.available && supports(state.companionVersion, 'backups');
  const latest = useRef<string | null>(null);
  const attach = useRef<AttachState>({ since: 0 });
  latest.current = on && ready ? layoutPayload(workspace) : null;
  useEffect(() => {
    if (workspace && state.hostId) setLayoutTarget({ hostId: state.hostId, workspace, replace });
    else setLayoutTarget(null);
  }, [workspace, state.hostId, replace]);
  useEffect(() => () => setLayoutTarget(null), []);
  useEffect(() => {
    if (!on) return;
    const tick = () => {
      const { state: next, send } = nextAttach(attach.current, latest.current, Date.now());
      attach.current = next;
      if (!send || !latest.current) return;
      const source = layoutSource(controller.getSnapshot().hosting);
      void backupCommand(
        'backupAttach',
        { source, workspace: JSON.parse(latest.current) as Record<string, z.core.util.JSONType> },
        z.unknown(),
        { quiet: true },
      ).catch(() => {
        // Not sent: try again at the next check.
        attach.current = { ...attach.current, sent: undefined, seen: attach.current.seen ?? '' };
      });
    };
    tick();
    const timer = setInterval(tick, 5000);
    return () => clearInterval(timer);
  }, [on, controller]);
  return null;
}
