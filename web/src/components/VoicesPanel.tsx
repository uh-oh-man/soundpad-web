import { useEffect, useMemo, useRef, useState } from 'react';
import {
  ChevronLeft,
  ChevronRight,
  Headphones,
  Play,
  RotateCcw,
  Search,
  Star,
  Tag,
  Trash2,
  Undo2,
  X,
} from 'lucide-react';
import { z } from 'zod';
import { controller } from '../controller';
import type { Command, Snapshot } from '../protocol';
import {
  NOTHING_PICKED,
  NO_FILTERS,
  TYPE_LABELS,
  cleanTag,
  combineResults,
  countTypes,
  describeAudit,
  filterFields,
  formatDuration,
  getSchema,
  hasFilters,
  listSchema,
  pick,
  searchSchema,
  voiceOf,
  type ContextWord,
  type Filters,
  type LibraryStatus,
  type Picked,
  type Recording,
  type ResultType,
  type SearchResult,
} from '../library/voices';
import { refreshLibrary, takeOpen, useOpenRequests, useVoiceStore } from '../library/voice-store';
import { ReviewQueue, VoiceManager, WordIndexSettings, WordsAndWanted } from './VoicesTabs';
import {
  VoiceAvatar,
  WordModeSwitch,
  libraryChange,
  useWordMode,
  useWordPlayer,
} from './VoiceShared';
import { PAGE } from './VoiceShared';
import '../voices.css';

type Props = { state: Snapshot; host: boolean; connected: boolean; canPlay: boolean };
type Tab = 'library' | 'review' | 'words' | 'voices' | 'index';

/**
 * Voices (2.41): the voice library. Search across Sounds, Voice clips and Sentences with type
 * badges, filters by voice, tag and favourite, clickable words that play exactly that word (private
 * preview, or into the game), a page of recordings at a time with multi-select for bulk changes, a
 * keyboard review queue, each voice's vocabulary and wanted words, and (host) the Soundpad
 * categories whose words are indexed. Every change can be undone and is recorded with who made it.
 */
export function VoicesPanel({ state, host, connected, canPlay }: Props) {
  const { status } = useVoiceStore();
  const [tab, setTab] = useState<Tab>('library');
  const [openId, setOpenId] = useState<string | null>(null);
  useEffect(() => {
    const waiting = takeOpen('recording');
    if (waiting?.kind === 'recording') setOpenId(waiting.recordingId);
    void refreshLibrary();
  }, []);
  useOpenRequests((request) => {
    if (request.kind !== 'recording') return;
    takeOpen('recording');
    setTab('library');
    setOpenId(request.recordingId);
  });
  if (!status) return <p className="muted">Loading the voice library…</p>;
  const tabs: [Tab, string][] = [
    ['library', 'Library'],
    ['review', `Review${status.unreviewed ? ` (${status.unreviewed})` : ''}`],
    ['words', 'Words'],
    ['voices', 'Voices'],
    ...(host ? ([['index', 'Word index']] as [Tab, string][]) : []),
  ];
  return (
    <div className="voices-panel">
      <p className="muted voices-intro">
        {status.count} recording{status.count === 1 ? '' : 's'} kept
        {status.pending ? ` · finding the words of ${status.pending}` : ''}
        {!status.canEdit && ' · the host lets guests look and listen, not change'}
      </p>
      {status.problem && (
        <p className="warning" role="status">
          {status.problem}
        </p>
      )}
      {status.worker && host && <p className="muted voices-worker">{status.worker}</p>}
      <div className="voices-tabs" role="tablist" aria-label="Voice library sections">
        {tabs.map(([key, label]) => (
          <button
            key={key}
            type="button"
            role="tab"
            aria-selected={tab === key}
            onClick={() => {
              setTab(key);
              setOpenId(null);
            }}
          >
            {label}
          </button>
        ))}
      </div>
      {tab === 'library' &&
        (openId ? (
          <RecordingDetail
            id={openId}
            status={status}
            canPlay={canPlay && connected}
            back={() => setOpenId(null)}
          />
        ) : (
          <LibraryView
            state={state}
            status={status}
            canPlay={canPlay && connected}
            open={setOpenId}
          />
        ))}
      {tab === 'review' && <ReviewQueue status={status} canPlay={canPlay && connected} />}
      {tab === 'words' && <WordsAndWanted status={status} />}
      {tab === 'voices' && <VoiceManager status={status} />}
      {tab === 'index' && host && <WordIndexSettings state={state} status={status} />}
    </div>
  );
}

