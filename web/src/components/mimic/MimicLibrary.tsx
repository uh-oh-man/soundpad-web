import { useEffect, useState } from 'react';
import { Download, Library, RefreshCw } from 'lucide-react';
import { z } from 'zod';
import { controller } from '../../controller';
import { formatTime } from '../Waveform';

const librarySchema = z.object({
  available: z.boolean(),
  clips: z
    .array(
      z.object({
        id: z.uuid(),
        title: z.string().max(200),
        transcript: z.string().max(600),
        capturedAt: z.number(),
        durationMs: z.number().int().nonnegative(),
      }),
    )
    .max(500),
});
type Clip = z.infer<typeof librarySchema>['clips'][number];

/**
 * Host only: clips saved in Voice Replay Studio (the desktop app this window grew from) on this PC.
 * Importing copies one into Temp; Voice Replay Studio's own files are never changed.
 */
export function MimicLibrary({ connected }: { connected: boolean }) {
  const [open, setOpen] = useState(false);
  const [clips, setClips] = useState<Clip[] | null>(null);
  const [failed, setFailed] = useState(false);
  const [available, setAvailable] = useState(true);
  const [filter, setFilter] = useState('');
  const [importing, setImporting] = useState('');
  async function load() {
    setFailed(false);
    try {
      const result = librarySchema.parse(await controller.run('mimicLibrary'));
      setAvailable(result.available);
      setClips(result.clips);
    } catch {
      setFailed(true); // the controller's notice says why
    }
  }
  // Load when opened, and again if the companion reconnects while the section is open.
  useEffect(() => {
    if (open && connected) void load();
  }, [open, connected]);
  async function importClip(clip: Clip) {
    if (importing) return;
    setImporting(clip.id);
    try {
      await controller.run('mimicLibraryImport', { sourceId: clip.id });
      controller.notify(`Imported “${clip.title}” into Temp.`, 'success');
    } catch {
      /* The controller shows the error. */
    } finally {
      setImporting('');
    }
  }
  const words = filter.trim().toLowerCase();
  const shown = (clips ?? []).filter(
    (c) => !words || `${c.title} ${c.transcript}`.toLowerCase().includes(words),
  );
  return (
    <details className="mimic-library" onToggle={(e) => setOpen(e.currentTarget.open)}>
      <summary>
        <Library size={15} aria-hidden="true" /> From Voice Replay Studio
      </summary>
      {failed ? (
        <p className="muted">
          Voice Replay Studio's library couldn't be read.{' '}
          <button onClick={() => void load()}>Try again</button>
        </p>
      ) : clips === null ? (
        <p className="muted">{connected ? 'Loading…' : 'Waiting for the companion…'}</p>
      ) : !available ? (
        <p className="muted">
          Voice Replay Studio has no library on this PC. Clips saved there (with its F6) appear
          here.
        </p>
      ) : !clips.length ? (
        <p className="muted">Voice Replay Studio's library has no clips with audio.</p>
      ) : (
        <>
          {clips.length > 6 && (
            <input
              type="search"
              aria-label="Search Voice Replay Studio clips"
              placeholder="Search titles and transcripts"
              value={filter}
              onChange={(e) => setFilter(e.target.value)}
            />
          )}
          <ul aria-label="Voice Replay Studio clips">
            {shown.map((clip) => (
              <li key={clip.id}>
                <span className="mimic-library-text">
                  <strong>{clip.title}</strong>
                  <small>
                    {formatTime(clip.durationMs)} · {new Date(clip.capturedAt).toLocaleString()}
                    {clip.transcript && clip.transcript !== clip.title && ` · ${clip.transcript}`}
                  </small>
                </span>
                <button
                  aria-label={`Import ${clip.title}`}
                  aria-disabled={!!importing || undefined}
                  aria-busy={importing === clip.id || undefined}
                  onClick={() => void importClip(clip)}
                >
                  <Download size={14} aria-hidden="true" /> Import
                </button>
              </li>
            ))}
          </ul>
          <p className="muted mimic-library-note">
            Importing copies a clip into Temp, like a Mimic clip. Voice Replay Studio keeps its own
            copy unchanged.
            <button title="Show clips saved since this list opened" onClick={() => void load()}>
              <RefreshCw size={13} aria-hidden="true" /> Refresh
            </button>
          </p>
        </>
      )}
    </details>
  );
}
