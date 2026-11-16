import { useEffect, useRef, useState } from 'react';
import { History } from 'lucide-react';
import { z } from 'zod';
import { controller } from '../controller';
import type { Snapshot } from '../protocol';
import { formatMb } from '../mimic/drafts';
import { formatSpan } from '../mimic/history';

const partSchema = z.object({
  count: z.number().int().nonnegative(),
  ms: z.number().int().nonnegative().nullish(),
  bytes: z.number().int().nonnegative(),
});
/** The companion's answer to sessionRecover (2.45): what a launch that did not close normally left. */
export const sessionRecoverySchema = z.object({
  pending: z.boolean(),
  temp: partSchema.nullish(),
  history: partSchema.nullish(),
  drafts: partSchema.nullish(),
});
export type SessionRecovery = z.infer<typeof sessionRecoverySchema>;

/** True while the snapshot says a crashed launch left something for the host to decide about. */
export const recoveryPending = (state: Snapshot) =>
  !!state.tempRecovery || (state.mimic?.history?.recoveredMs ?? 0) > 0;

/** The parts as list rows: what, and how big. Parts the companion has not reported come from the snapshot. */
export function recoveryRows(info: SessionRecovery | null, state: Snapshot) {
  const rows: { key: string; text: string }[] = [];
  const clips = info ? (info.temp?.count ?? 0) : (state.tempRecovery ?? 0);
  if (clips > 0)
    rows.push({
      key: 'temp',
      text: `${clips === 1 ? 'One clip' : `${clips} clips`} in Temp${info?.temp ? ` · ${formatMb(info.temp.bytes)}` : ''}`,
    });
  const ms = info ? (info.history?.ms ?? 0) : (state.mimic?.history?.recoveredMs ?? 0);
  if (ms > 0)
    rows.push({
      key: 'history',
      text: `${formatSpan(ms)} of recording history${info?.history ? ` · ${formatMb(info.history.bytes)}` : ''}`,
    });
  const drafts = info?.drafts;
  if (drafts && drafts.count > 0)
    rows.push({
      key: 'drafts',
      text: `${drafts.count === 1 ? 'One draft' : `${drafts.count} drafts`} of yours${drafts.ms ? ` (${formatSpan(drafts.ms)})` : ''} · ${formatMb(drafts.bytes)}`,
    });
  return rows;
}

/**
 * After a crash (2.45, gate sessionRecovery): one question for everything the previous session left,
 * instead of a Temp prompt here and a history notice in Mimic. Recover keeps the clips in Temp, the
 * recording history and the host's drafts as they were; Discard removes exactly those (never anything
 * kept, and not what was recorded since the companion started again). Host only.
 */
export function RecoverSession({ state }: { state: Snapshot }) {
  const pending = recoveryPending(state);
  const [info, setInfo] = useState<SessionRecovery | null>(null);
  const [busy, setBusy] = useState(false);
  const running = useRef(false);
  // What the snapshot says changed (a Temp count arrived after the history, say): ask again.
  const signal = `${state.tempRecovery ?? 0}/${state.mimic?.history?.recoveredMs ?? 0}`;
  useEffect(() => {
    if (!pending) {
      setInfo(null);
      return;
    }
    let live = true;
    void controller
      .run('sessionRecover', { action: 'status' }, { quiet: true })
      .then((raw) => {
        if (live) setInfo(sessionRecoverySchema.parse(raw));
      })
      .catch(() => {
        /* The snapshot's figures still show; the buttons still work. */
      });
    return () => {
      live = false;
    };
  }, [pending, signal]);
  if (!pending || info?.pending === false) return null;
  const rows = recoveryRows(info, state);
  const decide = async (action: 'recover' | 'discard') => {
    if (running.current) return;
    running.current = true;
    setBusy(true);
    try {
      await controller.run('sessionRecover', { action });
      controller.notify(
        action === 'recover'
          ? 'The previous session is back: its Temp clips, recording history and drafts.'
          : 'The previous session was discarded. Anything you kept stays.',
        'success',
      );
    } catch {
      /* Controller shows the error. */
    } finally {
      running.current = false;
      setBusy(false);
    }
  };
  return (
    <section className="temp-recovery recover-session" role="alert" aria-label="Previous session">
      <History size={17} aria-hidden="true" />
      <div className="recover-session-text">
        <p>The companion did not close normally last time. It kept from that session:</p>
        <ul className="recover-session-parts" aria-label="What the previous session left">
          {rows.map((row) => (
            <li key={row.key}>{row.text}</li>
          ))}
        </ul>
      </div>
      <button
        className="primary"
        aria-disabled={busy || undefined}
        onClick={() => void decide('recover')}
      >
        Recover previous session
      </button>
      <button
        aria-disabled={busy || undefined}
        onClick={() => {
          if (running.current) return;
          if (
            confirm(
              'Discard the previous session? Its clips in Temp, the recovered recording history and your drafts from it are deleted from this PC and cannot be brought back. Anything you kept stays, and so does what was recorded since.',
            )
          )
            void decide('discard');
        }}
      >
        Discard
      </button>
    </section>
  );
}
