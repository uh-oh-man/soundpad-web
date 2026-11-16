import { useEffect, useId, useState } from 'react';
import { ChevronDown, ChevronRight, HardDrive, History } from 'lucide-react';
import { z } from 'zod';
import { controller } from '../controller';
import { flattenCategories, type Snapshot } from '../protocol';
import { refreshBudget, useDraftStore } from '../mimic/draft-store';
import { formatMb, type BudgetRequest, type KeepSettings } from '../mimic/drafts';
import { Modal } from './Modal';

const budgetActionsSchema = z.object({
  guestId: z.string().max(128),
  actions: z
    .array(
      z.object({
        kind: z.enum(['temp', 'kept']),
        name: z.string().max(200).nullish(),
        bytes: z.number().nonnegative(),
        at: z.number(),
      }),
    )
    .max(100),
});
export type BudgetAction = z.infer<typeof budgetActionsSchema>['actions'][number];

/** "12:03" (or a date and time when it was not today), for a storage action. */
function when(at: number) {
  const date = new Date(at);
  const today = new Date().toDateString() === date.toDateString();
  return today
    ? date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
    : date.toLocaleString([], { dateStyle: 'short', timeStyle: 'short' });
}

/**
 * A guest's recent Temp and Keep actions (2.45, gate budgetActions): what they put into storage, with
 * its size and time, newest first. Fetched when opened, and again when their request changes. The
 * companion answers it to the host (any guest) and to that guest (only their own), nobody else.
 */
export function BudgetActions({
  guestId,
  name,
  revision,
}: {
  guestId?: string;
  name: string;
  revision: string;
}) {
  const [actions, setActions] = useState<BudgetAction[] | null>(null);
  const [failed, setFailed] = useState(false);
  useEffect(() => {
    let live = true;
    setFailed(false);
    void controller
      .run('budgetActions', guestId ? { guestId } : {}, { quiet: true })
      .then((raw) => {
        if (live) setActions(budgetActionsSchema.parse(raw).actions);
      })
      .catch(() => {
        if (live) setFailed(true);
      });
    return () => {
      live = false;
    };
  }, [guestId, revision]);
  if (failed) return <p className="budget-actions muted">The recent clips could not be loaded.</p>;
  if (!actions) return <p className="budget-actions muted">Loading recent clips…</p>;
  if (!actions.length)
    return (
      <p className="budget-actions muted">
        {name} has not added anything since the companion started.
      </p>
    );
  return (
    <ul className="budget-actions" aria-label={`Recent clips of ${name}`}>
      {actions.map((a, i) => (
        <li key={`${a.at}-${i}`}>
          <span className="budget-action-kind">{a.kind === 'kept' ? 'Kept' : 'Temp'}</span>
          <span className="budget-action-name">
            {a.name ?? (a.kind === 'kept' ? 'Kept audio' : 'A clip')}
          </span>
          <span className="budget-action-size">{formatMb(a.bytes)}</span>
          <time dateTime={new Date(a.at).toISOString()}>{when(a.at)}</time>
        </li>
      ))}
    </ul>
  );
}

function BudgetRequestRow({ request, history }: { request: BudgetRequest; history: boolean }) {
  const [open, setOpen] = useState(false);
  const id = useId();
  const decide = async (action: 'allow' | 'deny') => {
    try {
      await controller.run('budgetGrant', { guestId: request.guestId, action });
      await refreshBudget(true);
    } catch {
      /* Controller shows the error. */
    }
  };
  return (
    <li>
      <HardDrive size={16} aria-hidden="true" />
      <span>
        <strong>{request.name}</strong> asks for more{' '}
        {request.kind === 'kept' ? 'kept audio' : 'Temp space'}{' '}
        <small>({formatMb(request.neededBytes)} over)</small>
      </span>
      {history && (
        <button
          type="button"
          className="budget-actions-toggle"
          aria-expanded={open}
          aria-controls={id}
          aria-label={`Recent clips of ${request.name}`}
          onClick={() => setOpen((was) => !was)}
        >
          {open ? (
            <ChevronDown size={14} aria-hidden="true" />
          ) : (
            <ChevronRight size={14} aria-hidden="true" />
          )}{' '}
          Recent clips
        </button>
      )}
      <button
        className="primary"
        aria-label={`Allow ${request.name} more`}
        onClick={() => void decide('allow')}
      >
        Allow more
      </button>
      <button aria-label={`Not now for ${request.name}`} onClick={() => void decide('deny')}>
        Not now
      </button>
      {history && (
        <div id={id} className="budget-actions-panel" hidden={!open}>
          {open && (
            <BudgetActions
              guestId={request.guestId}
              name={request.name}
              revision={`${request.kind}/${request.neededBytes}/${request.at}`}
            />
          )}
        </div>
      )}
    </li>
  );
}

