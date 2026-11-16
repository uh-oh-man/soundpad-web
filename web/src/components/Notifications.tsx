import { useEffect, useRef, useState, useSyncExternalStore } from 'react';
import { Check, Info, TriangleAlert, X } from 'lucide-react';
import { createPortal } from 'react-dom';
import {
  dismissNotice,
  holdNotice,
  noticeActionRan,
  onNoticePushed,
  useNotices,
  type Notice,
} from '../notifications';
import { requestEditor } from '../shell';
import { watchFocusClearance } from '../focus-clear';
import { useNoticeSlot } from './Modal';
import { TechnicalDetails } from './TechnicalDetails';

const NARROW = '(max-width: 1000px)';
const narrowNow = () => {
  try {
    return matchMedia(NARROW).matches;
  } catch {
    return false;
  }
};
function useNarrow() {
  return useSyncExternalStore(
    (listener) => {
      const media = matchMedia(NARROW);
      media.addEventListener('change', listener);
      return () => media.removeEventListener('change', listener);
    },
    narrowNow,
    () => false,
  );
}

/**
 * Notices (2.43). On a wide screen they sit in a dock at the foot of the board column, in the page's
 * flow, so they never cover anything: the board scroller gives up the room instead. Up to 3 cards; older
 * ones fold into "N more", which opens Activity. On a narrow screen the dock floats above the player
 * with up to 2 cards and publishes its height as --notice-height, which the page, the board and the
 * editor sheet keep free at the bottom. While a dialog is open the cards move into it, at its foot
 * (2.41). Cards do not announce themselves (`aria-live="off"`): the single announcer below says each
 * notice once, wherever its card is. Info and success cards keep role status; error cards are a named
 * group, not role alert, because a browser raises an alert event for every alert element it creates, so
 * an error was said by the announcer and the card, and again each time a dialog opened or closed and the
 * cards moved into or out of it. Keyboard focus is kept clear of the cards, the player and the sheet's
 * minimized bar: by the CSS that counts --notice-height, and by focus-clear.ts for what it can't reach.
 */
export function Notifications() {
  const notices = useNotices();
  const slot = useNoticeSlot();
  const narrow = useNarrow();
  const dock = useRef<HTMLDivElement>(null);
  const max = slot ? 4 : narrow ? 2 : 3;
  const shown = notices.slice(-max);
  const hidden = notices.length - shown.length;
  useEffect(() => {
    const element = dock.current;
    const root = document.documentElement;
    if (!element || !narrow) {
      root.style.removeProperty('--notice-height');
      return;
    }
    const resize = new ResizeObserver(() => {
      const height = element.getBoundingClientRect().height;
      if (height) root.style.setProperty('--notice-height', `${Math.ceil(height) + 8}px`);
      else root.style.removeProperty('--notice-height');
    });
    resize.observe(element);
    return () => {
      resize.disconnect();
      root.style.removeProperty('--notice-height');
    };
  }, [narrow]);
  useEffect(() => watchFocusClearance([dock.current, slot]), [slot]);
  const stack = (
    <aside className={`notification-stack${slot ? ' in-dialog' : ''}`} aria-label="Notifications">
      {hidden > 0 && !slot && (
        <button
          type="button"
          className="notice-more"
          onClick={() => requestEditor({ kind: 'activity' })}
        >
          {hidden} more {hidden === 1 ? 'notice' : 'notices'}
        </button>
      )}
      {shown.map((notice) => (
        <NoticeCard key={notice.id} notice={notice} />
      ))}
    </aside>
  );
  return (
    <div className="notice-dock" ref={dock}>
      {slot ? createPortal(stack, slot) : notices.length ? stack : null}
    </div>
  );
}

function NoticeCard({ notice }: { notice: Notice }) {
  // An error's technical details (2.45): while they are open the card stays, wherever the pointer goes.
  const details = notice.kind === 'error' ? notice.details : undefined;
  const reading = useRef(false);
  const release = () => {
    if (!reading.current) holdNotice(notice.id, false);
  };
  return (
    <div
      className={`toast notice-${notice.kind}${details ? ' has-details' : ''}`}
      role={notice.kind === 'error' ? 'group' : 'status'}
      aria-label={notice.kind === 'error' ? 'Error notice' : undefined}
      aria-live="off"
      onPointerEnter={() => holdNotice(notice.id, true)}
      onPointerLeave={release}
      onFocus={() => holdNotice(notice.id, true)}
      onBlur={(e) => {
        if (!e.currentTarget.contains(e.relatedTarget as Node | null)) release();
      }}
    >
      {notice.kind === 'error' ? (
        <TriangleAlert size={17} />
      ) : notice.kind === 'success' ? (
        <Check size={17} />
      ) : (
        <Info size={17} />
      )}
      <span>{notice.message}</span>
      {notice.action && (
        <button
          className="toast-action"
          onClick={() => {
            noticeActionRan(notice.id);
            notice.action!.run();
          }}
        >
          {notice.action.label}
        </button>
      )}
      <button
        className="icon-button"
        aria-label="Dismiss notification"
        onClick={() => dismissNotice(notice.id)}
      >
        <X size={15} />
      </button>
      {details && (
        <TechnicalDetails
          details={details}
          onToggle={(open) => {
            reading.current = open;
            holdNotice(notice.id, open);
          }}
        />
      )}
    </div>
  );
}

/**
 * The page's one announcer for notices (2.43): each new notice's text is said once, politely, or at
 * once for errors. It stays mounted outside the app shell, so a dialog making the page inert never
 * silences it. The two live regions sit in a closed shadow root: assistive technology reads them, while
 * page scripts and text lookups (which would otherwise find every notice twice) do not see them. In
 * development the announcements are also listed on `window.__soundpadAnnouncements` for tests.
 */
export function NoticeAnnouncer() {
  const host = useRef<HTMLDivElement>(null);
  const [root, setRoot] = useState<ShadowRoot | null>(null);
  const [said, setSaid] = useState({ polite: '', assertive: '', n: 0 });
  useEffect(() => {
    const element = host.current;
    if (!element || root) return;
    try {
      setRoot(element.attachShadow({ mode: 'closed' }));
    } catch {
      // Already attached (a fast remount): keep the regions out of the page rather than doubled.
    }
  }, [root]);
  useEffect(
    () =>
      onNoticePushed((notice) => {
        if (import.meta.env.DEV) {
          const w = window as unknown as { __soundpadAnnouncements?: string[] };
          (w.__soundpadAnnouncements ??= []).push(notice.message);
        }
        setSaid((current) => {
          // The same words again still change the region (a trailing no-break space comes and goes),
          // so a repeated notice is said again.
          const text = notice.message + (current.n % 2 ? '' : ' ');
          return notice.kind === 'error'
            ? { ...current, assertive: text, n: current.n + 1 }
            : { ...current, polite: text, n: current.n + 1 };
        });
      }),
    [],
  );
  const hidden = {
    position: 'absolute',
    width: 1,
    height: 1,
    margin: -1,
    overflow: 'hidden',
    clip: 'rect(0 0 0 0)',
    whiteSpace: 'nowrap',
  } as const;
  return (
    <div className="notice-announcer" ref={host}>
      {root &&
        createPortal(
          <>
            <div id="notice-live" style={hidden} aria-live="polite" aria-atomic="true">
              {said.polite}
            </div>
            <div id="notice-live-assertive" style={hidden} aria-live="assertive" aria-atomic="true">
              {said.assertive}
            </div>
          </>,
          root,
        )}
    </div>
  );
}
