import {
  useEffect,
  useMemo,
  useRef,
  useState,
  type CSSProperties,
  type MutableRefObject,
  type KeyboardEvent as ReactKeyboardEvent,
  type PointerEvent as ReactPointerEvent,
} from 'react';
import {
  AlertTriangle,
  Circle,
  Headphones,
  Pin,
  Play,
  Plus,
  RotateCcw,
  Save,
  Shuffle,
  Square,
  Star,
  Trash2,
} from 'lucide-react';
import { controller } from '../controller';
import type { Command, SentenceInput, Snapshot } from '../protocol';
import { usePreviewAudio } from '../preview-audio';
import { useVoiceStore } from '../library/voice-store';
import { refreshSentences, useSentenceStore } from '../library/sentence-store';
import type { Voice } from '../library/voices';
import {
  alternativesSchema,
  changedSchema,
  fillTemplate,
  formatMs,
  itemRef,
  missingMarker,
  msOfPx,
  overlapping,
  paletteSchema,
  performanceSchema,
  pieceLabel,
  placeAt,
  prefersReducedMotion,
  prune,
  pxOf,
  sameRef,
  select,
  sentenceRights,
  sentenceSchema,
  setsSchema,
  sourceProblem,
  takeSchema,
  templateSlots,
  timelineKey,
  wordMarks,
  type Alternatives,
  type PaletteItem,
  type PerformanceSet,
  type Piece,
  type Selection,
  type Sentence,
} from '../library/sentences';
import { VoiceAvatar } from './VoiceShared';

type Props = {
  state: Snapshot;
  host: boolean;
  connected: boolean;
  canPlay: boolean;
  openId?: string | null;
};
type Tab = 'sentences' | 'palette' | 'sets';

/**
 * Sentences (2.42): sentences built from real recorded words, phrases, pauses and named nonverbal
 * pieces on a timeline; the speaking palette with Record performance; performance sets. Typing
 * matches without playing anything; Preview is private; Play into game plays the revision it was
 * pressed at. Missing words stay marked (a gap or a bleep), never filled in by themselves.
 */
export function SentencesPanel({ state, host, connected, canPlay, openId = null }: Props) {
  const { list } = useSentenceStore();
  // The list's flags are the session's; the host's setting for this guest narrows them (2.42 review).
  const rights = sentenceRights(list, state.permissions, host);
  const [tab, setTab] = useState<Tab>('sentences');
  const [open, setOpen] = useState<string | null>(openId);
  useEffect(() => {
    void refreshSentences();
  }, []);
  const tabs: [Tab, string][] = [
    ['sentences', 'Sentences'],
    ['palette', 'Palette'],
    ['sets', 'Sets'],
  ];
  return (
    <div className="voices-panel sentences-panel">
      <p className="muted voices-intro">
        Every word comes from a recording in the voice library. Missing words stay marked and play
        as a gap or a bleep.
      </p>
      {list?.problem && (
        <p className="warning" role="status">
          {list.problem}
        </p>
      )}
      <div className="voices-tabs" role="tablist" aria-label="Sentence sections">
        {tabs.map(([key, label]) => (
          <button
            key={key}
            type="button"
            role="tab"
            aria-selected={tab === key}
            onClick={() => {
              setTab(key);
              if (key === 'sentences') setOpen(null);
            }}
          >
            {label}
          </button>
        ))}
      </div>
      {tab === 'sentences' &&
        (open ? (
          <Builder
            id={open}
            canPlay={canPlay && connected}
            canKeep={rights.canKeep}
            canPlayParts={rights.canPlayParts}
            canEdit={rights.canEdit}
            back={() => setOpen(null)}
          />
        ) : (
          <SentenceList open={setOpen} canBuild={rights.canBuild} />
        ))}
      {tab === 'palette' && (
        <PalettePanel
          canPlay={canPlay && connected}
          canGame={canPlay && connected && rights.canPlayParts}
          canBuild={rights.canBuild}
          openTake={(id) => {
            setOpen(id);
            setTab('sentences');
          }}
        />
      )}
      {tab === 'sets' && (
        <SetsPanel
          canBuild={rights.canBuild}
          openSentence={(id) => {
            setOpen(id);
            setTab('sentences');
          }}
        />
      )}
      {!state.available && <p className="muted">Soundpad is not available right now.</p>}
    </div>
  );
}

function useVoices() {
  const { status } = useVoiceStore();
  return status?.voices ?? [];
}
const voiceById = (voices: Voice[], id: string | null | undefined) =>
  (id && voices.find((v) => v.id === id)) || null;

async function create(text: string, origin?: 'typed' | 'template') {
  const reply = sentenceSchema.parse(
    await controller.run('sentenceCreate', { sentence: { text, ...(origin && { origin }) } }),
  );
  await refreshSentences();
  return reply;
}

/**
 * A list setting changed by quick clicks (2.42 review): each change builds on the last one asked for,
 * not on the last reply, and the changes are sent one after another, so none undoes another.
 */
function useListSetting<T>(current: T[], send: (next: T[]) => Promise<unknown>) {
  const [pending, setPending] = useState<T[] | null>(null);
  const latest = useRef<T[] | null>(null);
  const chain = useRef<Promise<unknown>>(Promise.resolve());
  const change = (update: (list: T[]) => T[]) => {
    const next = update(latest.current ?? current);
    latest.current = next;
    setPending(next);
    chain.current = chain.current
      .then(() => send(next))
      .catch(() => {})
      .finally(() => {
        if (latest.current !== next) return;
        latest.current = null;
        setPending(null);
      });
  };
  return [pending ?? current, change] as const;
}

function SentenceList({ open, canBuild }: { open: (id: string) => void; canBuild: boolean }) {
  const { list, failed } = useSentenceStore();
  const [text, setText] = useState('');
  const [busy, setBusy] = useState(false);
  if (!list)
    return (
      <p className="muted" role="status">
        {failed ? 'The sentences could not be loaded. Trying again…' : 'Loading sentences…'}
      </p>
    );
  const kept = list.sentences.filter((s) => s.kept);
  const projects = list.sentences.filter((s) => !s.kept);
  const row = (s: (typeof list.sentences)[number]) => (
    <li key={s.id} className="sentence-row">
      <button type="button" className="sentence-open" onClick={() => open(s.id)}>
        <strong>{s.title || s.text || 'Untitled sentence'}</strong>
        <span className="muted">
          {s.text}
          {s.kept && ` · version ${s.versions} · ${formatMs(s.durationMs)}`}
          {!s.mine && ` · by ${s.ownerName}`}
          {s.origin === 'performance' && ' · performance'}
        </span>
      </button>
      {s.pinned && <span className="badge">Pinned</span>}
      {s.missing > 0 && <span className="badge warning-badge">{s.missing} missing</span>}
    </li>
  );
  return (
    <div className="sentences-list">
      {failed && (
        <p className="warning" role="status">
          The list could not be refreshed. Trying again…
        </p>
      )}
      {canBuild && (
        <form
          className="voices-inline"
          onSubmit={(e) => {
            e.preventDefault();
            if (busy || !text.trim()) return;
            setBusy(true);
            void create(text)
              .then((s) => {
                setText('');
                open(s.id);
              })
              .catch(() => {})
              .finally(() => setBusy(false));
          }}
        >
          <input
            aria-label="New sentence"
            placeholder="Type a sentence to build from recorded words"
            value={text}
            maxLength={4000}
            onChange={(e) => setText(e.target.value)}
          />
          <button type="submit" className="primary" aria-disabled={busy || undefined}>
            <Plus size={14} /> New sentence
          </button>
        </form>
      )}
      <h3>Kept sentences</h3>
      {kept.length === 0 ? (
        <p className="muted">Nothing kept yet. Keep a sentence to play it from the board.</p>
      ) : (
        <ul className="sentence-rows" aria-label="Kept sentences">
          {kept.map(row)}
        </ul>
      )}
      <h3>Your sentences in progress</h3>
      {projects.length === 0 ? (
        <p className="muted">
          None. Unfinished sentences stay here until you keep or discard them.
        </p>
      ) : (
        <ul className="sentence-rows" aria-label="Sentences in progress">
          {projects.map(row)}
        </ul>
      )}
    </div>
  );
}

