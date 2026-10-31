import { useCallback, useEffect, useRef, useState } from 'react';
import { Captions, Download, Keyboard, Mic2, RefreshCw, Square } from 'lucide-react';
import { z } from 'zod';
import { controller } from '../../controller';
import type { Snapshot } from '../../protocol';
import { supports } from '../../version';
import { preferredSource, sourceKey } from '../../mimic/selection';

const SOURCE_KEY = 'soundpad-mimic-source';
const sourcesSchema = z.object({
  sources: z
    .array(z.object({ id: z.string().max(512), name: z.string().max(600), kind: z.string() }))
    .max(500),
});

/** Keys the companion accepts for the Mimic hotkey (2.14+); F6 is Voice Replay Studio's. */
const HOTKEY_KEYS = ['F6', 'F7', 'F8', 'F9', 'F10'];

function rememberedSource() {
  try {
    return localStorage.getItem(SOURCE_KEY);
  } catch {
    return null; // storage can be unavailable; the default still works
  }
}

/** A checkbox that shows the requested state until the companion's snapshot confirms it. */
function useOptimistic(actual: boolean) {
  const [wanted, setWanted] = useState<boolean | null>(null);
  useEffect(() => {
    if (wanted !== null && wanted === actual) setWanted(null);
  }, [wanted, actual]);
  return [wanted ?? actual, setWanted] as const;
}

