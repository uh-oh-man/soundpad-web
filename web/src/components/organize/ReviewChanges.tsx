import { useState, useSyncExternalStore } from 'react';
import { controller } from '../../controller';
import { followFocus } from '../../focus-follow';
import { flattenCategories, type Snapshot } from '../../protocol';
import {
  classify,
  groupByTarget,
  isAppCategory,
  parentsOf,
  planUnavailable,
  type StepView,
} from '../../organize';
import {
  acceptProposal,
  applyPlan,
  clearDraft,
  discardPlan,
  dropFromDraft,
  proposeDraft,
  rejectProposal,
  removeSteps,
  resolveStep,
  useOrganize,
  withdrawProposal,
} from './organize-store';

const STATE_TEXT: Record<StepView['state'], string> = {
  pending: 'Waiting to apply',
  applying: 'Applying…',
  verified: '✓ Verified in Soundpad',
  conflict: 'Paused: Soundpad changed',
  failed: 'Failed',
  skipped: 'Skipped',
  manual: 'To do in Soundpad',
  done: '✓ Done',
  flagged: 'Check this in Soundpad',
};

function Badge({ support }: { support: 'native' | 'manual' }) {
  return (
    <span className={`organize-badge is-${support}`}>
      {support === 'native' ? 'Soundpad can do this' : 'Do this in Soundpad'}
    </span>
  );
}

function describe(step: StepView) {
  const sound = step.soundName ? `“${step.soundName}”` : 'a sound';
  const target = step.target ? `“${step.target.name}”` : 'the new category';
  switch (step.kind) {
    case 'createCategory':
      return `New category “${step.name}”`;
    case 'addSound':
      return `Add ${sound} to ${target}`;
    case 'removeFromCategory':
      return `Remove ${sound} from ${target}`;
    case 'removeCategory':
      return `Remove the category ${target}`;
    case 'renameCategory':
      return `Rename ${target} to “${step.name}”`;
    case 'createNested':
      return `New category “${step.name}” inside ${target}`;
    case 'reorder':
      return `Reorder ${target}`;
  }
}

/** The panel's heading: where focus goes when the control that had it goes away (never the page). */
const heading = () => document.getElementById('editor-title');
/** After `e`'s control takes itself away, focus goes to `next` (or the heading). */
const refocus = (e: { currentTarget: Element }, next?: () => HTMLElement | null) =>
  followFocus(e.currentTarget, () => next?.() ?? heading());

/**
 * Review changes (2.44), in the editor panel. The host sees the plan grouped by category, each step marked
 * "Soundpad can do this" or "Do this in Soundpad" with why, guests' proposals to accept, and Apply or
 * Discard; Apply's progress and any pause show here, with Skip, Retry or Rebind for a paused step, and a
 * recovery after a partial apply. A guest sees their own draft and proposals, and the host's plan.
 */
