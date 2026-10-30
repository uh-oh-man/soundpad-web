import { useEffect, useRef, useState } from 'react';
import { Music2, SkipBack, SkipForward, Play, Pause } from 'lucide-react';
import { z } from 'zod';
import { controller } from '../controller';
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
  const [enabled, setEnabled] = useState(false),
    [data, setData] = useState<z.infer<typeof schema> | null>(null),
    [error, setError] = useState('');
  async function request(action: 'status' | 'toggle' | 'next' | 'previous') {
    if (pending.current) return;
    pending.current = true;
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
  return (
    <section className="spotify-player" aria-label="Spotify player">
      <div className="spotify-heading">
        <span>
          <Music2 size={15} /> Spotify
        </span>
        {host && (
          <input
            type="checkbox"
            aria-label="Enable Spotify desktop integration"
            checked={enabled}
            onChange={(e) => void toggle(e.target.checked)}
          />
        )}
      </div>
      {enabled ? (
        <>
          <div className="spotify-track">
            {data?.available && data.artwork ? (
              <img src={data.artwork} alt="Spotify album artwork" />
            ) : (
              <span className="spotify-art-placeholder">
                <Music2 size={26} />
              </span>
            )}
            <div>
              <strong>
                {data?.available ? data.title || 'Track name unavailable' : 'Spotify unavailable'}
              </strong>
              <span>
                {data?.available
                  ? data.artist || 'Artist unavailable'
                  : data?.error || error || 'Checking Spotify…'}
              </span>
              <small>{data?.available ? data.status : ''}</small>
            </div>
          </div>
          {data?.available && data.durationMs != null && data.durationMs > 0 && (
            <progress
              aria-label="Spotify track progress"
              max={data.durationMs}
              value={data.positionMs ?? 0}
            />
          )}
          <div className="spotify-transport">
            <button
              aria-label="Previous Spotify track"
              disabled={!data?.available || !data.canPrevious}
              onClick={() => void request('previous')}
            >
              <SkipBack size={16} />
            </button>
            <button
              aria-label="Play or pause Spotify"
              disabled={!data?.available || !data.canToggle}
              onClick={() => void request('toggle')}
            >
              {data?.status === 'Playing' ? <Pause size={18} /> : <Play size={18} />}
            </button>
            <button
              aria-label="Next Spotify track"
              disabled={!data?.available || !data.canNext}
              onClick={() => void request('next')}
            >
              <SkipForward size={16} />
            </button>
          </div>
        </>
      ) : (
        <p>
          {host ? 'Enable your local Spotify player.' : 'Waiting for the host to enable Spotify.'}
        </p>
      )}
      <small className="spotify-scope">Spotify only · shared session player</small>
      {error && (
        <p className="warning" role="alert">
          {error}
        </p>
      )}
    </section>
  );
}