// ---- The builder ---------------------------------------------------------------------------------

function Builder({
  id,
  canPlay,
  canKeep,
  canPlayParts,
  canEdit,
  back,
}: {
  id: string;
  canPlay: boolean;
  canKeep: boolean;
  canPlayParts: boolean;
  canEdit: (canEdit: boolean) => boolean;
  back: () => void;
}) {
  const { list } = useSentenceStore();
  const voices = useVoices();
  const preview = usePreviewAudio();
  const [sentence, setSentence] = useState<Sentence | null>(null);
  const [loadFailed, setLoadFailed] = useState(false);
  const [draft, setDraft] = useState('');
  const [selection, setSelection] = useState<Selection>([]);
  const [busy, setBusy] = useState(false);
  const typing = useRef(0);
  const typedAt = useRef(0);
  // Typed text not sent yet (it goes 350 ms after the last key), and the last one on its way.
  const pendingText = useRef<{ timer: number; value: string } | null>(null);
  const sendingText = useRef<Promise<void> | null>(null);
  const refocus = useRef<string[] | null>(null);
  const retry = useRef({ timer: 0, delay: 1000 });
  const reduced = useMemo(prefersReducedMotion, []);

  const load = async () => {
    window.clearTimeout(retry.current.timer);
    try {
      const s = sentenceSchema.parse(
        await controller.run('sentenceGet', { sentenceId: id }, { quiet: true }),
      );
      setSentence(s);
      setLoadFailed(false);
      retry.current.delay = 1000;
      // Someone else's change (or a reply that came later) never replaces what is being typed.
      if (Date.now() - typedAt.current > 1500) setDraft(s.text);
    } catch {
      // A refusal or a short reconnect keeps what is shown (2.42 review) and tries again shortly.
      setLoadFailed(true);
      retry.current.timer = window.setTimeout(() => void load(), retry.current.delay);
      retry.current.delay = Math.min(30000, retry.current.delay * 2);
    }
  };
  useEffect(() => {
    void load();
  }, [id, list?.revision]);
  useEffect(
    () => () => {
      window.clearTimeout(retry.current.timer);
      retry.current.delay = 0;
    },
    [],
  );
  useEffect(() => {
    if (sentence) setSelection((current) => prune(current, sentence.pieces));
  }, [sentence]);

  const apply = async (edit: SentenceInput, quiet = false) => {
    try {
      const { sentence: next, result } = changedSchema.parse(
        await controller.run('sentenceEdit', { sentenceId: id, sentence: edit }, { quiet }),
      );
      setSentence(next);
      return result;
    } catch {
      return undefined; // the controller shows the refusal
    }
  };
  // Typing matches live: the text goes to the companion a moment after the last key; nothing plays.
  const sendText = (value: string, mine: number) => {
    const sending = controller
      .run('sentenceText', { sentenceId: id, sentence: { text: value } }, { quiet: true })
      .then((reply) => {
        if (mine === typing.current) setSentence(changedSchema.parse(reply).sentence);
      })
      .catch(() => {})
      .finally(() => {
        if (sendingText.current === sending) sendingText.current = null;
      });
    sendingText.current = sending;
    return sending;
  };
  const type = (value: string) => {
    setDraft(value);
    typedAt.current = Date.now();
    const mine = ++typing.current;
    if (pendingText.current) window.clearTimeout(pendingText.current.timer);
    pendingText.current = {
      value,
      timer: window.setTimeout(() => {
        pendingText.current = null;
        void sendText(value, mine);
      }, 350),
    };
  };
  /**
   * Sends text typed in the last 350 ms now and waits until the companion has it (2.42 review): Keep,
   * Play into game and Preview act on the companion's revision, which must be what the box shows.
   */
  const flush = async () => {
    const waiting = pendingText.current;
    if (waiting) {
      window.clearTimeout(waiting.timer);
      pendingText.current = null;
      await sendText(waiting.value, typing.current);
    } else if (sendingText.current) await sendingText.current;
  };
  const textPending = () => !!pendingText.current || !!sendingText.current;
  // Closing the window or the builder still saves what was typed.
  useEffect(
    () => () => {
      const waiting = pendingText.current;
      if (!waiting) return;
      window.clearTimeout(waiting.timer);
      pendingText.current = null;
      void controller
        .run('sentenceText', { sentenceId: id, sentence: { text: waiting.value } }, { quiet: true })
        .catch(() => {});
    },
    [id],
  );
  const leave = () => void flush().then(back);
  if (!sentence)
    return (
      <div>
        <button type="button" onClick={back}>
          Back
        </button>
        <p className="muted" role="status">
          {loadFailed ? 'The sentence could not be loaded. Trying again…' : 'Loading the sentence…'}
        </p>
      </div>
    );
  const editable = canEdit(sentence.canEdit);
  // Someone else's kept sentence plays as kept; the builder's own work is rendered from its words.
  const latestKept = !editable && sentence.kept ? sentence.versions[0]?.n : undefined;
  const rendersWords = latestKept === undefined;
  const canPlayHere = canPlay && (canPlayParts || !rendersWords);
  const selected = sentence.pieces.filter((p) => selection.includes(p.id));
  const one = selected.length === 1 ? selected[0] : null;
  const playIntoGame = async (version?: number) => {
    if (busy) return;
    setBusy(true);
    try {
      await flush();
      await controller.run('sentencePlay', {
        sentenceId: id,
        ...(version !== undefined && { sentence: { version } }),
      });
    } catch {
      /* shown by the controller */
    } finally {
      setBusy(false);
    }
  };
  const previewSentence = (fields: SentenceInput = {}) => {
    const play = () => void preview.play('sentencePreview', { sentenceId: id, sentence: fields });
    if (textPending()) void flush().then(play);
    else play();
  };
  // Removing a pause takes away the focused button: focus goes to a neighbour (2.42 review).
  const remove = (ids: string[]) => {
    const order = sentence.pieces.map((p) => p.id);
    const gone = new Set(
      ids.filter((x) => sentence.pieces.find((p) => p.id === x)?.kind === 'pause'),
    );
    if (gone.size) {
      const first = Math.min(...[...gone].map((x) => order.indexOf(x)));
      refocus.current = [...order.slice(first + 1), ...order.slice(0, first).reverse()].filter(
        (x) => !gone.has(x),
      );
    }
    return apply({ do: 'delete', pieces: ids });
  };
  return (
    <section
      className="sentence-builder"
      aria-label={`Sentence ${sentence.title || sentence.text}`}
    >
      <div className="voices-inline">
        <button type="button" onClick={leave}>
          Back to sentences
        </button>
        {sentence.kept && <span className="badge">Kept · version {sentence.versions[0]?.n}</span>}
        {!sentence.mine && <span className="muted">by {sentence.ownerName}</span>}
      </div>
      {loadFailed && (
        <p className="warning" role="status">
          The sentence could not be refreshed. Trying again…
        </p>
      )}
      <label className="sentence-text">
        <span>Sentence text</span>
        <textarea
          rows={2}
          value={draft}
          maxLength={4000}
          readOnly={!editable}
          placeholder="Type what to say. [laugh] adds a named piece."
          onChange={(e) => type(e.target.value)}
        />
      </label>
      <p className="sentence-words" aria-label="Matched words">
        {wordMarks(sentence).map(({ word, index, piece }) => {
          const marker = piece?.kind === 'missing' ? missingMarker(piece, reduced) : null;
          const voice = voiceById(voices, piece?.voiceId);
          return (
            <span
              key={index}
              className={`sentence-word${marker ? ' missing ' + marker.className : ''}${piece && selection.includes(piece.id) ? ' selected' : ''}`}
              style={voice ? ({ '--voice': voice.color } as CSSProperties) : undefined}
              title={marker?.label ?? (voice ? voice.name : undefined)}
            >
              {word}
              {marker && <span className="sr-only"> ({marker.label})</span>}
              {marker && reduced && (
                <span className="missing-badge" aria-hidden="true">
                  !
                </span>
              )}
            </span>
          );
        })}
      </p>
      {sentence.missing > 0 && (
        <p className="sentence-note" role="status">
          <AlertTriangle size={14} aria-hidden="true" /> {sentence.missing} word
          {sentence.missing === 1 ? ' is' : 's are'} not in the voice library. Each plays as a gap
          or a bleep; nothing is filled in for you.
          {editable && (
            <button type="button" onClick={() => void apply({ do: 'rematch' })}>
              Find missing again
            </button>
          )}
        </p>
      )}
      {editable && sentence.suggestContrast && (
        <p className="sentence-note">
          This line may sound more unsettling with contrasting deliveries.
          <button type="button" onClick={() => void apply({ do: 'contrast' })}>
            Try contrasting deliveries
          </button>
        </p>
      )}
      <div className="voices-inline sentence-actions">
        <button type="button" onClick={() => previewSentence()}>
          <Headphones size={14} /> {preview.previewing ? 'Previewing…' : 'Preview here'}
        </button>
        {preview.previewing && (
          <button type="button" onClick={preview.stop}>
            <Square size={13} /> Stop preview
          </button>
        )}
        <button
          type="button"
          className="primary"
          aria-disabled={!canPlayHere || busy || sentence.pieces.length === 0 || undefined}
          title={
            !canPlay
              ? 'Playback is not available'
              : canPlayHere
                ? undefined
                : 'The host has turned guest seeking off: only kept sentences play into the game'
          }
          onClick={() => canPlayHere && sentence.pieces.length > 0 && void playIntoGame(latestKept)}
        >
          <Play size={14} /> Play into game
        </button>
        {editable && canKeep && (
          <button
            type="button"
            aria-disabled={busy || sentence.pieces.length === 0 || undefined}
            onClick={() => {
              if (busy || sentence.pieces.length === 0) return;
              setBusy(true);
              void flush()
                .then(() => controller.run('sentenceKeep', { sentenceId: id }))
                .then((r) => {
                  const version = (r as { version?: number } | undefined)?.version;
                  controller.notify(
                    `Kept as version ${version ?? ''}.`.replace(' .', '.'),
                    'success',
                  );
                  return Promise.all([load(), refreshSentences()]);
                })
                .catch(() => {})
                .finally(() => setBusy(false));
            }}
          >
            <Save size={14} /> {sentence.kept ? 'Keep new version' : 'Keep'}
          </button>
        )}
        {editable && sentence.kept && (
          <button
            type="button"
            aria-pressed={sentence.pinned}
            onClick={() => void apply({ do: 'pin', pinned: !sentence.pinned })}
          >
            <Pin size={14} /> {sentence.pinned ? 'Unpin from board' : 'Pin to board'}
          </button>
        )}
        {editable && (
          <button
            type="button"
            onClick={() => void apply({ do: 'shuffle', pieces: [...selection] })}
          >
            <Shuffle size={14} /> Shuffle voices{selection.length ? ' (selected)' : ''}
          </button>
        )}
        {editable && !sentence.kept && sentence.mine && (
          <button
            type="button"
            onClick={() => {
              // Text not sent yet would only be refused once the sentence is gone.
              if (pendingText.current) window.clearTimeout(pendingText.current.timer);
              pendingText.current = null;
              void controller
                .run('sentenceDiscard', { sentenceId: id })
                .then(() => refreshSentences())
                .then(back)
                .catch(() => {});
            }}
          >
            <Trash2 size={14} /> Discard
          </button>
        )}
      </div>
      {editable && (
        <MixSettings sentence={sentence} voices={voices} apply={apply} preview={previewSentence} />
      )}
      <Timeline
        sentence={sentence}
        voices={voices}
        selection={selection}
        setSelection={setSelection}
        editable={editable}
        reduced={reduced}
        apply={apply}
        remove={remove}
        refocus={refocus}
        previewPiece={(p) => previewSentence({ pieces: [p.id] })}
      />
      {one && (
        <PieceInspector
          key={one.id}
          sentence={sentence}
          piece={one}
          voices={voices}
          editable={editable}
          apply={apply}
          remove={remove}
          previewAlternative={(i) => previewSentence({ pieces: [one.id], alternative: i })}
        />
      )}
      {editable && (
        <EffectsBox
          count={selected.length}
          apply={(effect) => void apply({ do: 'effects', pieces: [...selection], effect })}
        />
      )}
      {sentence.versions.length > 0 && (
        <Versions
          sentence={sentence}
          editable={editable}
          canPlay={canPlay}
          play={(n) => void playIntoGame(n)}
          preview={(n) => previewSentence({ version: n })}
          restore={(n) => void apply({ do: 'restore', version: n })}
        />
      )}
    </section>
  );
}

