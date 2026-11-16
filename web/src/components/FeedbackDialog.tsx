import { useEffect, useRef, useState } from 'react';
import { Send } from 'lucide-react';
import { controller } from '../controller';
import { notify } from '../notifications';
import { APP_VERSION } from '../version';
import { Modal } from './Modal';

const KINDS = [
  ['bug', 'Bug'],
  ['idea', 'Idea'],
  ['opinion', 'Opinion'],
] as const;
type Kind = (typeof KINDS)[number][0];
const MAX = 4000;

/**
 * Feedback (2.45): a short note to the host, from the header's Feedback button. The host's companion appends it
 * to one file on the host's desktop ("Soundpad Web feedback.md"); guests are never told the path.
 */
export function FeedbackDialog({
  host,
  where,
  onClose,
}: {
  host: boolean;
  /** What the sender was looking at, saved with the note ("Soundboard", "Voices"...). */
  where: string;
  onClose: () => void;
}) {
  const [kind, setKind] = useState<Kind>('idea');
  const [text, setText] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const field = useRef<HTMLTextAreaElement>(null);
  const trimmed = text.trim();
  // The dialog puts focus on its first button when it opens; the note box takes it right after.
  useEffect(() => {
    const frame = requestAnimationFrame(() => field.current?.focus());
    return () => cancelAnimationFrame(frame);
  }, []);

  async function send() {
    if (busy) return;
    if (!trimmed) {
      setError('Write a note first.');
      field.current?.focus();
      return;
    }
    setBusy(true);
    setError(null);
    try {
      await controller.run(
        'feedbackSend',
        { body: { kind, text: trimmed, where, version: APP_VERSION } },
        { quiet: true },
      );
      notify(
        host
          ? 'Saved to "Soundpad Web feedback.md" on your desktop.'
          : 'Thanks! Your note was sent to the host.',
        'success',
      );
      onClose();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'The note could not be sent. Try again.');
      setBusy(false);
    }
  }

  return (
    <Modal label="Send feedback" onClose={onClose}>
      <form
        className="feedback-form"
        onSubmit={(e) => {
          e.preventDefault();
          void send();
        }}
      >
        <p className="feedback-intro">
          {host
            ? 'Write down a bug, an idea or an opinion. Notes are saved to "Soundpad Web feedback.md" on your desktop.'
            : 'Tell the host what broke, what you would like, or what you think. Your note is saved on their PC with your name.'}
        </p>
        <div className="choice-group" role="radiogroup" aria-labelledby="feedback-kind">
          <span id="feedback-kind">Kind of note</span>
          <span className="choice-options">
            {KINDS.map(([value, label]) => (
              <label key={value}>
                <input
                  type="radio"
                  name="feedback-kind"
                  checked={kind === value}
                  onChange={() => setKind(value)}
                />
                {label}
              </label>
            ))}
          </span>
        </div>
        <label className="field feedback-text">
          Your note
          <textarea
            ref={field}
            rows={6}
            maxLength={MAX}
            value={text}
            placeholder={
              kind === 'bug'
                ? 'What happened, and what did you expect?'
                : kind === 'idea'
                  ? 'What would make this better?'
                  : 'What do you think so far?'
            }
            aria-describedby="feedback-count"
            onChange={(e) => {
              setText(e.target.value);
              if (error) setError(null);
            }}
            onKeyDown={(e) => {
              // Ctrl+Enter sends, like most chat boxes; Enter alone is a new line.
              if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
                e.preventDefault();
                void send();
              }
            }}
          />
        </label>
        <div className="feedback-foot">
          <small id="feedback-count" className="feedback-count">
            {text.length} / {MAX} · Ctrl+Enter sends
          </small>
          {error && (
            <p className="feedback-error" role="alert">
              {error}
            </p>
          )}
          <div className="feedback-actions">
            <button type="button" onClick={onClose}>
              Cancel
            </button>
            <button type="submit" className="primary" aria-disabled={busy}>
              <Send size={16} aria-hidden="true" />
              {busy ? 'Sending…' : 'Send'}
            </button>
          </div>
        </div>
      </form>
    </Modal>
  );
}
