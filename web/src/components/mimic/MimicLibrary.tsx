import { useState } from 'react';
import { Download, Library } from 'lucide-react';
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
  const [clips, setClips] = useState<Clip[] | null>(null);
  const [available, setAvailable] = useState(true);
  const [filter, setFilter] = useState('');
  const [importing, setImporting] = useState('');
  async function load() {
    try {
      const result = librarySchema.parse(await controller.run('mimicLibrary'));
      setAvailable(result.available);
      setClips(result.clips);
    } catch {
      setClips([]); // the controller shows why
    }
  }
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
    <details
      className="mimic-library"
      onToggle={(e) => {
        if (e.currentTarget.open && connected) void load();
      }}
    >
      <summary>
        <Library size={15} aria-hidden="true" /> From Voice Replay Studio
      </summary>
      {clips === null ? (
        <p className="muted">Loading…</p>
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
                  onClick={() => void importClip(clip)}
                >
                  <Download size={14} aria-hidden="true" /> Import
                </button>
              </li>
            ))}
          </ul>
          <p className="muted">
            Importing copies a clip into Temp, like a Mimic clip. Voice Replay Studio keeps its own
            copy unchanged.
          </p>
        </>
      )}
    </details>
  );
}