function MixSettings({
  sentence,
  voices,
  apply,
  preview,
}: {
  sentence: Sentence;
  voices: Voice[];
  apply: (edit: SentenceInput, quiet?: boolean) => Promise<unknown>;
  preview: (fields?: SentenceInput) => void;
}) {
  const [chaos, setChaos] = useState(sentence.settings.chaos);
  useEffect(() => setChaos(sentence.settings.chaos), [sentence.settings.chaos]);
  const s = sentence.settings;
  const [voiceIds, changeVoices] = useListSetting(s.voiceIds, (next) =>
    apply({ do: 'settings', voiceIds: next }),
  );
  const other = s.timing === 'fluent' ? 'original' : 'fluent';
  return (
    <details className="sentence-mix">
      <summary>Mix: voices, Smooth–Chaotic, timing and volume</summary>
      <div className="voices-inline">
        <label className="sentence-slider">
          Smooth
          <input
            type="range"
            min={0}
            max={100}
            value={chaos}
            aria-label="Smooth to Chaotic"
            onChange={(e) => setChaos(Number(e.target.value))}
            onPointerUp={() => chaos !== s.chaos && void apply({ do: 'settings', chaos })}
            onKeyUp={() => chaos !== s.chaos && void apply({ do: 'settings', chaos })}
          />
          Chaotic
        </label>
      </div>
      <fieldset className="sentence-voices">
        <legend>Voices in the mix {voiceIds.length === 0 && '(all)'}</legend>
        {voices.map((v) => (
          <label key={v.id} className="voices-check">
            <input
              type="checkbox"
              checked={voiceIds.includes(v.id)}
              onChange={(e) => {
                const on = e.target.checked;
                changeVoices((list) =>
                  on ? [...list.filter((x) => x !== v.id), v.id] : list.filter((x) => x !== v.id),
                );
              }}
            />
            <VoiceAvatar voice={v} size={18} /> {v.name}
          </label>
        ))}
        {voices.length === 0 && <span className="muted">Name voices in Voices to mix them.</span>}
      </fieldset>
      <div className="voices-inline">
        <label className="voices-check">
          <input
            type="checkbox"
            checked={s.altered}
            onChange={(e) => void apply({ do: 'settings', altered: e.target.checked })}
          />
          Use altered takes (reversed, sped-up, effects)
        </label>
        <label className="voices-check">
          <input
            type="checkbox"
            checked={s.balance}
            onChange={(e) => void apply({ do: 'settings', balance: e.target.checked })}
          />
          Balance volumes
        </label>
      </div>
      <div className="voices-inline" role="radiogroup" aria-label="Timing">
        <label className="voices-check">
          <input
            type="radio"
            name="sentence-timing"
            checked={s.timing === 'fluent'}
            onChange={() => void apply({ do: 'settings', timing: 'fluent' })}
          />
          Fluent joins
        </label>
        <label className="voices-check">
          <input
            type="radio"
            name="sentence-timing"
            checked={s.timing === 'original'}
            onChange={() => void apply({ do: 'settings', timing: 'original' })}
          />
          Original timing
        </label>
        <button type="button" onClick={() => preview({ timing: other })}>
          <Headphones size={13} /> Compare with{' '}
          {other === 'fluent' ? 'fluent joins' : 'original timing'}
        </button>
      </div>
      <div className="voices-inline">
        <label className="voices-check">
          <input
            type="checkbox"
            checked={s.cleanup}
            onChange={(e) => void apply({ do: 'settings', cleanup: e.target.checked })}
          />
          Gentle cleanup (edges and noise between words)
        </label>
        <button type="button" onClick={() => preview({ variant: 'original' })}>
          <Headphones size={13} /> Original
        </button>
        <button type="button" onClick={() => preview({ variant: 'cleaned' })}>
          <Headphones size={13} /> Cleaned
        </button>
      </div>
    </details>
  );
}

