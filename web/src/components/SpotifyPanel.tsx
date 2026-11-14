import { useEffect, useRef, useState } from 'react';
import { Music2, SkipBack, SkipForward, Play, Pause, ChevronDown } from 'lucide-react';
import { z } from 'zod';
import { controller } from '../controller';
import { usePref } from '../prefs';
const schema = z.object({
  enabled: z.boolean().optional(),
  available: z.boolean(),
  title: z.string().nullable().optional(),
  artist: z.string().nullable().optional(),
  status: z.string().nullable().optional(),
  positionMs: z.number().nullable().optional(),
  durationMs: z.number().nullable().optional(),
  canToggle: z.boolean(),
  canNext: z.boolean(),
  canPrevious: z.boolean(),
  artwork: z
    .string()
    .max(180000)
    .regex(/^data:image\/png;base64,[A-Za-z0-9+/=]+$/)
    .nullable()
    .optional(),
  error: z.string().nullable().optional(),
});
export function SpotifyPanel({ host = true }: { host?: boolean }) {
  const pending = useRef(false),
    version = useRef(0);
  // A press still on its way: its buttons stay focusable (aria-disabled) and ignore presses.
  const [busy, setBusy] = useState(false);
  const [enabled, setEnabled] = useState(false),
    [data, setData] = useState<z.infer<typeof schema> | null>(null),
    [error, setError] = useState('');
  async function request(action: 'status' | 'toggle' | 'next' | 'previous') {
    if (pending.current) return;
    pending.current = true;
    if (action !== 'status') setBusy(true);
    const startedVersion = version.current;
    try {
      const value = schema.parse(await controller.run('spotify', { action }));
      if (startedVersion !== version.current) return;
      setData(value);
      setEnabled(value.enabled ?? true);
      setError('');
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Spotify unavailable.');
    } finally {
      pending.current = false;
      if (action !== 'status') setBusy(false);
    }
  }
  useEffect(() => {
    void request('status');
  }, []);
  useEffect(() => {
    if (!enabled && host) return;
    void request('status');
    const timer = setInterval(() => void request('status'), 3000);
    return () => clearInterval(timer);
  }, [enabled, host]);
  async function toggle(value: boolean) {
    try {
      await controller.run('spotify', { action: value ? 'enable' : 'disable' });
      version.current++;
      setEnabled(value);
      setError('');
      if (!value) setData(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Spotify setting failed.');
    }
  }
  const [expanded, setExpanded] = usePref('spotifyExpanded');
  // One switch element in one place for both states, so it never remounts while it is being pressed:
  // in the collapsed row while off, with the extra controls once on (hidden while they are collapsed).
  const showSwitch = !enabled || expanded;
  const time = (ms: number) =>
    `${Math.floor(ms / 60000)}:${String(Math.floor(ms / 1000) % 60).padStart(2, '0')}`;
  return (
    <section
      className={`spotify-player spotify-compact ${enabled ? 'is-on' : 'is-off'}`}
      aria-label="Spotify player"
    >
      {enabled ? (
        <>
          <div className="spotify-row">
            {/* The artwork is also the "more" switch: the sidebar row has no room for a fourth button. */}
            <button
              className="spotify-art-toggle"
              aria-label="More Spotify controls"
              aria-expanded={expanded}
              aria-controls="spotify-more spotify-switch"
              title={expanded ? 'Fewer Spotify controls' : 'More Spotify controls'}
              onClick={() => setExpanded(!expanded)}
            >
              {data?.available && data.artwork ? (
                <img className="spotify-art" src={data.artwork} alt="Spotify album artwork" />
              ) : (
                <span className="spotify-art spotify-art-placeholder" aria-hidden="true">
                  <Music2 size={18} />
                </span>
              )}
              <span className="spotify-art-chevron" aria-hidden="true">
                <ChevronDown size={12} />
              </span>
            </button>
            <div className="spotify-meta">
              <strong title={data?.available ? (data.title ?? undefined) : undefined}>
                {data?.available ? data.title || 'Track name unavailable' : 'Spotify unavailable'}
              </strong>
              <span title={data?.available ? (data.artist ?? undefined) : undefined}>
                {data?.available
                  ? data.artist || 'Artist unavailable'
                  : data?.error || error || 'Checking Spotify…'}
              </span>
            </div>
            <button
              className="icon-button"
              aria-label="Play or pause Spotify"
              disabled={!data?.available || !data.canToggle}
              aria-disabled={busy || undefined}
              onClick={() => void request('toggle')}
            >
              {data?.status === 'Playing' ? <Pause size={18} /> : <Play size={18} />}
            </button>
            <button
              className="icon-button"
              aria-label="Next Spotify track"
              disabled={!data?.available || !data.canNext}
              aria-disabled={busy || undefined}
              onClick={() => void request('next')}
            >
              <SkipForward size={16} />
            </button>
          </div>
          {expanded && (
            <div id="spotify-more" className="spotify-more">
              <div className="spotify-progress">
                <button
                  className="icon-button"
                  aria-label="Previous Spotify track"
                  disabled={!data?.available || !data.canPrevious}
                  aria-disabled={busy || undefined}
                  onClick={() => void request('previous')}
                >
                  <SkipBack size={16} />
                </button>
                {data?.available && data.durationMs != null && data.durationMs > 0 ? (
                  <>
                    <progress
                      aria-label="Spotify track progress"
                      max={data.durationMs}
                      value={data.positionMs ?? 0}
                    />
                    <small className="tabular">
                      {time(data.positionMs ?? 0)} / {time(data.durationMs)}
                    </small>
                  </>
                ) : (
                  <small>{data?.available ? data.status : ''}</small>
                )}
              </div>
            </div>
          )}
        </>
      ) : (
        <div className="spotify-row">
          <span className="spotify-glyph" aria-hidden="true">
            <Music2 size={16} />
          </span>
          <span className="spotify-off">
            {host ? 'Spotify' : 'Spotify · off'}
            <small>
              {host ? 'Enable your local Spotify player' : 'The host has not enabled it'}
            </small>
          </span>
        </div>
      )}
      {host && (
        <label id="spotify-switch" className="spotify-enable" hidden={!showSwitch}>
          <input
            type="checkbox"
            aria-label="Enable Spotify desktop integration"
            checked={enabled}
            onChange={(e) => void toggle(e.target.checked)}
          />
          {enabled && <span>Desktop integration on</span>}
        </label>
      )}
      {enabled && expanded && (
        <small className="spotify-scope">Spotify only · shared session player</small>
      )}
      {error && (
        <p className="warning" role="alert">
          {error}
        </p>
      )}
    </section>
  );
}
