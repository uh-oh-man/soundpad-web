import { useEffect, useRef, useState } from 'react';
import { Music2, SkipBack, SkipForward, Play, Pause, ChevronDown } from 'lucide-react';
import { z } from 'zod';
import { controller } from '../controller';
import { usePref } from '../prefs';
import { HostOnlyTag } from './SimulatedBadge';
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
type Action = 'status' | 'toggle' | 'next' | 'previous';
/** What a guest's press says when the host turned guest Spotify controls off (2.46). */
export const GUEST_SPOTIFY_OFF = 'The host has turned off guest Spotify controls.';
/** How long after a press the row asks again: Spotify reports a new state a moment after it accepts a press. */
const SETTLE_MS = 700;
/**
 * The compact Spotify row (2.43). On a guest's page it shows and presses the host's Spotify, as the host's
 * "Guest Spotify controls" allow (`guestAllowed`).
 *
 * 2.46 (a guest's "spotify button dont work"): a press never waits for, or is dropped by, a status read
 * that is still on its way. Before, a press during one did nothing at all, and for a guest a status read
 * is on its way much of the time (each crosses the network to the host, and the companion reads Spotify
 * for the host and every guest). A press goes at once, and the status read it overtook is ignored.
 */
export function SpotifyPanel({
  host = true,
  guestAllowed = true,
}: {
  host?: boolean;
  guestAllowed?: boolean;
}) {
  // Every request takes the next number; only the newest one's answer is shown, so an older status
  // read never undoes what a press (or the switch) just showed.
  const latest = useRef(0),
    polling = useRef(false),
    pressing = useRef(false),
    settle = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  // A press still on its way: its buttons stay focusable (aria-disabled) and ignore presses.
  const [busy, setBusy] = useState(false);
  const [enabled, setEnabled] = useState(false),
    [data, setData] = useState<z.infer<typeof schema> | null>(null),
    [error, setError] = useState('');
  const locked = !host && !guestAllowed;
  async function request(action: Action) {
    const press = action !== 'status';
    // A status read waits for nothing and is skipped while another request is on its way. A press is
    // refused only while another press is (its buttons say busy), never because of a status read.
    if (press ? pressing.current : polling.current || pressing.current) return;
    if (press && locked) {
      controller.notify(GUEST_SPOTIFY_OFF, 'info');
      return;
    }
    if (press) {
      pressing.current = true;
      setBusy(true);
    } else polling.current = true;
    const mine = ++latest.current;
    try {
      // Status reads are quiet: one failing every 3 s would repeat its notice. The row shows the reason.
      const value = schema.parse(await controller.run('spotify', { action }, { quiet: !press }));
      if (mine !== latest.current) return;
      if (press && !value.available) {
        // Pressed, but Spotify did not take it (busy, or it refused): say so and keep showing the
        // track and its buttons rather than "Spotify unavailable" until the next status read.
        const reason = value.error || 'Spotify did not take that press. Try again.';
        setError(reason);
        controller.notify(reason, 'error');
      } else {
        setData(value);
        setEnabled(value.enabled ?? true);
        setError('');
      }
      if (press) {
        clearTimeout(settle.current);
        settle.current = setTimeout(() => void request('status'), SETTLE_MS);
      }
    } catch (e) {
      if (mine === latest.current)
        setError(e instanceof Error ? e.message : 'Spotify unavailable.');
    } finally {
      if (press) {
        pressing.current = false;
        setBusy(false);
      } else polling.current = false;
    }
  }
  useEffect(() => () => clearTimeout(settle.current), []);
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
      latest.current++;
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
  // A guest presses the host's Spotify: say so, and why a press does nothing when the host turned it off.
  const press = {
    'aria-disabled': busy || locked || undefined,
    'aria-describedby': locked ? 'spotify-guest-note' : undefined,
    title: locked ? GUEST_SPOTIFY_OFF : host ? undefined : 'Plays on the host’s PC',
  };
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
              {controller.getSnapshot().demo && <HostOnlyTag />}
              <span
                title={
                  data?.available ? (data.artist ?? undefined) : data?.error || error || undefined
                }
              >
                {data?.available
                  ? data.artist || 'Artist unavailable'
                  : data?.error || error || 'Checking Spotify…'}
              </span>
            </div>
            {/* Without a player to control (unavailable, and always in the example) the row has no
                transport, so the name, the Host's PC only tag and the reason have the full width. */}
            {data?.available && (
              <>
                <button
                  className="icon-button spotify-toggle"
                  aria-label="Play or pause Spotify"
                  disabled={!data.canToggle}
                  {...press}
                  onClick={() => void request('toggle')}
                >
                  {data.status === 'Playing' ? <Pause size={18} /> : <Play size={18} />}
                </button>
                <button
                  className="icon-button spotify-next"
                  aria-label="Next Spotify track"
                  disabled={!data.canNext}
                  {...press}
                  onClick={() => void request('next')}
                >
                  <SkipForward size={16} />
                </button>
              </>
            )}
          </div>
          {expanded && (
            <div id="spotify-more" className="spotify-more">
              <div className="spotify-progress">
                <button
                  className="icon-button"
                  aria-label="Previous Spotify track"
                  disabled={!data?.available || !data.canPrevious}
                  {...press}
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
      {enabled && locked && (
        <small id="spotify-guest-note" className="spotify-scope">
          {GUEST_SPOTIFY_OFF}
        </small>
      )}
      {enabled && expanded && !locked && (
        <small className="spotify-scope">
          {host
            ? 'Spotify only · shared session player'
            : 'The host’s Spotify · shared session player'}
        </small>
      )}
      {error && (
        <p className="warning" role="alert">
          {error}
        </p>
      )}
    </section>
  );
}