function Timeline({
  sentence,
  voices,
  selection,
  setSelection,
  editable,
  reduced,
  apply,
  remove,
  refocus,
  previewPiece,
}: {
  sentence: Sentence;
  voices: Voice[];
  selection: Selection;
  setSelection: (s: Selection) => void;
  editable: boolean;
  reduced: boolean;
  apply: (edit: SentenceInput, quiet?: boolean) => Promise<unknown>;
  remove: (ids: string[]) => Promise<unknown>;
  refocus: MutableRefObject<string[] | null>;
  previewPiece: (p: Piece) => void;
}) {
  const [drag, setDrag] = useState<{
    id: string;
    startX: number;
    from: number;
    at: number;
    moved: boolean;
  } | null>(null);
  const refs = useRef(new Map<string, HTMLButtonElement>());
  const group = useRef<HTMLDivElement>(null);
  // Alt+arrow nudges (2.42 review): each builds on the last one, shown at once and sent as one move
  // once the keys rest, so quick presses all count and key repeat does not flood the host.
  const [nudged, setNudged] = useState<{ id: string; at: number } | null>(null);
  const nudge = useRef<{ id: string; at: number; timer: number } | null>(null);
  const overlaps = overlapping(sentence);
  const pieces = sentence.pieces;
  // A removed pause took the focus with it: a neighbour (or the timeline) gets it back.
  useEffect(() => {
    const want = refocus.current;
    if (!want) return;
    const active = document.activeElement;
    if (active && active !== document.body && active.isConnected) return;
    refocus.current = null;
    const next = want.find((x) => refs.current.has(x));
    (next ? refs.current.get(next) : group.current)?.focus();
  }, [pieces]);
  const sendNudge = () => {
    const n = nudge.current;
    if (!n) return;
    window.clearTimeout(n.timer);
    nudge.current = null;
    void apply({ do: 'move', pieces: [n.id], offsetMs: n.at }, true).finally(() =>
      setNudged((shown) => (shown && shown.id === n.id && shown.at === n.at ? null : shown)),
    );
  };
  useEffect(() => () => sendNudge(), []);
  const width = pxOf(Math.max(sentence.durationMs, 2000) + 1000);
  const others = (except: string) =>
    pieces
      .filter((p) => p.id !== except)
      .map((p) => ({ start: p.offsetMs, end: p.offsetMs + p.durationMs }));
  const down = (e: ReactPointerEvent<HTMLButtonElement>, p: Piece) => {
    if (e.button !== 0) return;
    e.currentTarget.setPointerCapture?.(e.pointerId);
    setDrag({ id: p.id, startX: e.clientX, from: p.offsetMs, at: p.offsetMs, moved: false });
  };
  const move = (e: ReactPointerEvent<HTMLButtonElement>) => {
    if (!drag || !editable) return;
    const dx = e.clientX - drag.startX;
    if (!drag.moved && Math.abs(dx) < 3) return;
    // Free movement; holding Shift snaps to the grid or a neighbour's edge.
    setDrag({
      ...drag,
      moved: true,
      at: placeAt(drag.from + msOfPx(dx), e.shiftKey, others(drag.id)),
    });
  };
  const up = (e: ReactPointerEvent<HTMLButtonElement>, p: Piece) => {
    const current = drag;
    setDrag(null);
    if (!current) return;
    // Releasing never plays: a drag moves the piece, a click selects it.
    if (current.moved && editable)
      void apply({ do: 'move', pieces: [p.id], offsetMs: current.at }, true);
    else setSelection(select(selection, p.id, e.ctrlKey || e.metaKey));
  };
  const key = (e: ReactKeyboardEvent<HTMLButtonElement>, p: Piece, index: number) => {
    const action = timelineKey(e);
    if (!action) return;
    // Escape with nothing to clear closes the window, as everywhere else (2.42 review).
    if (action.kind === 'clear' && selection.length === 0) return;
    e.preventDefault();
    switch (action.kind) {
      case 'focus': {
        const next = pieces[index + action.step];
        if (next) {
          refs.current.get(next.id)?.focus();
          setSelection([next.id]);
        }
        break;
      }
      case 'move': {
        if (!editable) break;
        if (nudge.current && nudge.current.id !== p.id) sendNudge();
        const base = nudge.current?.at ?? (nudged?.id === p.id ? nudged.at : p.offsetMs);
        const at = Math.max(0, base + action.deltaMs);
        if (nudge.current) window.clearTimeout(nudge.current.timer);
        nudge.current = { id: p.id, at, timer: window.setTimeout(sendNudge, 200) };
        setNudged({ id: p.id, at });
        break;
      }
      case 'delete':
        if (editable) void remove(selection.includes(p.id) ? [...selection] : [p.id]);
        break;
      case 'preview':
        previewPiece(p);
        break;
      case 'select':
        setSelection(select(selection, p.id, action.add));
        break;
      case 'clear':
        setSelection([]);
        break;
    }
  };
  return (
    <div className="sentence-timeline-wrap">
      <div className="voices-inline">
        <strong>Timeline</strong>
        <span className="muted">
          {formatMs(sentence.durationMs)} · drag freely, Shift snaps · Space selects · Alt+←/→
          nudges · Delete leaves a pause
        </span>
        {editable && (
          <button
            type="button"
            onClick={() =>
              void apply({ do: 'insertPause', offsetMs: sentence.durationMs, durationMs: 300 })
            }
          >
            <Plus size={13} /> Pause
          </button>
        )}
      </div>
      <div
        className="sentence-timeline"
        role="group"
        aria-label="Timeline"
        ref={group}
        tabIndex={-1}
      >
        <div className="sentence-track" style={{ width }}>
          {Array.from({ length: Math.ceil(width / pxOf(1000)) }, (_, i) => (
            <span
              key={i}
              className="sentence-tick"
              style={{ left: pxOf(i * 1000) }}
              aria-hidden="true"
            >
              {i} s
            </span>
          ))}
          {pieces.map((p, index) => {
            const voice = voiceById(voices, p.voiceId);
            const marker = p.kind === 'missing' ? missingMarker(p, reduced) : null;
            const offset =
              drag?.id === p.id ? drag.at : nudged?.id === p.id ? nudged.at : p.offsetMs;
            const problem = sourceProblem(p);
            return (
              <button
                key={p.id}
                ref={(el) => {
                  if (el) refs.current.set(p.id, el);
                  else refs.current.delete(p.id);
                }}
                type="button"
                className={[
                  'timeline-piece',
                  `kind-${p.kind}`,
                  selection.includes(p.id) ? 'selected' : '',
                  overlaps.has(p.id) ? 'overlap' : '',
                  marker?.className ?? '',
                  p.manual ? 'manual' : '',
                  drag?.id === p.id && drag.moved ? 'dragging' : '',
                  problem ? `source-${problem}` : '',
                ].join(' ')}
                style={{
                  left: pxOf(offset),
                  width: Math.max(14, pxOf(p.durationMs)),
                  ...(voice && ({ '--voice': voice.color } as CSSProperties)),
                }}
                aria-pressed={selection.includes(p.id)}
                aria-label={`${marker?.label ?? pieceLabel(p)}${voice ? `, ${voice.name}` : ''}, at ${formatMs(offset)}${overlaps.has(p.id) ? ', overlaps another piece' : ''}${problem ? `, source ${problem}` : ''}`}
                onPointerDown={(e) => down(e, p)}
                onPointerMove={move}
                onPointerUp={(e) => up(e, p)}
                onKeyDown={(e) => key(e, p, index)}
              >
                <span className="timeline-text">
                  {p.kind === 'pause' ? '…' : (p.substitute ?? p.text)}
                </span>
                {marker && reduced && (
                  <span className="missing-badge" aria-hidden="true">
                    !
                  </span>
                )}
              </button>
            );
          })}
        </div>
      </div>
      {overlaps.size > 0 && (
        <p className="muted sentence-overlap-note">
          Overlapping pieces are striped: the later-starting piece cuts off the earlier one.
        </p>
      )}
    </div>
  );
}