/** The words of a match, each a button that plays it; the matching words are marked. */
function ContextWords({
  words,
  label,
  onPlay,
}: {
  words: ContextWord[];
  label: string;
  onPlay: (word: ContextWord, all: ContextWord[]) => void;
}) {
  return (
    <span className="voices-context" aria-label={label}>
      {words.map((w) => (
        <button
          key={w.i}
          type="button"
          className={`voices-word${w.hit ? ' hit' : ''}${w.u ? ' uncertain' : ''}`}
          aria-label={`Play “${w.t}”${w.hit ? ' (match)' : ''}`}
          title={w.u ? 'Uncertain: check this word and its edges' : undefined}
          onClick={() => onPlay(w, words)}
        >
          {w.hit ? <mark>{w.t}</mark> : w.t}
        </button>
      ))}
    </span>
  );
}

function FilterBar({
  status,
  filters,
  set,
}: {
  status: LibraryStatus;
  filters: Filters;
  set: (f: Filters) => void;
}) {
  return (
    <div className="voices-filters" role="group" aria-label="Filters">
      <select
        aria-label="Voice"
        value={filters.voice}
        onChange={(e) => set({ ...filters, voice: e.target.value })}
      >
        <option value="">All voices</option>
        <option value="none">Unassigned ({status.unassigned})</option>
        {status.voices.map((v) => (
          <option key={v.id} value={v.id}>
            {v.name} ({v.count})
          </option>
        ))}
      </select>
      <select
        aria-label="Tag"
        value={filters.tag}
        onChange={(e) => set({ ...filters, tag: e.target.value })}
      >
        <option value="">All tags</option>
        {status.tags.map((t) => (
          <option key={t} value={t}>
            {t}
          </option>
        ))}
      </select>
      <label className="voices-check">
        <input
          type="checkbox"
          checked={filters.favorite}
          onChange={(e) => set({ ...filters, favorite: e.target.checked })}
        />
        Favourites
      </label>
      {hasFilters(filters) && (
        <button type="button" onClick={() => set({ ...NO_FILTERS, query: filters.query })}>
          <X size={13} /> Clear filters
        </button>
      )}
    </div>
  );
}