export function ReviewChanges({
  state,
  host,
  canPlan,
}: {
  state: Snapshot;
  host: boolean;
  canPlan: boolean;
}) {
  const { view, draft, busy } = useOrganize();
  const demo = useSyncExternalStore(controller.subscribe, controller.getSnapshot).demo;
  const plan = view?.plan ?? null;
  const categories = flattenCategories(state.library.categories).filter(
    ({ category }) => !isAppCategory(category.name),
  );
  const [rebinding, setRebinding] = useState<Record<string, string>>({});
  if (!canPlan)
    return (
      <p className="organize-note">
        {planUnavailable(host, demo, state.companionVersion, 'review')}
      </p>
    );
  const steps = plan?.steps ?? [];
  // The plan's steps in the order they are shown, so a removed step's neighbour can take focus.
  const shownOrder = groupByTarget(steps).flatMap((g) => g.steps.map((s) => s.id));
  const neighbour = (id: string) => {
    const at = shownOrder.indexOf(id);
    const ids = [...shownOrder.slice(at + 1), ...shownOrder.slice(0, at).reverse()];
    return () =>
      ids
        .map((x) => document.querySelector<HTMLElement>(`[data-step-remove="${x}"]`))
        .find((b) => b?.isConnected) ?? null;
  };
  const open = steps.filter((s) => !['verified', 'skipped', 'done'].includes(s.state));
  const applying = plan?.state === 'applying';
  const paused = plan?.state === 'paused';
  const nativePending = steps.some((s) => s.state === 'pending' && s.support === 'native');
  const blocked = steps.some((s) => s.state === 'conflict' || s.state === 'failed');
  const status = !plan
    ? 'No changes yet. Select sounds in Organize mode and drag them onto a category, or use Add selected to….'
    : applying
      ? plan.waiting
        ? 'Waiting for Soundpad to be quiet: changes are made only while nothing plays.'
        : 'Applying, one change at a time. Sounds can still be played between changes.'
      : paused
        ? (plan.pausedText ?? 'Paused.')
        : plan.state === 'done'
          ? 'Applied. Steps done in Soundpad are ticked off when Soundpad Web sees them.'
          : `${open.length} change${open.length === 1 ? '' : 's'} waiting.`;
  return (
    <div className="review-changes">
      <p className="review-status" role="status" aria-live="polite">
        {status}
      </p>
      {view?.problem && host && (
        <p className="review-problem" role="alert">
          {view.problem}
        </p>
      )}
      {plan?.addBehaviour === 'duplicates' && host && (
        <p className="organize-note">
          On this Soundpad, adding a sound to a category also lists it once more in All sounds.
        </p>
      )}
      {host && plan?.paused === 'duplicates' && (
        <div className="review-actions">
          <button
            type="button"
            className="primary"
            aria-disabled={busy || undefined}
            onClick={() => !busy && void resolveStep({ resolution: 'continue' })}
          >
            Continue
          </button>
          <button
            type="button"
            aria-disabled={busy || undefined}
            onClick={() => !busy && void resolveStep({ resolution: 'stop' })}
          >
            Stop
          </button>
        </div>
      )}
      {!host && (
        <section className="review-section" aria-label="Your proposal">
          <h3>Your proposal</h3>
          {draft.length ? (
            <ul className="review-steps">
              {draft.map((d, i) => {
                const c = classify(d.input, state.library.categories, state.library.sounds);
                return (
                  <li key={i} className="review-step">
                    <span className="review-step-text">{d.label}</span>
                    <Badge support={c.support} />
                    <small>{c.reason}</small>
                    <button
                      type="button"
                      aria-label={`Remove ${d.label} from your proposal`}
                      data-draft-remove={i}
                      onClick={(e) => {
                        refocus(
                          e,
                          () =>
                            document.querySelector<HTMLElement>(
                              `[data-draft-remove="${Math.max(0, Math.min(i, draft.length - 2))}"]`,
                            ) ?? null,
                        );
                        dropFromDraft(i);
                      }}
                    >
                      Remove
                    </button>
                  </li>
                );
              })}
            </ul>
          ) : (
            <p className="muted">
              Select sounds in Organize mode and drag them onto a category to suggest a change.
            </p>
          )}
          <div className="review-actions">
            <button
              type="button"
              className="primary"
              aria-disabled={busy || !draft.length || undefined}
              onClick={() => !busy && draft.length && void proposeDraft()}
            >
              Propose to the host
            </button>
            {!!draft.length && (
              <button
                type="button"
                onClick={(e) => {
                  refocus(e);
                  clearDraft();
                }}
              >
                Clear
              </button>
            )}
          </div>
        </section>
      )}
      {!!view?.proposals.length && (
        <section
          className="review-section"
          aria-label={host ? 'Proposals from guests' : 'Your proposals'}
        >
          <h3>{host ? 'Proposals from guests' : 'Waiting for the host'}</h3>
          {view.proposals.map((p) => (
            <div key={p.id} className="review-proposal">
              <strong>
                {host
                  ? `${p.by} proposes ${p.steps.length} change${p.steps.length === 1 ? '' : 's'}`
                  : `${p.steps.length} change${p.steps.length === 1 ? '' : 's'} proposed`}
              </strong>
              <ul className="review-steps">
                {p.steps.map((s) => (
                  <li key={s.id} className="review-step">
                    <span className="review-step-text">{describe(s)}</span>
                    <Badge support={s.support} />
                  </li>
                ))}
              </ul>
              <div className="review-actions">
                {host ? (
                  <>
                    <button
                      type="button"
                      className="primary"
                      aria-disabled={busy || applying || undefined}
                      onClick={(e) => {
                        if (busy || applying) return;
                        refocus(e);
                        void acceptProposal(p.id);
                      }}
                    >
                      Accept
                    </button>
                    <button
                      type="button"
                      aria-disabled={busy || undefined}
                      onClick={(e) => {
                        if (busy) return;
                        refocus(e);
                        void rejectProposal(p.id);
                      }}
                    >
                      Decline
                    </button>
                  </>
                ) : (
                  p.mine && (
                    <button
                      type="button"
                      aria-disabled={busy || undefined}
                      onClick={(e) => {
                        if (busy) return;
                        refocus(e);
                        void withdrawProposal(p.id);
                      }}
                    >
                      Withdraw
                    </button>
                  )
                )}
              </div>
            </div>
          ))}
        </section>
      )}
      {plan && !!steps.length && (
        <section
          className="review-section"
          aria-label={host ? 'Changes to apply' : "The host's plan"}
        >
          {!host && <h3>The host&rsquo;s plan</h3>}
          {groupByTarget(steps).map((group) => (
            <div key={group.key} className="review-group">
              <h4>{group.label}</h4>
              <ul className="review-steps">
                {group.steps.map((s) => (
                  <li key={s.id} className={`review-step is-${s.state}`} data-step-state={s.state}>
                    <span className="review-step-text">{describe(s)}</span>
                    <Badge support={s.support} />
                    <span className="review-step-state">{STATE_TEXT[s.state]}</span>
                    <small>{s.note ?? s.reason}</small>
                    {s.by !== 'Host' && <small className="review-by">Proposed by {s.by}</small>}
                    {host && (
                      <span className="review-step-actions">
                        {(s.state === 'conflict' || s.state === 'failed') && (
                          <>
                            <button
                              type="button"
                              aria-disabled={busy || undefined}
                              onClick={() =>
                                !busy && void resolveStep({ resolution: 'skip', stepId: s.id })
                              }
                            >
                              Skip
                            </button>
                            <button
                              type="button"
                              aria-disabled={busy || undefined}
                              onClick={() =>
                                !busy && void resolveStep({ resolution: 'retry', stepId: s.id })
                              }
                            >
                              Retry
                            </button>
                            {s.kind !== 'createCategory' && !s.targetStepId && (
                              <span className="review-rebind">
                                <select
                                  aria-label={`Rebind ${describe(s)} to`}
                                  value={rebinding[s.id] ?? s.candidate?.categoryId ?? ''}
                                  onChange={(e) =>
                                    setRebinding((r) => ({ ...r, [s.id]: e.target.value }))
                                  }
                                >
                                  <option value="">Choose a category…</option>
                                  {categories.map(({ category: c, depth }) => (
                                    <option key={c.id} value={c.id}>
                                      {'　'.repeat(depth) + (c.name || 'Unnamed category')}
                                    </option>
                                  ))}
                                </select>
                                <button
                                  type="button"
                                  aria-disabled={
                                    busy ||
                                    !(rebinding[s.id] ?? s.candidate?.categoryId) ||
                                    undefined
                                  }
                                  onClick={() => {
                                    const id = rebinding[s.id] ?? s.candidate?.categoryId;
                                    const c = categories.find(
                                      (x) => x.category.id === id,
                                    )?.category;
                                    if (!busy && c)
                                      void resolveStep({
                                        resolution: 'rebind',
                                        stepId: s.id,
                                        targetCategoryId: c.id,
                                        targetCategoryName: c.name,
                                        targetCategoryParents: parentsOf(
                                          state.library.categories,
                                          c.id,
                                        ),
                                      });
                                  }}
                                >
                                  Rebind
                                </button>
                              </span>
                            )}
                          </>
                        )}
                        {(s.state === 'manual' || s.state === 'flagged') && (
                          <button
                            type="button"
                            aria-disabled={busy || undefined}
                            onClick={() =>
                              !busy && void resolveStep({ resolution: 'done', stepId: s.id })
                            }
                          >
                            Mark done
                          </button>
                        )}
                        {['pending', 'manual', 'conflict', 'failed', 'flagged'].includes(s.state) &&
                          !applying && (
                            <button
                              type="button"
                              aria-label={`Remove ${describe(s)} from the plan`}
                              aria-disabled={busy || undefined}
                              data-step-remove={s.id}
                              onClick={(e) => {
                                if (busy) return;
                                refocus(e, neighbour(s.id));
                                void removeSteps([s.id]);
                              }}
                            >
                              Remove
                            </button>
                          )}
                      </span>
                    )}
                  </li>
                ))}
              </ul>
            </div>
          ))}
        </section>
      )}
      {host && view?.recovery && (
        <section className="review-section review-recovery" aria-label="Recovery">
          <h3>After a partial apply</h3>
          {view.recovery.created > 0 && (
            <button
              type="button"
              aria-disabled={busy || applying || undefined}
              onClick={() =>
                !busy && !applying && void resolveStep({ resolution: 'removeCreated' })
              }
            >
              Remove the empty categories this plan created ({view.recovery.created})
            </button>
          )}
          {view.recovery.added.length > 0 && (
            <>
              <p>To undo the sounds already added, in Soundpad remove each from its category:</p>
              <ul>
                {view.recovery.added.map((a, i) => (
                  <li key={i}>
                    &ldquo;{a.soundName}&rdquo; from &ldquo;{a.category}&rdquo;
                  </li>
                ))}
              </ul>
            </>
          )}
          {view.checkpoint && (
            <p className="muted">
              A copy of Soundpad&rsquo;s sound list file is kept, as Soundpad last saved it before
              this plan was first applied
              {view.checkpoint.takenAtUtcMs
                ? ` (${new Date(view.checkpoint.takenAtUtcMs).toLocaleString()})`
                : ''}
              . Changes made in Soundpad after it last saved are not in it. Restore it only with
              Soundpad closed: copy <code>{view.checkpoint.copy}</code> over{' '}
              <code>%APPDATA%\Leppsoft\soundlist.spl</code>.
            </p>
          )}
        </section>
      )}
      {host && plan && (
        <div className="review-actions review-footer">
          <button
            type="button"
            className="primary"
            aria-disabled={busy || applying || blocked || !nativePending || undefined}
            onClick={() => !busy && !applying && !blocked && nativePending && void applyPlan()}
          >
            Apply
          </button>
          {(applying || paused) && (
            <button
              type="button"
              aria-disabled={busy || undefined}
              onClick={() => !busy && void resolveStep({ resolution: 'cancel' })}
            >
              Cancel plan
            </button>
          )}
          {!applying && (
            <button
              type="button"
              aria-disabled={busy || undefined}
              onClick={(e) => {
                if (busy) return;
                refocus(e);
                void discardPlan();
              }}
            >
              {plan.state === 'done' ? 'Clear' : 'Discard'}
            </button>
          )}
        </div>
      )}
    </div>
  );
}
