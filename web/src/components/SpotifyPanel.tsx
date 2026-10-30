import { useEffect, useState } from 'react';
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
  error: z.string().nullable().optional(),
});
export function SpotifyPanel() {
  const [enabled, setEnabled] = useState(false),
    [data, setData] = useState<z.infer<typeof schema> | null>(null),
    [error, setError] = useState('');
  async function request(action: 'status' | 'toggle' | 'next' | 'previous') {
    try {
      const value = schema.parse(await controller.run('spotify', { action }));
      setData(value);
      setEnabled(value.enabled ?? true);
      setError('');
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Spotify unavailable.');
    }
  }
  useEffect(() => {
    void request('status');
  }, []);
  useEffect(() => {
    if (!enabled) return;
    void request('status');
    const timer = setInterval(() => void request('status'), 3000);
    return () => clearInterval(timer);
  }, [enabled]);
  async function toggle(value: boolean) {
    try {
      await controller.run('spotify', { action: value ? 'enable' : 'disable' });
      setEnabled(value);
      if (!value) setData(null);
    } catch {
      /* The controller reports command errors; browser storage is optional for display names. */
    }
  }
  return (
    <section className="spotify-panel">
      <div className="setting-row">
        <span>
          <Music2 size={17} /> Spotify desktop <small>Optional · host only</small>
        </span>
        <input
          type="checkbox"
          aria-label="Enable Spotify desktop integration"
          checked={enabled}
          onChange={(e) => void toggle(e.target.checked)}
        />
      </div>
      {enabled && (
        <>
          <strong>{data?.available ? data.title : 'Spotify unavailable'}</strong>
          <p className="muted">
            {data?.available
              ? data.artist
              : data?.error || error || 'Checking the local Spotify media session…'}
          </p>
          {data?.available && (
            <>
              <small>{data.status}</small>
              {data.durationMs != null && data.durationMs > 0 && (
                <progress
                  aria-label="Spotify track progress"
                  max={data.durationMs}
                  value={data.positionMs ?? 0}
                />
              )}
            </>
          )}
          <div className="transport">
            <button
              aria-label="Previous Spotify track"
              disabled={!data?.available || !data.canPrevious}
              onClick={() => void request('previous')}
            >
              <SkipBack size={17} />
            </button>
            <button
              aria-label="Play or pause Spotify"
              disabled={!data?.available || !data.canToggle}
              onClick={() => void request('toggle')}
            >
              {data?.status === 'Playing' ? <Pause size={17} /> : <Play size={17} />}
            </button>
            <button
              aria-label="Next Spotify track"
              disabled={!data?.available || !data.canNext}
              onClick={() => void request('next')}
            >
              <SkipForward size={17} />
            </button>
          </div>
          <p className="muted">
            Controls only the Spotify Windows media session. Audio stays in Spotify; nothing is
            streamed or rerouted.
          </p>
        </>
      )}
    </section>
  );
}
