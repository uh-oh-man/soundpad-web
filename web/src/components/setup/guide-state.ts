import type { SoundpadProblem } from './setup-store';
import type { Snapshot } from '../../protocol';
import { supports } from '../../version';

/** The setup guide's steps, in order (2.44). */
export const STEPS = [
  { id: 'soundpad', title: 'Soundpad' },
  { id: 'capture', title: 'Capture output' },
  { id: 'invitations', title: 'Invitations' },
  { id: 'shortcuts', title: 'Shortcuts (optional)' },
  { id: 'launch', title: 'Launch' },
] as const;
export type StepId = (typeof STEPS)[number]['id'];

export type GuideState = {
  /** The step on screen (index into STEPS). */
  step: number;
  /** Steps the host has been through. */
  seen: StepId[];
  /** Finish was pressed. */
  finished: boolean;
};
export type GuideAction =
  | { type: 'next' }
  | { type: 'back' }
  | { type: 'go'; step: number }
  | { type: 'finish' }
  | { type: 'restart' };

export const initialGuide: GuideState = { step: 0, seen: ['soundpad'], finished: false };

/** The guide's step state: Next, Back, a step chosen in its list, Finish (on the last step only). */
export function guideReducer(state: GuideState, action: GuideAction): GuideState {
  const visit = (step: number): GuideState => {
    const at = Math.max(0, Math.min(STEPS.length - 1, step));
    const id = STEPS[at].id;
    return { ...state, step: at, seen: state.seen.includes(id) ? state.seen : [...state.seen, id] };
  };
  switch (action.type) {
    case 'next':
      return visit(state.step + 1);
    case 'back':
      return visit(state.step - 1);
    case 'go':
      return Number.isInteger(action.step) ? visit(action.step) : state;
    case 'finish':
      return state.step === STEPS.length - 1 ? { ...state, finished: true } : state;
    case 'restart':
      return initialGuide;
  }
}

/** What fixes each reason Soundpad can't be reached (the guide's first step and the connection banner). */
export function soundpadAdvice(problem: SoundpadProblem, elevated: boolean): string {
  switch (problem) {
    case 'notRunning':
      return 'Open Soundpad.';
    case 'noAnswer':
      return 'Soundpad is open but did not answer. Restart Soundpad.';
    case 'accessDenied':
      return (
        'Soundpad and Soundpad Web run with different rights (one as administrator). Start both the same way.' +
        (elevated ? ' Soundpad Web is running as administrator.' : '')
      );
    case 'oldVersion':
      return 'Update Soundpad.';
  }
}

/**
 * O02 (2.45): a brand-new host's first-run guide starts with guest capture (Guest Mimic) on, as the usual
 * friend permissions. Only then: never for an install that kept data from before, never once setup is
 * done, never when it is already on, and only once (the host may untick it and it stays off). The stored
 * default stays off, so a state.json without the field never turns guest capture on by itself.
 */
export function shouldPreselectGuestCapture(
  info: { existing: boolean; done: boolean },
  state: Pick<Snapshot, 'setupDone' | 'permissions' | 'companionVersion'>,
  alreadyDone: boolean,
) {
  return (
    !alreadyDone &&
    !info.existing &&
    !info.done &&
    state.setupDone === false &&
    state.permissions.guestMimic !== true &&
    supports(state.companionVersion, 'mimic')
  );
}