/** Host-only controls: what Mimic listens to, transcripts and the F6 hotkey. */
export function MimicHostControls({ state, connected }: { state: Snapshot; connected: boolean }) {
  const mimic = state.mimic;
  const active = !!mimic?.active;
  const [sources, setSources] = useState<z.infer<typeof sourcesSchema>['sources']>([]);
  const [source, setSource] = useState('');
  const [busy, setBusy] = useState(false);
  // Read synchronously so a second click during a slow command is ignored even before React re-renders.
  const running = useRef(false);
  const [transcribing, setWantTranscript] = useOptimistic(!!mimic?.transcribing);
  const [hotkey, setWantHotkey] = useOptimistic(!!mimic?.hotkey);
  const hotkeyKey = mimic?.hotkeyKey ?? 'F6';

  const loadSources = useCallback(async () => {
    try {
      const parsed = sourcesSchema.parse(await controller.run('mimicSources'));
      setSources(parsed.sources);
      setSource((current) => preferredSource(parsed.sources, current, rememberedSource()));
    } catch {
      /* Controller shows the error. */
    }
  }, []);
  useEffect(() => {
    if (connected) void loadSources();
  }, [connected, loadSources]);

  async function command(run: () => Promise<unknown>, onError?: () => void) {
    running.current = true;
    setBusy(true);
    await run().catch(() => onError?.());
    running.current = false;
    setBusy(false);
  }

  return (
    <>
      <div className="mimic-source">
        <label>
          Listen to
          <select
            aria-label="Mimic source"
            value={source}
            disabled={busy || !sources.length}
            onChange={(e) => setSource(e.target.value)}
          >
            {sources.map((s) => (
              <option key={s.id} value={s.id}>
                {s.name}
              </option>
            ))}
          </select>
        </label>
        <button
          className="icon-button"
          aria-label="Refresh Mimic sources"
          title="Refresh sources"
          disabled={busy}
          onClick={() => void loadSources()}
        >
          <RefreshCw size={16} />
        </button>
        {active ? (
          <button disabled={busy} onClick={() => void command(() => controller.run('mimicStop'))}>
            <Square size={15} /> Stop listening
          </button>
        ) : (
          <button
            className="primary"
            disabled={busy || !source || !connected}
            onClick={() =>
              void command(async () => {
                const name = sources.find((s) => s.id === source)?.name;
                await controller.run('mimicStart', { sourceId: source });
                try {
                  if (name) localStorage.setItem(SOURCE_KEY, sourceKey(name));
                } catch {
                  /* Remembering the source is optional. */
                }
              })
            }
          >
            <Mic2 size={15} /> Start listening
          </button>
        )}
      </div>
      {supports(state.companionVersion, 'transcripts') && (
        <div className="mimic-transcribe">
          <Captions size={16} />
          {mimic?.transcriptModel ? (
            <label>
              <input
                type="checkbox"
                aria-label="Transcribe Mimic audio"
                checked={transcribing}
                aria-disabled={busy || undefined}
                onChange={(e) => {
                  if (running.current) return;
                  const on = e.target.checked;
                  setWantTranscript(on);
                  void command(
                    () => controller.run('mimicTranscribe', { action: on ? 'enable' : 'disable' }),
                    () => setWantTranscript(null),
                  );
                }}
              />
              Transcript
            </label>
          ) : (
            <button
              disabled={busy || mimic?.transcriptStatus?.startsWith('Downloading')}
              onClick={() =>
                void command(() => controller.run('mimicTranscribe', { action: 'download' }))
              }
            >
              <Download size={15} /> Get transcripts (148 MB, once)
            </button>
          )}
          <small>
            {mimic?.transcriptStatus && mimic.transcriptStatus !== 'Off'
              ? mimic.transcriptStatus
              : 'Speech is recognized on this PC; only the text is shared.'}
          </small>
        </div>
      )}
      {supports(state.companionVersion, 'hotkey') && (
        <div className="mimic-transcribe">
          <Keyboard size={16} />
          <label>
            {/* aria-disabled, not disabled, while busy: Chrome drops focus from a control that becomes
                disabled, and keyboard users would lose their place after every tick. */}
            <input
              type="checkbox"
              aria-label={`${hotkeyKey} hotkey`}
              checked={hotkey}
              aria-disabled={busy || undefined}
              onChange={(e) => {
                if (running.current) return;
                const on = e.target.checked;
                setWantHotkey(on);
                void command(
                  () => controller.run('mimicHotkey', { action: on ? 'enable' : 'disable' }),
                  () => setWantHotkey(null),
                );
              }}
            />
            {/* With the key picker next to it, the label needn't repeat the key. */}
            {supports(state.companionVersion, 'hotkeyKey') ? 'Hotkey' : `${hotkeyKey} hotkey`}
          </label>
          {/* Another key when a game or app already uses F6. */}
          {supports(state.companionVersion, 'hotkeyKey') && (
            <select
              aria-label="Hotkey key"
              value={hotkeyKey}
              aria-disabled={busy || undefined}
              onChange={(e) => {
                if (running.current) return;
                const key = e.target.value;
                void command(() => controller.run('mimicHotkey', { action: 'key', name: key }));
              }}
            >
              {HOTKEY_KEYS.map((k) => (
                <option key={k}>{k}</option>
              ))}
            </select>
          )}
          {supports(state.companionVersion, 'hotkeyLength') && (
            <label>
              saves the last
              <select
                aria-label={`${hotkeyKey} saves the last`}
                value={mimic?.hotkeySeconds ?? 10}
                aria-disabled={busy || undefined}
                onChange={(e) =>
                  !running.current &&
                  void controller
                    .run('mimicHotkey', {
                      action: 'length',
                      positionMs: Number(e.target.value) * 1000,
                    })
                    .catch(() => {})
                }
              >
                {[3, 5, 10, 15, 30].map((s) => (
                  <option key={s} value={s}>
                    {s} s
                  </option>
                ))}
              </select>
            </label>
          )}
          <small>
            {mimic?.hotkeyStatus && mimic.hotkeyStatus !== 'Off'
              ? mimic.hotkeyStatus
              : `Press ${hotkeyKey} in any app on this PC to save the last ${mimic?.hotkeySeconds ?? 10} seconds, like Voice Replay Studio.`}
          </small>
        </div>
      )}
    </>
  );
}
