import { Check, Info, TriangleAlert, X } from 'lucide-react';
import { createPortal } from 'react-dom';
import { dismissNotice, holdNotice, useNotices } from '../notifications';
import { useNoticeSlot } from './Modal';
/**
 * Notices sit at the bottom right, above the player, clear of the header, the example banner, Stop
 * and Panic (2.41). While a dialog is open they move into it, at its foot, where they never cover
 * its close button and Tab reaches their Undo inside the dialog's focus trap.
 */
export function Notifications() {
  const notices = useNotices();
  const slot = useNoticeSlot();
  const stack = (
    <aside className={`notification-stack${slot ? ' in-dialog' : ''}`} aria-label="Notifications">
      {notices.map((notice) => (
        <div
          className={`toast notice-${notice.kind}`}
          key={notice.id}
          role={notice.kind === 'error' ? 'alert' : 'status'}
          onPointerEnter={() => holdNotice(notice.id, true)}
          onPointerLeave={() => holdNotice(notice.id, false)}
          onFocus={() => holdNotice(notice.id, true)}
          onBlur={(e) => {
            if (!e.currentTarget.contains(e.relatedTarget as Node | null))
              holdNotice(notice.id, false);
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
                dismissNotice(notice.id);
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
        </div>
      ))}
    </aside>
  );
  return slot ? createPortal(stack, slot) : stack;
}