function PieceInspector({
  sentence,
  piece,
  voices,
  editable,
  apply,
  remove,
  previewAlternative,
}: {
  sentence: Sentence;
  piece: Piece;
  voices: Voice[];
  editable: boolean;
  apply: (edit: SentenceInput, quiet?: boolean) => Promise<unknown>;
  remove: (ids: string[]) => Promise<unknown>;
  previewAlternative: (index: number) => void;
}) {
  const [alternatives, setAlternatives] = useState<Alternatives | null>(null);
  const [length, setLength] = useState(Math.round(piece.durationMs));
  const [edges, setEdges] = useState({
    start: piece.source?.startMs ?? 0,
    end: piece.source?.endMs ?? 0,
  });
  useEffect(() => {
    let live = true;
    void controller
      .run(
        'sentenceAlternatives',
        { sentenceId: sentence.id, sentence: { pieces: [piece.id] } },
        { quiet: true },
      )
      .then((r) => live && setAlternatives(alternativesSchema.parse(r)))
      .catch(() => {});
    return () => {
      live = false;
    };
  }, [sentence.id, sentence.revision, piece.id]);
  const voice = voiceById(voices, piece.voiceId);
  const ids = [piece.id];
  const problem = sourceProblem(piece);
  // The volume slider from the pointer or the keyboard (2.42 review): sent when it changed.
  const setGain = (gain: number) => {
    if (Number.isFinite(gain) && gain !== piece.gain)
      void apply({ do: 'gain', pieces: ids, gain }, true);
  };
  return (
    <section className="sentence-inspector" aria-label={`Piece ${pieceLabel(piece)}`}>
      <div className="voices-inline">
        {voice && <VoiceAvatar voice={voice} size={22} />}
        <strong>{pieceLabel(piece)}</strong>
        {piece.chosen && <span className="badge">Your choice</span>}
        {piece.kind === 'splice' && <span className="badge warning-badge">Splice (approved)</span>}
        {piece.substitute && <span className="badge warning-badge">Similar word</span>}
      </div>
      {problem && (
        <p className="warning" role="status">
          {problem === 'missing'
            ? 'Its recording is no longer in the voice library: this piece cannot play until you swap it or delete it.'
            : 'Its recording changed since this sentence was kept. Kept versions still play as they were.'}
        </p>
      )}
      {piece.kind === 'splice' && (
        <ul className="muted">
          {piece.parts?.map((part, i) => (
            <li key={i}>{part.label}</li>
          ))}
        </ul>
      )}
      {(piece.kind === 'word' || piece.kind === 'named') && alternatives && (
        <div>
          <div className="voices-inline">
            <span>
              Take {alternatives.current + 1} of {alternatives.takes.length}
            </span>
            {editable && alternatives.takes.length > 1 && (
              <button type="button" onClick={() => void apply({ do: 'swap', pieces: ids })}>
                <Shuffle size={13} /> Swap take
              </button>
            )}
          </div>
          <ul className="sentence-takes" aria-label="Takes">
            {alternatives.takes.map((t) => {
              const v = voiceById(voices, t.voiceId);
              return (
                <li key={t.index} className={t.current ? 'current' : undefined}>
                  <VoiceAvatar voice={v} size={18} />
                  <span>
                    {t.title} {t.altered && <span className="badge">altered</span>}
                  </span>
                  <button
                    type="button"
                    aria-label={`Preview take ${t.index + 1}`}
                    onClick={() => previewAlternative(t.index)}
                  >
                    <Headphones size={13} />
                  </button>
                  {editable && !t.current && (
                    <button
                      type="button"
                      onClick={() => void apply({ do: 'swap', pieces: ids, alternative: t.index })}
                    >
                      Use take {t.index + 1}
                    </button>
                  )}
                </li>
              );
            })}
          </ul>
        </div>
      )}
      {(piece.kind === 'missing' || piece.substitute || piece.kind === 'splice') && editable && (
        <div>
          <div className="voices-inline" role="radiogroup" aria-label="Missing word plays as">
            <label className="voices-check">
              <input
                type="radio"
                name={`missing-${piece.id}`}
                checked={piece.kind === 'missing' && piece.missing !== 'bleep'}
                onChange={() => void apply({ do: 'missing', pieces: ids, mode: 'gap' })}
              />
              Gap
            </label>
            <label className="voices-check">
              <input
                type="radio"
                name={`missing-${piece.id}`}
                checked={piece.kind === 'missing' && piece.missing === 'bleep'}
                onChange={() => void apply({ do: 'missing', pieces: ids, mode: 'bleep' })}
              />
              Bleep
            </label>
          </div>
          {alternatives && alternatives.similar.length > 0 && (
            <div className="voices-inline" aria-label="Similar recorded words">
              <span className="muted">Similar recorded words (your choice, shown as such):</span>
              {alternatives.similar.map((s) => (
                <button
                  key={s.word}
                  type="button"
                  onClick={() => void apply({ do: 'resolve', pieces: ids, word: s.word })}
                >
                  Use “{s.word}”
                </button>
              ))}
            </div>
          )}
          {alternatives && alternatives.splices.length > 0 && (
            <div aria-label="Candidate splices">
              <span className="muted">
                Candidate splices from actual recordings (only with your approval):
              </span>
              <ul className="sentence-takes">
                {alternatives.splices.map((s) => (
                  <li key={s.index}>
                    <span>{s.description}</span>
                    <button
                      type="button"
                      onClick={() => void apply({ do: 'resolve', pieces: ids, splice: s.index })}
                    >
                      Approve splice
                    </button>
                  </li>
                ))}
              </ul>
            </div>
          )}
        </div>
      )}
      {editable && (piece.kind === 'pause' || piece.kind === 'missing') && (
        <form
          className="voices-inline"
          onSubmit={(e) => {
            e.preventDefault();
            void apply({
              do: 'pause',
              pieces: ids,
              durationMs: Math.min(60000, Math.max(10, length)),
            });
          }}
        >
          <label>
            Length (ms){' '}
            <input
              type="number"
              min={10}
              max={60000}
              value={length}
              onChange={(e) => setLength(Number(e.target.value))}
            />
          </label>
          <button type="submit">Set length</button>
        </form>
      )}
      {editable && piece.source && (piece.kind === 'word' || piece.kind === 'named') && (
        <form
          className="voices-inline"
          onSubmit={(e) => {
            e.preventDefault();
            void apply({
              do: 'edges',
              pieces: ids,
              startMs: Math.round(edges.start),
              endMs: Math.round(edges.end),
            });
          }}
        >
          <label>
            Start (ms){' '}
            <input
              type="number"
              min={0}
              value={edges.start}
              onChange={(e) => setEdges({ ...edges, start: Number(e.target.value) })}
            />
          </label>
          <label>
            End (ms){' '}
            <input
              type="number"
              min={0}
              value={edges.end}
              onChange={(e) => setEdges({ ...edges, end: Number(e.target.value) })}
            />
          </label>
          <button type="submit">Set edges</button>
        </form>
      )}
      {editable && piece.kind !== 'pause' && piece.kind !== 'missing' && (
        <div className="voices-inline">
          <label>
            Fade in (ms){' '}
            <input
              type="number"
              min={0}
              max={2000}
              defaultValue={piece.fadeInMs}
              onBlur={(e) =>
                void apply(
                  {
                    do: 'fade',
                    pieces: ids,
                    fadeInMs: Math.min(2000, Math.max(0, Number(e.target.value))),
                  },
                  true,
                )
              }
            />
          </label>
          <label>
            Fade out (ms){' '}
            <input
              type="number"
              min={0}
              max={2000}
              defaultValue={piece.fadeOutMs}
              onBlur={(e) =>
                void apply(
                  {
                    do: 'fade',
                    pieces: ids,
                    fadeOutMs: Math.min(2000, Math.max(0, Number(e.target.value))),
                  },
                  true,
                )
              }
            />
          </label>
          <label className="voices-check">
            <input
              type="checkbox"
              checked={piece.gain == null}
              onChange={(e) =>
                void apply(
                  e.target.checked
                    ? { do: 'gain', pieces: ids, balanced: true }
                    : { do: 'gain', pieces: ids, gain: 1 },
                )
              }
            />
            Balanced volume
          </label>
          {piece.gain != null && (
            <label>
              Volume{' '}
              <input
                type="range"
                min={0}
                max={2}
                step={0.05}
                defaultValue={piece.gain}
                aria-label="Piece volume"
                onPointerUp={(e) => setGain(Number(e.currentTarget.value))}
                onKeyUp={(e) => setGain(Number(e.currentTarget.value))}
              />
            </label>
          )}
        </div>
      )}
      {editable && (
        <button type="button" onClick={() => void remove(ids)}>
          <Trash2 size={13} /> {piece.kind === 'pause' ? 'Remove pause' : 'Delete (leaves a pause)'}
        </button>
      )}
    </section>
  );
}

