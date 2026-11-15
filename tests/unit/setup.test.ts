/** The setup guide's step state and the fix it shows for each Soundpad problem (2.44, gate setup). */
import { describe, expect, it } from 'vitest';
import {
  STEPS,
  guideReducer,
  initialGuide,
  soundpadAdvice,
  type GuideState,
} from '../../web/src/components/setup/guide-state';
import { EMPTY, parseSnapshot } from '../../web/src/protocol';
import { withoutHostOnly } from '../../web/src/network/session';

describe('setup guide steps', () => {
  it('walks Soundpad, capture output, invitations, shortcuts and launch, in that order', () => {
    expect(STEPS.map((s) => s.id)).toEqual([
      'soundpad',
      'capture',
      'invitations',
      'shortcuts',
      'launch',
    ]);
    expect(initialGuide).toEqual({ step: 0, seen: ['soundpad'], finished: false });
  });

  it('Next and Back stay within the steps and remember what was seen', () => {
    let g: GuideState = guideReducer(initialGuide, { type: 'back' });
    expect(g.step).toBe(0);
    g = guideReducer(g, { type: 'next' });
    g = guideReducer(g, { type: 'next' });
    expect(g).toMatchObject({ step: 2, seen: ['soundpad', 'capture', 'invitations'] });
    g = guideReducer(g, { type: 'back' });
    expect(g).toMatchObject({ step: 1, seen: ['soundpad', 'capture', 'invitations'] });
    for (let i = 0; i < 10; i++) g = guideReducer(g, { type: 'next' });
    expect(g.step).toBe(STEPS.length - 1);
    expect(g.seen).toHaveLength(STEPS.length);
  });

  it('a step chosen in the list is shown; out of range is clamped, not a number is ignored', () => {
    expect(guideReducer(initialGuide, { type: 'go', step: 3 })).toMatchObject({
      step: 3,
      seen: ['soundpad', 'shortcuts'],
    });
    expect(guideReducer(initialGuide, { type: 'go', step: 99 }).step).toBe(4);
    expect(guideReducer(initialGuide, { type: 'go', step: -2 }).step).toBe(0);
    expect(guideReducer(initialGuide, { type: 'go', step: 1.5 })).toBe(initialGuide);
  });

  it('Finish counts only on the last step, and restart starts over', () => {
    expect(guideReducer(initialGuide, { type: 'finish' }).finished).toBe(false);
    const last = guideReducer(initialGuide, { type: 'go', step: 4 });
    const done = guideReducer(last, { type: 'finish' });
    expect(done.finished).toBe(true);
    expect(guideReducer(done, { type: 'restart' })).toEqual(initialGuide);
  });
});

describe('Soundpad fixes', () => {
  it('says what to do for each reason', () => {
    expect(soundpadAdvice('notRunning', false)).toBe('Open Soundpad.');
    expect(soundpadAdvice('noAnswer', false)).toBe(
      'Soundpad is open but did not answer. Restart Soundpad.',
    );
    expect(soundpadAdvice('accessDenied', false)).toBe(
      'Soundpad and Soundpad Web run with different rights (one as administrator). Start both the same way.',
    );
    expect(soundpadAdvice('accessDenied', true)).toMatch(
      /Start both the same way\. Soundpad Web is running as administrator\.$/,
    );
    expect(soundpadAdvice('oldVersion', true)).toBe('Update Soundpad.');
  });
});

describe('what guests receive', () => {
  it('no setup, Soundpad problem, rights, hotkeys, updater or backups, and no F6 status line', () => {
    const host = parseSnapshot({
      ...EMPTY,
      setupDone: false,
      soundpadProblem: 'accessDenied',
      companionElevated: true,
      hotkeys: 4,
      backups: 2,
      organize: 3,
      update: { state: 'available', current: '2.44.0', latest: '2.44.1' },
      mimic: {
        active: true,
        source: 'Output',
        status: 'Listening',
        windowMs: 30000,
        hotkeyStatus: 'Saved the last 10 s at 14:02',
      },
    });
    const guest = withoutHostOnly(host);
    for (const key of [
      'setupDone',
      'soundpadProblem',
      'companionElevated',
      'hotkeys',
      'backups',
      'update',
    ] as const)
      expect(guest, key).not.toHaveProperty(key);
    expect(guest.organize).toBe(3);
    expect(guest.mimic).not.toHaveProperty('hotkeyStatus');
    expect(guest.mimic?.active).toBe(true);
    // The host's own snapshot is untouched.
    expect(host.setupDone).toBe(false);
    expect(host.mimic?.hotkeyStatus).toBe('Saved the last 10 s at 14:02');
  });
});