function LibraryView({
  state,
  status,
  canPlay,
  open,
}: {
  state: Snapshot;
  status: LibraryStatus;
  canPlay: boolean;
  open: (id: string) => void;
}) {
  const [filters, setFilters] = useState<Filters>(NO_FILTERS);
  const [typed, setTyped] = useState('');
  const [types, setTypes] = useState<Set<ResultType>>(
    () => new Set<ResultType>(['sound', 'clip', 'sentence']),
  );
  const [page, setPage] = useState(0);
  const [list, setList] = useState<{ total: number; items: Recording[] } | null>(null);
  const [results, setResults] = useState<SearchResult[] | null>(null);
  const [picked, setPicked] = useState<Picked>(NOTHING_PICKED);
  const [mode, setMode] = useWordMode();
  const player = useWordPlayer(canPlay);
  // The search runs a moment after typing stops.
  useEffect(() => {
    const timer = setTimeout(() => setFilters((f) => ({ ...f, query: typed.trim() })), 250);
    return () => clearTimeout(timer);
  }, [typed]);
  const fields = filterFields(filters);
  const key = JSON.stringify(fields);
  useEffect(() => setPage(0), [key]);
  useEffect(() => {
    if (filters.query) return;
    let live = true;
    controller
      .run('libraryList', { ...fields, offset: page * PAGE, limit: PAGE }, { quiet: true })
      .then((raw) => {
        if (live) setList(listSchema.parse(raw));
      })
      .catch(() => live && setList({ total: 0, items: [] }));
    return () => {
      live = false;
    };
  }, [key, page, filters.query, status.revision]);
  useEffect(() => {
    if (!filters.query) {
      setResults(null);
      return;
    }
    let live = true;
    controller
      .run('librarySearch', { ...fields, query: filters.query, limit: 100 }, { quiet: true })
      .then((raw) => {
        if (live) setResults(searchSchema.parse(raw).results);
      })
      .catch(() => live && setResults([]));
    return () => {
      live = false;
    };
  }, [key, filters.query, status.revision]);
  const combined = useMemo(
    () =>
      results
        ? combineResults(
            filters.query,
            hasFilters(filters) ? [] : state.library.sounds,
            results,
            types,
          )
        : [],
    [results, filters, state.library.sounds, types],
  );
  const counts = useMemo(
    () =>
      results
        ? countTypes(
            combineResults(
              filters.query,
              hasFilters(filters) ? [] : state.library.sounds,
              results,
              new Set(['sound', 'clip', 'sentence']),
            ),
          )
        : null,
    [results, filters, state.library.sounds],
  );
  const order = list?.items.map((r) => r.id) ?? [];
  const voices = status.voices;
  return (
    <section className="voices-library" aria-label="Voice library">
      <label className="search voices-search">
        <Search size={16} aria-hidden="true" />
        <input
          aria-label="Search the voice library"
          placeholder="Search sounds, voice clips and sentences by name or exact words…"
          value={typed}
          maxLength={200}
          onChange={(e) => setTyped(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Escape' && typed) {
              e.preventDefault();
              e.stopPropagation();
              setTyped('');
            }
          }}
        />
        {typed && (
          <button type="button" aria-label="Clear search" onClick={() => setTyped('')}>
            <X size={14} />
          </button>
        )}
      </label>
      <FilterBar status={status} filters={filters} set={setFilters} />
      <WordModeSwitch mode={mode} set={setMode} canPlay={canPlay} />
      {filters.query ? (
        <>
          <div className="voices-types" role="group" aria-label="Result types">
            {(['sound', 'clip', 'sentence'] as ResultType[]).map((t) => (
              <button
                key={t}
                type="button"
                aria-pressed={types.has(t)}
                onClick={() =>
                  setTypes((current) => {
                    const next = new Set(current);
                    if (next.has(t)) next.delete(t);
                    else next.add(t);
                    return next;
                  })
                }
              >
                {TYPE_LABELS[t]}s {counts ? `(${counts[t]})` : ''}
              </button>
            ))}
          </div>
          {results === null ? (
            <p className="muted">Searching…</p>
          ) : combined.length === 0 ? (
            <p className="muted">
              Nothing found for “{filters.query}”.{' '}
              {types.has('sentence') && 'Sentences arrive with the sentence builder.'}
            </p>
          ) : (
            <ul className="voices-results" aria-label="Search results">
              {combined.map((item) => {
                if (item.type === 'sound')
                  return (
                    <li key={item.key} className="voices-result">
                      <span className="type-badge sound">{TYPE_LABELS.sound}</span>
                      <strong className="voices-title">{item.title}</strong>
                      {item.words && (
                        <ContextWords
                          words={item.words.context}
                          label={`Words in ${item.title}`}
                          onPlay={(w) => player.play(mode, { soundId: item.soundId }, w)}
                        />
                      )}
                      <button
                        type="button"
                        aria-label={`${mode === 'game' ? 'Play' : 'Preview'} ${item.title}`}
                        onClick={() => player.whole(mode, { soundId: item.soundId })}
                      >
                        {mode === 'game' ? <Play size={14} /> : <Headphones size={14} />}
                      </button>
                    </li>
                  );
                if (item.type === 'clip') {
                  const r = item.result;
                  const voice = voiceOf(voices, r.voiceId);
                  return (
                    <li key={item.key} className="voices-result">
                      <span className="type-badge clip">{TYPE_LABELS.clip}</span>
                      <VoiceAvatar voice={voice} size={22} />
                      <button
                        type="button"
                        className="link voices-title"
                        onClick={() => open(r.id)}
                      >
                        {r.title || 'Untitled'}
                      </button>
                      {r.context.length > 0 && (
                        <ContextWords
                          words={r.context}
                          label={`Words in ${r.title}`}
                          onPlay={(w) =>
                            player.play(mode, { recordingId: r.id, soundId: r.soundId }, w)
                          }
                        />
                      )}
                      <button
                        type="button"
                        aria-label={`${mode === 'game' ? 'Play' : 'Preview'} ${r.title}`}
                        onClick={() =>
                          player.whole(mode, { recordingId: r.id, soundId: r.soundId })
                        }
                      >
                        {mode === 'game' ? <Play size={14} /> : <Headphones size={14} />}
                      </button>
                    </li>
                  );
                }
                return null;
              })}
            </ul>
          )}
        </>
      ) : (
        <>
          {picked.ids.length > 0 && (
            <BulkBar status={status} ids={picked.ids} clear={() => setPicked(NOTHING_PICKED)} />
          )}
          {!list ? (
            <p className="muted">Loading…</p>
          ) : list.items.length === 0 ? (
            <p className="muted">
              {hasFilters(filters)
                ? 'No recordings match these filters.'
                : 'Nothing kept yet. Keep a clip (from Mimic or Temp) and it appears here with its words.'}
            </p>
          ) : (
            <ul className="voices-list" aria-label="Recordings">
              {list.items.map((r) => {
                const voice = voiceOf(voices, r.voiceId);
                return (
                  <li key={r.id} className={picked.ids.includes(r.id) ? 'picked' : undefined}>
                    <input
                      type="checkbox"
                      aria-label={`Select ${r.title || 'recording'}`}
                      checked={picked.ids.includes(r.id)}
                      onChange={() => {}}
                      onClick={(e) => setPicked((p) => pick(p, order, r.id, e.shiftKey))}
                    />
                    <VoiceAvatar voice={voice} />
                    <span className="voices-row">
                      <button
                        type="button"
                        className="link voices-title"
                        onClick={() => open(r.id)}
                      >
                        {r.favorite && <Star size={12} className="fav" aria-label="Favourite" />}
                        {r.title || 'Untitled'}
                      </button>
                      <small>
                        {voice?.name ?? 'Unassigned'} · {formatDuration(r.durationMs)}
                        {r.tags.length > 0 && ` · ${r.tags.join(', ')}`}
                        {r.wordsState === 'pending' && ' · finding words…'}
                        {r.wordsState === 'failed' && ' · words could not be found'}
                        {!r.reviewed && ' · new'}
                      </small>
                      {r.text && <span className="voices-text">{r.text}</span>}
                    </span>
                    <button
                      type="button"
                      aria-label={`${mode === 'game' ? 'Play' : 'Preview'} ${r.title}`}
                      onClick={() => player.whole(mode, { recordingId: r.id, soundId: r.soundId })}
                    >
                      {mode === 'game' ? <Play size={14} /> : <Headphones size={14} />}
                    </button>
                  </li>
                );
              })}
            </ul>
          )}
          {list && list.total > PAGE && (
            <div className="voices-pages" role="group" aria-label="Pages">
              <button type="button" disabled={page === 0} onClick={() => setPage((p) => p - 1)}>
                <ChevronLeft size={14} /> Newer
              </button>
              <span>
                {page * PAGE + 1}–{Math.min(list.total, (page + 1) * PAGE)} of {list.total}
              </span>
              <button
                type="button"
                disabled={(page + 1) * PAGE >= list.total}
                onClick={() => setPage((p) => p + 1)}
              >
                Older <ChevronRight size={14} />
              </button>
            </div>
          )}
        </>
      )}
    </section>
  );
}