function EffectsBox({
  count,
  apply,
}: {
  count: number;
  apply: (effect: NonNullable<SentenceInput['effect']>) => void;
}) {
  const [effect, setEffect] = useState({ reverse: false, speed: 1, keepPitch: false, echo: false });
  return (
    <details className="sentence-effects">
      <summary>
        Effects {count ? `(${count} selected piece${count === 1 ? '' : 's'})` : '(whole sentence)'}
      </summary>
      <div className="voices-inline">
        <label className="voices-check">
          <input
            type="checkbox"
            checked={effect.reverse}
            onChange={(e) => setEffect({ ...effect, reverse: e.target.checked })}
          />
          Reverse
        </label>
        <label>
          Speed{' '}
          <input
            type="number"
            min={0.25}
            max={4}
            step={0.05}
            value={effect.speed}
            onChange={(e) => setEffect({ ...effect, speed: Number(e.target.value) || 1 })}
          />
        </label>
        <label className="voices-check">
          <input
            type="checkbox"
            checked={effect.keepPitch}
            onChange={(e) => setEffect({ ...effect, keepPitch: e.target.checked })}
          />
          Keep pitch
        </label>
        <label className="voices-check">
          <input
            type="checkbox"
            checked={effect.echo}
            onChange={(e) => setEffect({ ...effect, echo: e.target.checked })}
          />
          Echo
        </label>
        <button
          type="button"
          onClick={() => apply({ ...effect, speed: Math.min(4, Math.max(0.25, effect.speed)) })}
        >
          Apply effects
        </button>
      </div>
    </details>
  );
}

function Versions({
  sentence,
  editable,
  canPlay,
  play,
  preview,
  restore,
}: {
  sentence: Sentence;
  editable: boolean;
  canPlay: boolean;
  play: (n: number) => void;
  preview: (n: number) => void;
  restore: (n: number) => void;
}) {
  return (
    <section aria-label="Versions">
      <h3>Versions</h3>
      <ul className="sentence-takes">
        {sentence.versions.map((v) => (
          <li key={v.n}>
            <span>
              Version {v.n} · {formatMs(v.durationMs)} · {v.byName}
            </span>
            <button
              type="button"
              aria-label={`Preview version ${v.n}`}
              onClick={() => preview(v.n)}
            >
              <Headphones size={13} />
            </button>
            <button
              type="button"
              aria-label={`Play version ${v.n} into game`}
              aria-disabled={!canPlay || undefined}
              onClick={() => canPlay && play(v.n)}
            >
              <Play size={13} />
            </button>
            {editable && (
              <button type="button" onClick={() => restore(v.n)}>
                <RotateCcw size={13} /> Restore
              </button>
            )}
            {v.affected.length > 0 && (
              <span className="warning">
                {v.affected
                  .map(
                    (a) =>
                      `“${a.text}” ${a.state === 'missing' ? 'lost its recording' : 'has a changed recording'}`,
                  )
                  .join('; ')}{' '}
                (this version still plays as kept)
              </span>
            )}
          </li>
        ))}
      </ul>
    </section>
  );
}

// ---- Palette -------------------------------------------------------------------------------------

type TapMode = 'preview' | 'game';
function usePaletteMode() {
  const [mode, setMode] = useState<TapMode>(() => {
    try {
      return localStorage.getItem('soundpad.palette.mode') === 'game' ? 'game' : 'preview';
    } catch {
      return 'preview';
    }
  });
  return [
    mode,
    (next: TapMode) => {
      setMode(next);
      try {
        localStorage.setItem('soundpad.palette.mode', next);
      } catch {
        /* a convenience only */
      }
    },
  ] as const;
}