/**
 * Guests waiting for more storage (2.40), in the same list style as the lobby: the request stays until
 * the host decides. "Allow more" lasts until the companion closes and never deletes anything. With
 * `history` (2.45, gate budgetActions) each row opens to that guest's recent Temp and Keep clips.
 */
export function BudgetRequests({
  requests,
  history = false,
}: {
  requests: BudgetRequest[];
  history?: boolean;
}) {
  if (!requests.length) return null;
  return (
    <ul className="lobby-requests budget-requests" aria-label="Guests waiting for more storage">
      {requests.map((request) => (
        <BudgetRequestRow key={request.guestId} request={request} history={history} />
      ))}
    </ul>
  );
}

/**
 * After a crash (2.40): the previous session's clips are still in Temp until the host decides. Since
 * 2.45 (gate sessionRecovery) RecoverSession asks once for Temp, history and drafts; this stays for
 * older companions.
 */
export function TempRecovery({ clips }: { clips: number }) {
  const [busy, setBusy] = useState(false);
  const decide = async (action: 'recover' | 'discard') => {
    if (busy) return;
    setBusy(true);
    try {
      await controller.run('tempRecover', { action });
      controller.notify(
        action === 'recover'
          ? 'The previous session’s clips are back in Temp.'
          : 'The previous session’s Temp was removed.',
        'success',
      );
    } catch {
      /* Controller shows the error. */
    } finally {
      setBusy(false);
    }
  };
  return (
    <section className="temp-recovery" role="alert" aria-label="Previous session's Temp">
      <History size={17} aria-hidden="true" />
      <p>
        The companion did not close normally last time.{' '}
        {clips === 1 ? 'One clip' : `${clips} clips`} from that session {clips === 1 ? 'is' : 'are'}{' '}
        still in Temp.
      </p>
      <button
        className="primary"
        aria-disabled={busy || undefined}
        onClick={() => void decide('recover')}
      >
        Recover previous session&rsquo;s Temp
      </button>
      <button
        aria-disabled={busy || undefined}
        onClick={() => {
          if (confirm('Remove the previous session’s clips from Temp? Kept audio stays.'))
            void decide('discard');
        }}
      >
        Discard
      </button>
    </section>
  );
}

/**
 * Where Keep puts clips (the host chooses once; guests never choose a path) and how much each guest may
 * use: Temp for this session, and kept audio per session or in total (2.40).
 */