/** Bulk organisation: a voice, a tag, favourite or reviewed for every selected recording, as one Undo. */
function BulkBar({
  status,
  ids,
  clear,
}: {
  status: LibraryStatus;
  ids: string[];
  clear: () => void;
}) {
  const [tag, setTag] = useState('');
  const n = ids.length;
  const plural = `${n} recording${n === 1 ? '' : 's'}`;
  const run = (fields: Partial<Command>, done: string) =>
    void libraryChange('libraryEdit', { recordingIds: ids, ...fields }, done);
  return (
    <div className="voices-bulk" role="group" aria-label="Change the selected recordings">
      <strong>{n} selected</strong>
      <select
        aria-label="Set the voice of the selected recordings"
        disabled={!status.canEdit}
        value=""
        onChange={(e) => {
          const value = e.target.value;
          if (value) run({ voiceId: value === 'none' ? '' : value }, `Voice set for ${plural}.`);
        }}
      >
        <option value="">Set voice…</option>
        <option value="none">Unassigned</option>
        {status.voices.map((v) => (
          <option key={v.id} value={v.id}>
            {v.name}
          </option>
        ))}
      </select>
      <form
        className="voices-inline"
        onSubmit={(e) => {
          e.preventDefault();
          const clean = cleanTag(tag);
          if (!clean) return;
          run({ addTags: [clean] }, `Tag “${clean}” added to ${plural}.`);
          setTag('');
        }}
      >
        <input
          aria-label="Tag for the selected recordings"
          placeholder="Tag"
          maxLength={40}
          value={tag}
          onChange={(e) => setTag(e.target.value)}
        />
        <button type="submit" disabled={!status.canEdit}>
          <Tag size={13} /> Add tag
        </button>
      </form>
      <button
        type="button"
        disabled={!status.canEdit}
        onClick={() => run({ favorite: true }, `${plural} made favourites.`)}
      >
        <Star size={13} /> Favourite
      </button>
      <button
        type="button"
        disabled={!status.canEdit}
        onClick={() => run({ reviewed: true }, `${plural} marked reviewed.`)}
      >
        Mark reviewed
      </button>
      <button
        type="button"
        disabled={!status.canEdit}
        onClick={() =>
          void libraryChange(
            'libraryRemove',
            { recordingIds: ids },
            `${plural} taken out of the library (files kept).`,
          ).then((ok) => ok && clear())
        }
      >
        <Trash2 size={13} /> Remove from library
      </button>
      <button type="button" onClick={clear}>
        <X size={13} /> Clear selection
      </button>
    </div>
  );
}

