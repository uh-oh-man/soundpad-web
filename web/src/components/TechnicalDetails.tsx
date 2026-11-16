import { useState } from 'react';
import { Copy } from 'lucide-react';
import '../diagnostics.css';

/**
 * "Technical details" (2.45, D07) under an error: a native disclosure, so it is reached with Tab and opened
 * with Enter or Space, and a screen reader says "Technical details, collapsed". Its text is never in a live
 * region: the notice is announced once by the page's announcer, and the details only when someone opens them.
 * The lines are in the page only while open, so text searches (and tests) find a notice's message once.
 * Copy puts the lines on the clipboard for a report.
 */
export function TechnicalDetails({
  details,
  onToggle,
}: {
  details: string;
  /** Opened or closed: a notice holds still while its details are open. */
  onToggle?: (open: boolean) => void;
}) {
  const [open, setOpen] = useState(false);
  const [copied, setCopied] = useState(false);
  return (
    <details
      className="technical-details"
      onToggle={(e) => {
        const now = (e.currentTarget as HTMLDetailsElement).open;
        setOpen(now);
        if (!now) setCopied(false);
        onToggle?.(now);
      }}
    >
      <summary>Technical details</summary>
      {open && (
        <>
          <pre tabIndex={0} aria-label="Technical details text">
            {details}
          </pre>
          <button
            type="button"
            className="technical-copy"
            onClick={() => {
              void navigator.clipboard
                ?.writeText(details)
                .then(() => setCopied(true))
                .catch(() => setCopied(false));
            }}
          >
            <Copy size={13} aria-hidden="true" /> {copied ? 'Copied' : 'Copy details'}
          </button>
        </>
      )}
    </details>
  );
}