function PalettePanel({
  canPlay,
  canGame,
  canBuild,
  openTake,
}: {
  canPlay: boolean;
  /** A word into the game: playback, and (guests) seeking, as for a region (2.42 review). */
  canGame: boolean;
  canBuild: boolean;
  openTake: (id: string) => void;
}) {
  const voices = useVoices();
  const preview = usePreviewAudio();
  const [mode, setMode] = usePaletteMode();
  const [query, setQuery] = useState('');
  const [items, setItems] = useState<PaletteItem[]>([]);
  const [sets, setSets] = useState<{ active: string; sets: PerformanceSet[] } | null>(null);
  const [recording, setRecording] = useState<{ taps: number } | null>(null);
  const [take, setTake] = useState<string | null>(null);
  const [naming, setNaming] = useState<PaletteItem | null>(null);
  const [name, setName] = useState('');
  const searchId = useRef(0);
  const search = (value: string) => {
    const mine = ++searchId.current;
    void controller
      .run('paletteSearch', { query: value.slice(0, 200) }, { quiet: true })
      .then((r) => mine === searchId.current && setItems(paletteSchema.parse(r).items))
      .catch(() => {});
  };
  const loadSets = () =>
    void controller
      .run('performanceSets', {}, { quiet: true })
      .then((r) => setSets(setsSchema.parse(r)))
      .catch(() => {});
  useEffect(() => {
    search('');
    loadSets();
    void controller
      .run('performance', { action: 'status' }, { quiet: true })
      .then((r) => {
        const p = performanceSchema.parse(r);
        setRecording(p.recording ? { taps: p.taps } : null);
      })
      .catch(() => {});
  }, []);
  const active = sets?.sets.find((s) => s.id === sets.active) ?? null;
  const [favorites, changeFavorites] = useListSetting(active?.favorites ?? [], (next) =>
    active
      ? controller
          .run('performanceSets', { action: 'update', set: { id: active.id, favorites: next } })
          .then((r) => {
            setSets(setsSchema.parse(r));
            search(query);
          })
      : Promise.resolve(),
  );
  const starred = (item: PaletteItem) =>
    active ? favorites.some((f) => sameRef(f, itemRef(item))) : item.favorite;
  const tap = (item: PaletteItem) => {
    const ref = itemRef(item);
    if (recording) setRecording({ taps: recording.taps + 1 });
    if (mode === 'game' && canGame)
      void controller
        .run('paletteTap', { action: 'game', sentence: { item: ref } })
        .catch(() => {});
    else void preview.play('paletteTap', { action: 'preview', sentence: { item: ref } });
  };
  const toggleFavorite = (item: PaletteItem) => {
    if (!active) return;
    const ref = itemRef(item);
    changeFavorites((list) =>
      list.some((f) => sameRef(f, ref)) ? list.filter((f) => !sameRef(f, ref)) : [...list, ref],
    );
  };
  return (
    <div className="sentence-palette">
      <div className="voices-search">
        <input
          aria-label="Search the palette"
          placeholder="Search words, phrases and [named] pieces"
          value={query}
          onChange={(e) => {
            setQuery(e.target.value);
            search(e.target.value);
          }}
        />
      </div>
      <div className="voices-mode" role="radiogroup" aria-label="Palette taps">
        <span>Taps:</span>
        <label>
          <input
            type="radio"
            name="palette-mode"
            checked={mode === 'preview' || !canGame}
            onChange={() => setMode('preview')}
          />
          <Headphones size={13} aria-hidden="true" /> Preview here
        </label>
        <label
          title={
            canGame
              ? undefined
              : canPlay
                ? 'The host has turned guest seeking off: palette words play only here'
                : 'Playback is not available'
          }
        >
          <input
            type="radio"
            name="palette-mode"
            checked={mode === 'game' && canGame}
            disabled={!canGame}
            onChange={() => setMode('game')}
          />
          <Play size={13} aria-hidden="true" /> Play into game
        </label>
        {active && <span className="muted">Set: {active.name}</span>}
      </div>
      {canBuild && (
        <div className="voices-inline sentence-record" role="status">
          {recording ? (
            <>
              <span className="recording-dot" aria-hidden="true" />
              <span>
                Recording your performance: {recording.taps} tap{recording.taps === 1 ? '' : 's'}
              </span>
              <button
                type="button"
                className="primary"
                onClick={() =>
                  void controller
                    .run('performance', { action: 'stop' })
                    .then((r) => {
                      const t = takeSchema.parse(r);
                      setRecording(null);
                      setTake(t.sentenceId);
                      return refreshSentences();
                    })
                    .catch(() => setRecording(null))
                }
              >
                <Square size={13} /> Stop recording
              </button>
              <button
                type="button"
                onClick={() =>
                  void controller
                    .run('performance', { action: 'cancel' })
                    .then(() => setRecording(null))
                    .catch(() => {})
                }
              >
                Cancel
              </button>
            </>
          ) : (
            <>
              <button
                type="button"
                onClick={() =>
                  void controller
                    .run('performance', { action: 'start' })
                    .then(() => {
                      setTake(null);
                      setRecording({ taps: 0 });
                    })
                    .catch(() => {})
                }
              >
                <Circle size={13} /> Record performance
              </button>
              <span className="muted">
                Records only your own taps and their timing, as an editable take.
              </span>
              {take && (
                <button type="button" className="primary" onClick={() => openTake(take)}>
                  Open the take as a sentence
                </button>
              )}
            </>
          )}
        </div>
      )}
      <ul className="palette-items" aria-label="Palette">
        {items.map((item, i) => {
          const voice = voiceById(voices, item.voiceId);
          return (
            <li
              key={`${item.namedId ?? item.recordingId}-${item.startMs}-${i}`}
              className={`palette-item kind-${item.kind}`}
              style={voice ? ({ '--voice': voice.color } as CSSProperties) : undefined}
            >
              <button
                type="button"
                className="palette-tap"
                aria-label={`Play “${item.text}”${voice ? `, ${voice.name}` : ''}`}
                onClick={() => tap(item)}
              >
                <VoiceAvatar voice={voice} size={20} />
                <span>{item.text}</span>
                <span className="muted">{formatMs(item.durationMs)}</span>
              </button>
              <button
                type="button"
                className="icon-button"
                aria-pressed={starred(item)}
                aria-label={`${starred(item) ? 'Remove' : 'Add'} favourite “${item.text}”`}
                onClick={() => toggleFavorite(item)}
              >
                <Star size={13} />
              </button>
              {canBuild && item.kind !== 'named' && (
                <button
                  type="button"
                  aria-label={`Name “${item.text}” as a nonverbal piece`}
                  onClick={() => setNaming(item)}
                >
                  Name…
                </button>
              )}
            </li>
          );
        })}
        {items.length === 0 && (
          <li className="muted">
            {query
              ? 'Nothing recorded matches that.'
              : 'Search for words, or name a laugh, gasp or breath in a recording to find it here.'}
          </li>
        )}
      </ul>
      {naming && naming.recordingId && (
        <form
          className="voices-inline"
          aria-label="Name a nonverbal piece"
          onSubmit={(e) => {
            e.preventDefault();
            void controller
              .run('paletteMark', {
                recordingId: naming.recordingId!,
                name: name.slice(0, 40),
                sentence: { startMs: Math.round(naming.startMs), endMs: Math.round(naming.endMs) },
              })
              .then(() => {
                setNaming(null);
                setName('');
                controller.notify(`Named [${name.trim().toLowerCase()}].`, 'success');
                search(query);
                return refreshSentences();
              })
              .catch(() => {});
          }}
        >
          <span>Name “{naming.text}” as</span>
          <input
            aria-label="Piece name"
            placeholder="laugh, gasp, breath, scream…"
            value={name}
            maxLength={30}
            onChange={(e) => setName(e.target.value)}
          />
          <button type="submit" className="primary">
            Name piece
          </button>
          <button type="button" onClick={() => setNaming(null)}>
            Cancel
          </button>
        </form>
      )}
    </div>
  );
}

