import { useLayoutEffect, useRef, useState, type PointerEvent } from 'react';

/**
 * Mimic's live transcript follows the newest words (2.46). It is a short scrolling list with the newest
 * sentence at the bottom; until 2.45 nothing scrolled it, so the word just said was out of sight. While
 * the list is scrolled to its end it stays there as words arrive; scrolling back up stops that (the
 * person is reading something older), and scrolling to the end again, or Jump to live, starts it again.
 */

/** Pixels from the end that still count as "at the newest words" (a wheel step can stop just short). */
export const FOLLOW_SLACK_PX = 24;

/**
 * While the pointer is pressed on the list, the list holds still, and waits this long after release, so a word never slides away under the cursor as a new phrase arrives.
 */
export const AIM_HOLD_MS = 400;

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
  const lastScroll = useRef(0);
  const pausedForEarlierWords = useRef(false);
  const aimed = useRef(-Infinity);
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
    if (el.scrollTop < lastScroll.current - FOLLOW_SLACK_PX && !atEnd(el)) {
      set(false);
      return;
    }
    if (!atEnd(el, 1)) el.scrollTop = el.scrollHeight;
    lastScroll.current = el.scrollTop;
  });
  return {
    ref,
    following: shown,
    pause: () => {
      pausedForEarlierWords.current = true;
      set(false);
    },
    onScroll: () => {
      const el = ref.current;
      if (el) {
        lastScroll.current = el.scrollTop;
        const atNewest = atEnd(el);
        // Prepending a page restores the visible line and may generate a scroll event near the bottom.
        if (pausedForEarlierWords.current && atNewest) return;
        pausedForEarlierWords.current = false;
        if (!following.current || performance.now() - aimed.current >= AIM_HOLD_MS) set(atNewest);
      }
    },
    onPointerDown: (event: PointerEvent<T>) => {
      if (
        event.pointerType !== 'touch' &&
        event.target instanceof Element &&
        event.target.closest('button')
      )
        aimed.current = Infinity;
    },
    onPointerUp: () => {
      if (aimed.current === Infinity) aimed.current = performance.now();
    },
    jump: () => {
      pausedForEarlierWords.current = false;
      aimed.current = -Infinity;
      set(true);
      const el = ref.current;
      if (!el) return;
      el.scrollTop = el.scrollHeight;
      lastScroll.current = el.scrollTop;
      // The list may itself be below the panel's fold: bring its end into view too, at once.
      el.lastElementChild?.scrollIntoView?.({ block: 'nearest', behavior: 'instant' });
    },
  };
}
