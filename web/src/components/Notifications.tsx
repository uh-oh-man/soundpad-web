import { Check, Info, TriangleAlert, X } from 'lucide-react';
import { dismissNotice, holdNotice, useNotices } from '../notifications';
export function Notifications() {
  const notices = useNotices();
  return (
    <aside className="notification-stack" aria-label="Notifications">
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
}
