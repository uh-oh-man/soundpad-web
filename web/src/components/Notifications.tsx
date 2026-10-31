import { Check, Info, TriangleAlert, X } from 'lucide-react';
import { dismissNotice, useNotices } from '../notifications';
export function Notifications() {
  const notices = useNotices();
  return (
    <aside className="notification-stack" aria-label="Notifications">
      {notices.map((notice) => (
        <div
          className={`toast notice-${notice.kind}`}
          key={notice.id}
          role={notice.kind === 'error' ? 'alert' : 'status'}
        >
          {notice.kind === 'error' ? (
            <TriangleAlert size={17} />
          ) : notice.kind === 'success' ? (
            <Check size={17} />
          ) : (
            <Info size={17} />
          )}
          <span>{notice.message}</span>
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