/** One recording: its words (each plays), title, voice, tags, favourite, corrected text, where it came from and its history. */
function RecordingDetail({
  id,
  status,
  canPlay,
  back,
}: {
  id: string;
  status: LibraryStatus;
  canPlay: boolean;
  back: () => void;
}) {
  const [data, setData] = useState<z.infer<typeof getSchema> | null>(null);
  const [gone, setGone] = useState(false);
  const [mode, setMode] = useWordMode();
  const player = useWordPlayer(canPlay);
  const [title, setTitle] = useState('');
  const [text, setText] = useState('');
  const [tag, setTag] = useState('');
  const shown = useRef<string | null>(null);
  useEffect(() => {
    let live = true;
    controller
      .run('libraryGet', { recordingId: id }, { quiet: true })
      .then((raw) => {
        if (!live) return;
        const next = getSchema.parse(raw);
        setData(next);
        if (shown.current !== id) {
          shown.current = id;
          setTitle(next.recording.title);
          setText(next.recording.correctedText ?? '');
        }
      })
      .catch(() => live && setGone(true));
    return () => {
      live = false;
    };
  }, [id, status.revision]);
  if (gone)
    return (
      <p className="muted">
        That recording is no longer in the library.{' '}
        <button type="button" className="link" onClick={back}>
          Back
        </button>
      </p>
    );
  if (!data) return <p className="muted">Loading…</p>;
  const r = data.recording;
  const voice = voiceOf(status.voices, r.voiceId);
  const edit = (fields: Partial<Command>, done: string) =>
    void libraryChange('libraryEdit', { recordingId: r.id, ...fields }, done);
  const source =
    r.source === 'history'
      ? 'From the recording history'
      : r.source === 'vrs'
        ? 'From Voice Replay Studio'
        : r.source === 'copy'
          ? 'An edited copy'
          : r.source === 'soundpad'
            ? 'Added from Soundpad'
            : 'Uploaded';
  return (
    <section className="voices-detail" aria-label={`Recording ${r.title}`}>
      <div className="voices-detail-head">
        <button type="button" onClick={back}>
          <ChevronLeft size={14} /> All recordings
        </button>
        <VoiceAvatar voice={voice} size={32} />
        <form
          className="voices-inline grow"
          onSubmit={(e) => {
            e.preventDefault();
            if (title.trim() && title.trim() !== r.title) edit({ title: title.trim() }, 'Renamed.');
          }}
        >
          <input
            aria-label="Title"
            maxLength={200}
            value={title}
            disabled={!status.canEdit}
            onChange={(e) => setTitle(e.target.value)}
          />
          <button
            type="submit"
            disabled={!status.canEdit || !title.trim() || title.trim() === r.title}
          >
            Rename
          </button>
        </form>
        <button
          type="button"
          aria-pressed={r.favorite}
          disabled={!status.canEdit}
          onClick={() =>
            edit(
              { favorite: !r.favorite },
              r.favorite ? 'Not a favourite any more.' : 'Made a favourite.',
            )
          }
        >
          <Star size={14} /> {r.favorite ? 'Favourite' : 'Make favourite'}
        </button>
      </div>
      <p className="muted">
        {source}, kept by {r.byName} · {formatDuration(r.durationMs)}
        {!r.soundId && ' · not in Soundpad right now (preview only)'}
      </p>
      <WordModeSwitch mode={mode} set={setMode} canPlay={canPlay && !!r.soundId} />
      <div className="voices-words" aria-label="Words">
        {data.words.length ? (
          data.words.map((w, i) => (
            <button
              key={i}
              type="button"
              className={`voices-word${w.u ? ' uncertain' : ''}`}
              aria-label={`Play “${w.t}”`}
              onClick={() => player.play(mode, { recordingId: r.id, soundId: r.soundId }, w)}
            >
              {w.t}
            </button>
          ))
        ) : (
          <span className="muted">
            {r.wordsState === 'pending'
              ? 'Finding its words in the background…'
              : r.wordsState === 'failed'
                ? `Its words could not be found${r.wordsError ? `: ${r.wordsError}` : ''}.`
                : 'No words in this recording.'}
          </span>
        )}
        <button
          type="button"
          aria-label={`${mode === 'game' ? 'Play' : 'Preview'} all of it`}
          onClick={() => player.whole(mode, { recordingId: r.id, soundId: r.soundId })}
        >
          {mode === 'game' ? <Play size={14} /> : <Headphones size={14} />} All
        </button>
        {r.wordsState === 'failed' && status.canEdit && (
          <button
            type="button"
            onClick={() =>
              void controller
                .run('libraryRetry', { recordingId: r.id })
                .then(() => refreshLibrary())
            }
          >
            <RotateCcw size={13} /> Try again
          </button>
        )}
      </div>
      <div className="setting-row">
        <label htmlFor="voices-voice">Voice</label>
        <select
          id="voices-voice"
          disabled={!status.canEdit}
          value={r.voiceId ?? ''}
          onChange={(e) => edit({ voiceId: e.target.value }, 'Voice set.')}
        >
          <option value="">Unassigned</option>
          {status.voices.map((v) => (
            <option key={v.id} value={v.id}>
              {v.name}
            </option>
          ))}
        </select>
      </div>
      <div className="voices-tags" aria-label="Tags">
        {r.tags.map((t) => (
          <span key={t} className="voices-tag">
            {t}
            {status.canEdit && (
              <button
                type="button"
                aria-label={`Remove tag ${t}`}
                onClick={() => edit({ removeTags: [t] }, `Tag “${t}” removed.`)}
              >
                <X size={11} />
              </button>
            )}
          </span>
        ))}
        <form
          className="voices-inline"
          onSubmit={(e) => {
            e.preventDefault();
            const clean = cleanTag(tag);
            if (!clean) return;
            edit({ addTags: [clean] }, `Tag “${clean}” added.`);
            setTag('');
          }}
        >
          <input
            aria-label="Add a tag"
            placeholder="Add a tag"
            maxLength={40}
            value={tag}
            disabled={!status.canEdit}
            onChange={(e) => setTag(e.target.value)}
          />
          <button type="submit" disabled={!status.canEdit || !cleanTag(tag)}>
            <Tag size={13} /> Add
          </button>
        </form>
      </div>
      <form
        className="voices-correct"
        onSubmit={(e) => {
          e.preventDefault();
          edit(
            { correctedText: text.trim() },
            text.trim() ? 'Corrected text saved.' : 'Corrected text cleared.',
          );
        }}
      >
        <label htmlFor="voices-text">Corrected text</label>
        <textarea
          id="voices-text"
          rows={2}
          maxLength={4000}
          disabled={!status.canEdit}
          placeholder={data.words.map((w) => w.t).join(' ') || 'What is said'}
          value={text}
          onChange={(e) => setText(e.target.value)}
        />
        <p className="muted">
          Kept apart from the words' timings, which never move. With the same number of words, each
          word takes its corrected spelling in search.
        </p>
        <button type="submit" disabled={!status.canEdit || text.trim() === (r.correctedText ?? '')}>
          Save text
        </button>
      </form>
      {data.audit.length > 0 && (
        <details className="voices-audit">
          <summary>Changes ({data.audit.length})</summary>
          <ol>
            {data.audit.map((a) => (
              <li key={a.id}>
                <time dateTime={new Date(a.at).toISOString()}>
                  {new Date(a.at).toLocaleString()}
                </time>{' '}
                {describeAudit(a, status.voices)}
                {status.canEdit && !a.undoOf && a.field !== 'record' && (
                  <button
                    type="button"
                    className="link"
                    onClick={() =>
                      void controller
                        .run('libraryUndo', { auditId: a.group })
                        .then(() => {
                          controller.notify('Undone.', 'success');
                          return refreshLibrary();
                        })
                        .catch(() => {})
                    }
                  >
                    <Undo2 size={12} /> Undo
                  </button>
                )}
              </li>
            ))}
          </ol>
        </details>
      )}
      {status.canEdit && (
        <button
          type="button"
          className="danger-link"
          onClick={() =>
            void libraryChange(
              'libraryRemove',
              { recordingIds: [r.id] },
              'Taken out of the voice library. Its file is kept.',
            ).then((ok) => ok && back())
          }
        >
          <Trash2 size={13} /> Remove from the library (the file stays)
        </button>
      )}
    </section>
  );
}