export function KeepSettingsForm({ state, connected }: { state: Snapshot; connected: boolean }) {
  const { budget } = useDraftStore();
  const settings = budget?.host?.settings;
  const [draft, setDraft] = useState<KeepSettings | null>(null);
  const [saving, setSaving] = useState(false);
  useEffect(() => {
    if (settings && !draft) setDraft(settings);
  }, [settings, draft]);
  useEffect(() => {
    void refreshBudget(true);
  }, []);
  if (!draft) return null;
  const categories = flattenCategories(state.library.categories).filter(
    (c) =>
      /^\d+$/.test(c.category.id) && !/^All$|^Temp .{1,3} Soundpad Web \[/.test(c.category.name),
  );
  return (
    <form
      className="keep-settings"
      aria-label="Keep and guest storage"
      onSubmit={(e) => {
        e.preventDefault();
        if (saving) return;
        setSaving(true);
        const chosen = categories.find((c) => Number(c.category.id) === draft.categoryId);
        void controller
          .run('keepSettings', {
            destinationDirectory: draft.destination ?? '',
            // No category chosen: the app-managed Saved category (an empty name clears the choice).
            ...(chosen
              ? { categoryId: Number(chosen.category.id), categoryName: chosen.category.name }
              : { categoryName: '' }),
            tempMb: draft.tempMb,
            keptMb: draft.keptMb,
            budgetMode: draft.budgetMode,
          })
          .then(() => {
            controller.notify('Keep and storage settings saved.', 'success');
            return refreshBudget(true);
          })
          .catch(() => {})
          .finally(() => setSaving(false));
      }}
    >
      <h3>Keep and guest storage</h3>
      <label>
        Keep folder
        <input
          placeholder="Default: companion permanent library"
          maxLength={1024}
          value={draft.destination ?? ''}
          onChange={(e) => setDraft({ ...draft, destination: e.target.value })}
        />
      </label>
      <label>
        Keep category
        <select
          value={draft.categoryId ?? ''}
          onChange={(e) =>
            setDraft({
              ...draft,
              categoryId: e.target.value === '' ? null : Number(e.target.value),
            })
          }
        >
          <option value="">App-managed Saved category</option>
          {categories.map((c) => (
            <option key={c.category.id} value={c.category.id}>
              {c.category.name}
            </option>
          ))}
        </select>
      </label>
      <p className="muted">
        Guests keep clips into this folder and category; they can never choose a path. Guests have
        no storage limit: their clips and copies never wait for you. Nothing they kept is ever
        deleted.
      </p>
      <button type="submit" disabled={!connected} aria-disabled={saving || undefined}>
        Save storage settings
      </button>
    </form>
  );
}

/** One guest's storage, for their row in the host's guest list. */
export function GuestUsage({ participantId }: { participantId?: string }) {
  const { budget } = useDraftStore();
  const usage = participantId
    ? budget?.host?.guests.find((g) => g.id === participantId)
    : undefined;
  if (!usage) return null;
  return (
    <small className={`guest-usage${usage.paused ? ' paused' : ''}`}>
      Temp {formatMb(usage.tempBytes)} · kept {formatMb(usage.keptBytes)}
    </small>
  );
}

/**
 * The companion window is closing while Temp holds clips (2.40): keep some first, quit anyway, or
 * cancel. Shown only in the window's own page; the companion quits once it has the answer.
 */
export function QuitPromptDialog({ clips }: { clips: number }) {
  const [assets, setAssets] = useState<{ id: string; name: string }[] | null>(null);
  const [chosen, setChosen] = useState<string[]>([]);
  useEffect(() => {
    void controller
      .run('audioAssets', {}, { quiet: true })
      .then((raw) => {
        const parsed = z
          .object({
            manager: z.object({
              assets: z.array(
                z.object({
                  id: z.string(),
                  name: z.string(),
                  variants: z.array(
                    z.object({
                      permanent: z.boolean(),
                      current: z.boolean(),
                      state: z.string(),
                      nativeLabel: z.string().nullable().optional(),
                    }),
                  ),
                }),
              ),
            }),
          })
          .parse(raw);
        setAssets(
          parsed.manager.assets
            .filter((a) =>
              a.variants.some(
                (v) => v.current && !v.permanent && v.nativeLabel && v.state !== 'cleanup-pending',
              ),
            )
            .map((a) => ({ id: a.id, name: a.name })),
        );
      })
      .catch(() => setAssets([]));
  }, []);
  return (
    <Modal label="Quit Soundpad Web" onClose={() => controller.answerQuit('cancel')}>
      <p>
        {clips === 1 ? 'One clip' : `${clips} clips`} in Temp will be removed when Soundpad Web
        quits. Tick the ones to keep permanently first.
      </p>
      {assets === null ? (
        <p className="muted">Loading Temp…</p>
      ) : (
        <ul className="quit-clips" aria-label="Clips in Temp">
          {assets.map((a) => (
            <li key={a.id}>
              <label>
                <input
                  type="checkbox"
                  checked={chosen.includes(a.id)}
                  onChange={(e) =>
                    setChosen((list) =>
                      e.target.checked ? [...list, a.id] : list.filter((id) => id !== a.id),
                    )
                  }
                />{' '}
                {a.name}
              </label>
            </li>
          ))}
        </ul>
      )}
      <div className="dialog-foot">
        <button
          className="primary"
          aria-disabled={!chosen.length || undefined}
          onClick={() => chosen.length && controller.answerQuit('keep', chosen)}
        >
          Keep selected
        </button>
        <button onClick={() => controller.answerQuit('quit')}>Quit anyway</button>
        <button onClick={() => controller.answerQuit('cancel')}>Cancel</button>
      </div>
    </Modal>
  );
}
