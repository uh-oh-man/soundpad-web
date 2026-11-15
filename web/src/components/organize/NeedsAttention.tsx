import { useState } from 'react';
import { AlertTriangle, Play } from 'lucide-react';
import type { Sound } from '../../protocol';

export const MISSING_REASON = 'The file is missing on the host’s PC';

/**
 * Needs attention (2.44, gate missingFiles): the sounds of this category whose file is missing on the host's
 * PC, at the end of the category. They are never sent to Soundpad to play (its reaction is unknown and may
 * open a dialog mid-game); pressing Play says why instead. Guests see the same, never the path.
 */
export function NeedsAttention({ sounds, host }: { sounds: Sound[]; host: boolean }) {
  const [said, setSaid] = useState<string | null>(null);
  if (!sounds.length) return null;
  return (
    <section className="needs-attention" aria-label={`Needs attention (${sounds.length})`}>
      <h3>
        <AlertTriangle size={16} aria-hidden="true" /> Needs attention ({sounds.length})
      </h3>
      <p className="muted">
        {host
          ? 'Relink or remove these sounds in Soundpad: their files are missing.'
          : 'The host needs to relink or remove these sounds in Soundpad.'}
      </p>
      <ul>
        {sounds.map((sound) => (
          <li key={sound.id} className="needs-attention-item">
            <button
              type="button"
              className="icon-button"
              aria-label={`Play from start: ${sound.name}`}
              aria-disabled="true"
              title={MISSING_REASON}
              onClick={() => setSaid(sound.id)}
            >
              <Play size={13} aria-hidden="true" />
            </button>
            <span className="sound-name">{sound.name}</span>
            <small>
              {said === sound.id ? (
                <span role="status">
                  {MISSING_REASON}. Relink or remove this sound in Soundpad.
                </span>
              ) : (
                'Relink or remove this sound in Soundpad.'
              )}
            </small>
          </li>
        ))}
      </ul>
    </section>
  );
}