// ---- Performance sets ----------------------------------------------------------------------------

function SetsPanel({
  canBuild,
  openSentence,
}: {
  canBuild: boolean;
  openSentence: (id: string) => void;
}) {
  const voices = useVoices();
  const [sets, setSets] = useState<{ active: string; sets: PerformanceSet[] } | null>(null);
  const [newName, setNewName] = useState('');
  const [template, setTemplate] = useState({ name: '', text: '' });
  const [slots, setSlots] = useState<Record<string, string>>({});
  const runSets = (fields: Partial<Command>) =>
    void controller
      .run('performanceSets', fields)
      .then((r) => setSets(setsSchema.parse(r)))
      .catch(() => {});
  useEffect(() => {
    void controller
      .run('performanceSets', {}, { quiet: true })
      .then((r) => setSets(setsSchema.parse(r)))
      .catch(() => {});
  }, []);
  const active = sets ? (sets.sets.find((s) => s.id === sets.active) ?? sets.sets[0]) : null;
  const update = (fields: Record<string, unknown>) =>
    active
      ? controller
          .run('performanceSets', { action: 'update', set: { id: active.id, ...fields } })
          .then((r) => setSets(setsSchema.parse(r)))
      : Promise.resolve();
  const [setVoices, changeSetVoices] = useListSetting(active?.voiceIds ?? [], (voiceIds) =>
    update({ voiceIds }),
  );
  const [templates, changeTemplates] = useListSetting(active?.templates ?? [], (next) =>
    update({ templates: next }),
  );
  if (!sets || !active) return <p className="muted">Loading your performance sets…</p>;
  return (
    <div className="sentence-sets">
      <p className="muted">
        A set holds voice filters, palette favourites and templates, as references to the library:
        no copies of any audio. Your active set is yours alone.
      </p>
      <label>
        Active set{' '}
        <select
          value={active.id}
          onChange={(e) => runSets({ action: 'activate', set: { id: e.target.value } })}
        >
          {sets.sets.map((s) => (
            <option key={s.id} value={s.id}>
              {s.name}
            </option>
          ))}
        </select>
      </label>
      <form
        className="voices-inline"
        onSubmit={(e) => {
          e.preventDefault();
          if (!newName.trim()) return;
          runSets({ action: 'create', set: { name: newName.trim().slice(0, 40) } });
          setNewName('');
        }}
      >
        <input
          aria-label="New set name"
          placeholder="New set"
          value={newName}
          maxLength={40}
          onChange={(e) => setNewName(e.target.value)}
        />
        <button type="submit">
          <Plus size={13} /> Add set
        </button>
        {active.id !== 'default' && (
          <button
            type="button"
            onClick={() => runSets({ action: 'delete', set: { id: active.id } })}
          >
            <Trash2 size={13} /> Remove “{active.name}”
          </button>
        )}
      </form>
      <fieldset className="sentence-voices">
        <legend>
          Voices in “{active.name}” {setVoices.length === 0 && '(all)'}
        </legend>
        {voices.map((v) => (
          <label key={v.id} className="voices-check">
            <input
              type="checkbox"
              checked={setVoices.includes(v.id)}
              onChange={(e) => {
                const on = e.target.checked;
                changeSetVoices((list) =>
                  on ? [...list.filter((x) => x !== v.id), v.id] : list.filter((x) => x !== v.id),
                );
              }}
            />
            <VoiceAvatar voice={v} size={18} /> {v.name}
          </label>
        ))}
      </fieldset>
      <p className="muted">
        {active.favorites.length} favourite{active.favorites.length === 1 ? '' : 's'} (star them in
        the palette).
      </p>
      <h3>Templates</h3>
      <ul className="sentence-takes" aria-label="Templates">
        {templates.map((t, index) => {
          const names = templateSlots(t.text);
          return (
            <li key={t.id || `new-${index}`}>
              <strong>{t.name}</strong>
              <span className="muted">{t.text}</span>
              {names.map((n) => (
                <input
                  key={n}
                  aria-label={`${t.name}: ${n}`}
                  placeholder={n}
                  value={slots[`${t.id}:${n}`] ?? ''}
                  onChange={(e) => setSlots({ ...slots, [`${t.id}:${n}`]: e.target.value })}
                />
              ))}
              {canBuild && (
                <button
                  type="button"
                  onClick={() =>
                    void create(
                      fillTemplate(
                        t.text,
                        Object.fromEntries(names.map((n) => [n, slots[`${t.id}:${n}`] ?? ''])),
                      ),
                      'template',
                    )
                      .then((s) => openSentence(s.id))
                      .catch(() => {})
                  }
                >
                  Use template
                </button>
              )}
              <button
                type="button"
                aria-label={`Remove template ${t.name}`}
                onClick={() => changeTemplates((list) => list.filter((x) => x !== t))}
              >
                <Trash2 size={13} />
              </button>
            </li>
          );
        })}
      </ul>
      <form
        className="voices-inline"
        onSubmit={(e) => {
          e.preventDefault();
          if (!template.name.trim() || !template.text.trim()) return;
          const added = {
            id: '',
            name: template.name.trim().slice(0, 40),
            text: template.text.trim().slice(0, 400),
          };
          changeTemplates((list) => [...list, added]);
          setTemplate({ name: '', text: '' });
        }}
      >
        <input
          aria-label="Template name"
          placeholder="Template name"
          value={template.name}
          maxLength={40}
          onChange={(e) => setTemplate({ ...template, name: e.target.value })}
        />
        <input
          aria-label="Template text"
          placeholder="Hello {name}, behind you"
          value={template.text}
          maxLength={400}
          onChange={(e) => setTemplate({ ...template, text: e.target.value })}
        />
        <button type="submit">
          <Plus size={13} /> Add template
        </button>
      </form>
    </div>
  );
}

/** Kept sentences pinned to the board: each plays its latest kept version into the game. */
export function PinnedSentences({ canPlay }: { canPlay: boolean }) {
  const { list } = useSentenceStore();
  const pinned = list?.sentences.filter((s) => s.pinned) ?? [];
  if (pinned.length === 0) return null;
  return (
    <section className="pinned-sentences" aria-label="Pinned sentences">
      {pinned.map((s) => (
        <button
          key={s.id}
          type="button"
          className="pinned-sentence"
          aria-label={`Play sentence: ${s.title || s.text}`}
          aria-disabled={!canPlay || undefined}
          title={s.text}
          // The latest kept version, as kept (an edit not kept yet is not what the tile plays).
          onClick={() =>
            canPlay &&
            void controller
              .run('sentencePlay', { sentenceId: s.id, sentence: { version: s.versions } })
              .catch(() => {})
          }
        >
          <Play size={14} aria-hidden="true" />
          <span>{s.title || s.text}</span>
          <span className="muted">{formatMs(s.durationMs)}</span>
        </button>
      ))}
    </section>
  );
}
