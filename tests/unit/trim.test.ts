import { describe, it, expect } from 'vitest';
import { trimDrag, trimGrab } from '../../web/src/trim';

describe('Edit copy waveform dragging', () => {
  it('grabs the nearer edge within the tolerance, otherwise starts a new range', () => {
    expect(trimGrab(1.05, 1, 4, 0.2)).toBe('from');
    expect(trimGrab(3.9, 1, 4, 0.2)).toBe('to');
    expect(trimGrab(2.5, 1, 4, 0.2)).toBe('new');
    // Both edges on one spot: pressing past it pulls the end, before it the start.
    expect(trimGrab(2.1, 2, 2, 0.2)).toBe('to');
    expect(trimGrab(1.9, 2, 2, 0.2)).toBe('from');
  });
  it('drags a new range either way from where the press began', () => {
    expect(trimDrag('new', 3, { from: 0, to: 10 }, 1, 10)).toEqual({ from: 1, to: 3 });
    expect(trimDrag('new', 0.5, { from: 0, to: 10 }, 2, 10)).toEqual({ from: 0.5, to: 2 });
  });
  it('moves one edge, swapping when it crosses the other, and stays inside the sound', () => {
    expect(trimDrag('from', 2, { from: 1, to: 4 }, 1, 10)).toEqual({ from: 2, to: 4 });
    expect(trimDrag('from', 6, { from: 1, to: 4 }, 1, 10)).toEqual({ from: 4, to: 6 });
    expect(trimDrag('to', 12, { from: 1, to: 4 }, 4, 10)).toEqual({ from: 1, to: 10 });
    expect(trimDrag('to', -3, { from: 1, to: 4 }, 4, 10)).toEqual({ from: 0, to: 1 });
    expect(trimDrag('new', 1.23456, { from: 0, to: 10 }, 0, 10)).toEqual({ from: 0, to: 1.23 });
  });
});
