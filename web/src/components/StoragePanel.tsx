import { useEffect, useState } from 'react';
import { HardDrive, History } from 'lucide-react';
import { z } from 'zod';
import { controller } from '../controller';
import { flattenCategories, type Snapshot } from '../protocol';
import { refreshBudget, useDraftStore } from '../mimic/draft-store';
import { formatMb, type BudgetRequest, type KeepSettings } from '../mimic/drafts';
import { Modal } from './Modal';

/**
 * Guests waiting for more storage (2.40), in the same list style as the lobby: the request stays until
 * the host decides. "Allow more" lasts until the companion closes and never deletes anything.
 */
export function BudgetRequests({ requests }: { requests: BudgetRequest[] }) {
  if (!requests.length) return null;
  const decide = async (guestId: string, action: 'allow' | 'deny') => {
    try {
      await controller.run('budgetGrant', { guestId, action });
      await refreshBudget(true);
    } catch {
      /* Controller shows the error. */
    }
  };
  return (
    <ul className="lobby-requests budget-requests" aria-label="Guests waiting for more storage">
      {requests.map((request) => (
        <li key={request.guestId}>
          <HardDrive size={16} aria-hidden="true" />
          <span>
            <strong>{request.name}</strong> asks for more{' '}
            {request.kind === 'kept' ? 'kept audio' : 'Temp space'}{' '}
            <small>({formatMb(request.neededBytes)} over)</small>
          </span>
          <button
            className="primary"
            aria-label={`Allow ${request.name} more`}
            onClick={() => void decide(request.guestId, 'allow')}
          >
            Allow more
          </button>
          <button
            aria-label={`Not now for ${request.name}`}
            onClick={() => void decide(request.guestId, 'deny')}
          >
            Not now
          </button>
        </li>
      ))}
    </ul>
  );
}

/** After a crash (2.40): the previous session's clips are still in Temp until the host decides. */
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
  const mb = (value: string, fallback: number) => {
    const n = z.coerce.number().int().safeParse(value);
    return n.success ? n.data : fallback;
  };
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
      <label>
        Temp per guest (MB)
        <input
          type="number"
          min={1}
          max={4096}
          value={draft.tempMb}
          onChange={(e) => setDraft({ ...draft, tempMb: mb(e.target.value, draft.tempMb) })}
        />
      </label>
      <label>
        Kept audio per guest (MB)
        <input
          type="number"
          min={1}
          max={65536}
          value={draft.keptMb}
          onChange={(e) => setDraft({ ...draft, keptMb: mb(e.target.value, draft.keptMb) })}
        />
      </label>
      <label>
        Count kept audio
        <select
          value={draft.budgetMode}
          onChange={(e) =>
            setDraft({ ...draft, budgetMode: e.target.value === 'total' ? 'total' : 'session' })
          }
        >
          <option value="session">Per session</option>
          <option value="total">In total</option>
        </select>
      </label>
      <p className="muted">
        Guests keep clips into this folder and category; they can never choose a path. When a guest
        reaches their allowance, their new clips wait until you allow more (until the companion
        closes). Nothing they kept is ever deleted.
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
      Temp {formatMb(usage.tempBytes, usage.tempLimitBytes)} · kept{' '}
      {formatMb(usage.keptBytes, usage.keptLimitBytes)}
      {usage.waiting ? ' · waiting for more' : ''}
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
