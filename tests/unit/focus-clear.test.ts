import { describe, expect, it } from 'vitest';
import { clearShift, FOCUS_MARGIN, revealShift } from '../../web/src/focus-clear';

// A 375 x 812 phone: the player bar at the bottom, notices 16 px above it, full width.
const player = { top: 654, bottom: 812, left: 0, right: 375 };
const notices = { top: 556, bottom: 638, left: 16, right: 359 };
const button = (top: number, left = 30, right = 130) => ({ top, bottom: top + 40, left, right });

describe('clearShift', () => {
  it('leaves a control alone when nothing covers it', () => {
    expect(clearShift(button(300), [notices, player], 0)).toBe(0);
  });
  it('scrolls a control under the notices up to just above them', () => {
    const shift = clearShift(button(600), [notices, player], 0);
    expect(600 + 40 - shift).toBe(notices.top - FOCUS_MARGIN);
  });
  it('clears the notices too when clearing the player bar would leave it under them', () => {
    const shift = clearShift(button(700), [notices, player], 0);
    expect(700 + 40 - shift).toBe(notices.top - FOCUS_MARGIN);
  });
  it('ignores notices beside the control and empty overlays', () => {
    const narrow = { top: 556, bottom: 638, left: 200, right: 359 };
    const empty = { top: 640, bottom: 640, left: 0, right: 375 };
    expect(clearShift(button(600), [narrow, empty], 0)).toBe(0);
  });
  it('never scrolls the control past the top of its scroll area', () => {
    const tall = { top: 120, bottom: 638, left: 16, right: 359 };
    expect(clearShift(button(200), [tall, player], 150)).toBe(50);
  });
});

// The phone's sticky header ends at 142 px (2.43); a dialog's title row works the same way.
describe('revealShift', () => {
  it('leaves a control below the header alone', () => {
    expect(revealShift(button(150), [notices, player], 142)).toBe(0);
  });
  it('scrolls a control under the header down to just below it', () => {
    const shift = revealShift(button(100), [notices, player], 142);
    expect(shift).toBeLessThan(0);
    expect(100 - shift).toBe(142 + FOCUS_MARGIN);
  });
  it('settles for just below the header when the margin would put it under the notices', () => {
    // A tall control that only just fits between the header and the notices.
    const tall = { top: 120, bottom: 120 + (notices.top - 142), left: 30, right: 130 };
    expect(120 - revealShift(tall, [notices, player], 142)).toBe(142);
  });
});
