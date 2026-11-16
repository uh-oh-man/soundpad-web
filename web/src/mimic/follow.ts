import { useLayoutEffect, useRef, useState } from 'react';

/**
 * Mimic's live transcript follows the newest words (2.46). It is a short scrolling list with the newest
 * sentence at the bottom; until 2.45 nothing scrolled it, so the word just said was out of sight. While
 * the list is scrolled to its end it stays there as words arrive; scrolling back up stops that (the
 * person is reading something older), and scrolling to the end again, or Jump to live, starts it again.
 */

/** Pixels from the end that still count as "at the newest words" (a wheel step can stop just short). */
export const FOLLOW_SLACK_PX = 24;

/**
 * While the pointer moves over the list (someone aiming at a word), the list holds still this long after
 * the last movement, so a word never slides away under the cursor as a new phrase arrives.
 */
export const AIM_HOLD_MS = 1200;

type ScrollBox = { scrollTop: number; scrollHeight: number; clientHeight: number };

/** Whether a scroll box shows its end. A list too short to scroll always does. */
export function atEnd(box: ScrollBox, slack = FOLLOW_SLACK_PX) {
  return box.scrollHeight - box.clientHeight - box.scrollTop <= slack;
}

/**
 * Keeps the element on `ref` scrolled to its end after every render while it is following (new words, a
 * sentence that grew, a time label that wrapped a line). `onScroll` goes on the element; `jump` scrolls
 * it (and the panel around it) to the newest words and follows again.
 */
export function useFollowNewest<T extends HTMLElement>() {
  const ref = useRef<T>(null);
  const following = useRef(true);
  const aimed = useRef(0);
  const [shown, setShown] = useState(true);
  const set = (on: boolean) => {
    if (following.current === on) return;
    following.current = on;
    setShown(on);
  };
  // No dependencies: it runs after each render (twice a second while listening), and only writes when
  // the list is not at its very end.
  useLayoutEffect(() => {
    const el = ref.current;
    if (!el || !following.current || performance.now() - aimed.current < AIM_HOLD_MS) return;
    if (!atEnd(el, 1)) el.scrollTop = el.scrollHeight;
  });
  return {
    ref,
    following: shown,
    onScroll: () => {
      const el = ref.current;
      if (el) set(atEnd(el));
    },
    onPointerMove: (e: { pointerType: string }) => {
      if (e.pointerType === 'mouse' || e.pointerType === 'pen') aimed.current = performance.now();
    },
    onPointerLeave: () => {
      aimed.current = 0;
    },
    jump: () => {
      aimed.current = 0;
      set(true);
      const el = ref.current;
      if (!el) return;
      el.scrollTop = el.scrollHeight;
      // The list may itself be below the panel's fold: bring its end into view too, at once.
      el.lastElementChild?.scrollIntoView?.({ block: 'nearest', behavior: 'instant' });
    },
  };
}
