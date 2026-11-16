import { useState } from 'react';
import { z } from 'zod';
import { useStageH } from '../../stage-h';
import { supports } from '../../version';
import '../../diagnostics.css';

export const logTailSchema = z.object({
  exists: z.boolean(),
  file: z.string().max(4096),
  size: z.number().nonnegative(),
  redacted: z.boolean(),
  lines: z.array(z.string().max(2100)).max(500),
});
export type LogTail = z.infer<typeof logTailSchema>;
/** How many of the log's last lines Settings shows. */
export const LOG_LINES = 200;

/**
 * Settings → Companion log (2.45, gate diagnostics, D07): the end of companion.log, for when something went
 * wrong and the host wants to see or share what the companion said. Host only (the companion refuses guests),
 * never in the example, hidden for an older companion. Shown on request, not on every opening of Settings.
 * "Hide file paths" asks the companion for a copy without folders or the Windows user name, for sharing.
 */
export function DiagnosticsSection() {
  const { state, mode, demo } = useStageH();
  if (mode !== 'host' || demo || !supports(state.companionVersion, 'diagnostics')) return null;
  return <CompanionLog />;
}

function CompanionLog() {
  const { controller } = useStageH();
  const [tail, setTail] = useState<LogTail | null>(null);
  const [hide, setHide] = useState(false);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [copied, setCopied] = useState(false);
  async function load(redact = hide) {
    if (loading) return;
    setLoading(true);
    setError('');
    setCopied(false);
    try {
      const reply = await controller.run(
        'diagnosticsLog',
        { body: { lines: LOG_LINES, redact } },
        { quiet: true },
      );
      setTail(logTailSchema.parse(reply));
    } catch (e) {
      setError(e instanceof Error ? e.message : 'The companion log could not be read.');
    } finally {
      setLoading(false);
    }
  }
  const text = tail?.lines.join('\n') ?? '';
  return (
    <section className="appearance-setting diagnostics-setting" aria-label="Companion log">
      <h3>Companion log</h3>
      <p className="muted">
        What the companion wrote lately, newest last. It stays on this PC unless you copy it.
      </p>
      <div className="companion-log-tools">
        <button type="button" aria-disabled={loading || undefined} onClick={() => void load()}>
          {tail ? 'Refresh log' : 'Show the log'}
        </button>
        <label>
          <input
            type="checkbox"
            checked={hide}
            onChange={(e) => {
              setHide(e.target.checked);
              if (tail) void load(e.target.checked);
            }}
          />{' '}
          Hide file paths
        </label>
        {tail && tail.lines.length > 0 && (
          <button
            type="button"
            onClick={() => {
              void navigator.clipboard
                ?.writeText(text)
                .then(() => setCopied(true))
                .catch(() => setCopied(false));
            }}
          >
            {copied ? 'Copied' : 'Copy log'}
          </button>
        )}
      </div>
      {error && <p className="inline-error">{error}</p>}
      {tail &&
        (tail.lines.length ? (
          <>
            <pre
              className="companion-log"
              tabIndex={0}
              aria-label={`Companion log, last ${tail.lines.length} lines`}
            >
              {text}
            </pre>
            <p className="muted">
              {tail.file}
              {tail.redacted ? ' (file paths hidden)' : ''}
            </p>
          </>
        ) : (
          <p className="muted">
            {tail.exists
              ? 'The log is empty.'
              : 'There is no log yet: the companion writes one as it runs.'}
          </p>
        ))}
    </section>
  );
}
