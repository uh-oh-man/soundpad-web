import { useEffect, useRef, useState } from 'react';
import { Check, Headphones, Pause, Play, Plus, SkipForward, Star, Trash2, X } from 'lucide-react';
import { controller } from '../controller';
import { flattenCategories, type Snapshot } from '../protocol';
import {
  extendQueue,
  formatDuration,
  getSchema,
  listSchema,
  queueDone,
  reviewAction,
  startQueue,
  stepQueue,
  vocabularySchema,
  voiceOf,
  type LibraryStatus,
  type LibraryWord,
  type Queue,
  type Recording,
} from '../library/voices';
import { refreshLibrary } from '../library/voice-store';
import { VoiceAvatar, libraryChange, useWordPlayer } from './VoiceShared';

/**
 * The review queue (2.41): new recordings one at a time. Listen (private preview), correct the text,
 * give a voice and tags, then Keep (it is marked reviewed) or Skip, and the next one comes. Keyboard:
 * Space previews, Enter keeps, S or → skips, ← goes back, F favourite, 1–9 the voice of that number.
 */
export function ReviewQueue({ status, canPlay }: { status: LibraryStatus; canPlay: boolean }) {
  const [queue, setQueue] = useState<Queue | null>(null);
  const [items, setItems] = useState<Record<string, Recording>>({});
  const [words, setWords] = useState<LibraryWord[]>([]);
  const [text, setText] = useState('');
  const [tag, setTag] = useState('');
  const player = useWordPlayer(canPlay);
  const region = useRef<HTMLElement>(null);
  useEffect(() => {
    let live = true;
    controller
      .run('libraryList', { reviewed: false, limit: 100 }, { quiet: true })
      .then((raw) => {
        if (!live) return;
        const list = listSchema.parse(raw).items;
        setItems((current) => ({ ...current, ...Object.fromEntries(list.map((r) => [r.id, r])) }));
        // Oldest first: the order they were kept in.
        const ids = list.map((r) => r.id).reverse();
        setQueue((q) => (q ? extendQueue(q, ids) : startQueue(ids)));
      })
      .catch(() => live && setQueue((q) => q ?? startQueue([])));
    return () => {
      live = false;
    };
  }, [status.revision]);
  const current = queue && !queueDone(queue) ? items[queue.ids[queue.at]] : undefined;
  useEffect(() => {
    if (!current) return;
    let live = true;
    setText(current.correctedText ?? '');
    setTag('');
    controller
      .run('libraryGet', { recordingId: current.id }, { quiet: true })
      .then((raw) => {
        if (!live) return;
        const data = getSchema.parse(raw);
        setWords(data.words);
        setItems((all) => ({ ...all, [data.recording.id]: data.recording }));
      })
      .catch(() => live && setWords([]));
    return () => {
      live = false;
    };
  }, [current?.id]);
  // Keyboard first: the queue takes focus once it has loaded.
  const focused = useRef(false);
  useEffect(() => {
    if (!queue || focused.current || !region.current) return;
    focused.current = true;
    region.current.focus();
  }, [queue]);
  if (!queue) return <p className="muted">Loading the review queue…</p>;
  if (!current)
    return (
      <section className="voices-review" aria-label="Review queue">
        <p>
          <strong>All caught up.</strong>{' '}
          {queue.kept.length + queue.skipped.length > 0
            ? `${queue.kept.length} kept, ${queue.skipped.length} skipped.`
            : 'New recordings show up here to listen to, label and keep.'}
        </p>
        {queue.skipped.length > 0 && (
          <button type="button" onClick={() => setQueue(startQueue(queue.skipped))}>
            Go through the skipped ones
          </button>
        )}
      </section>
    );
  const voice = voiceOf(status.voices, current.voiceId);
  const edit = (fields: Parameters<typeof controller.run>[1], done: string) =>
    libraryChange('libraryEdit', { recordingId: current.id, ...fields }, done);
  async function keep() {
    if (!current) return;
    const fields = {
      reviewed: true,
      ...(text.trim() !== (current.correctedText ?? '') && { correctedText: text.trim() }),
    };
    if (await edit(fields, `“${current.title || 'Recording'}” reviewed.`))
      setQueue((q) => (q ? stepQueue(q, 'keep') : q));
  }
  function act(key: string, typing: boolean, ctrl: boolean) {
    const action = reviewAction(key, { typing, ctrl });
    if (!action || !current) return false;
    switch (action.kind) {
      case 'preview':
        if (player.previewing) player.stop();
        else player.whole('preview', { recordingId: current.id });
        break;
      case 'keep':
        void keep();
        break;
      case 'skip':
        setQueue((q) => (q ? stepQueue(q, 'skip') : q));
        break;
      case 'back':
        setQueue((q) => (q ? stepQueue(q, 'back') : q));
        break;
      case 'favorite':
        void edit(
          { favorite: !current.favorite },
          current.favorite ? 'Not a favourite any more.' : 'Made a favourite.',
        );
        break;
      case 'voice': {
        const v = status.voices[action.index];
        if (v) void edit({ voiceId: v.id }, `Voice set to ${v.name}.`);
        break;
      }
      case 'leave':
        if (typing && document.activeElement instanceof HTMLElement) document.activeElement.blur();
        region.current?.focus();
        return true;
    }
    return true;
  }
  return (
    <section
      className="voices-review"
      aria-label="Review queue"
      tabIndex={0}
      ref={region}
      onKeyDown={(e) => {
        const target = e.target as HTMLElement;
        const typing = target.matches('input, textarea, select');
        if (target instanceof HTMLButtonElement && (e.key === ' ' || e.key === 'Enter')) return;
        if (act(e.key, typing, e.ctrlKey || e.metaKey || e.altKey)) {
          e.preventDefault();
          e.stopPropagation();
        }
      }}
    >
      <p className="muted">
        {queue.at + 1} of {queue.ids.length} · Space listens, Enter keeps, S skips, ← goes back, F
        favourite, 1–9 sets the voice.
      </p>
      <div className="voices-review-card">
        <VoiceAvatar voice={voice} size={40} />
        <div>
          <strong className="voices-title">{current.title || 'Untitled'}</strong>
          <small>
            {voice?.name ?? 'Unassigned'} · {formatDuration(current.durationMs)} · kept by{' '}
            {current.byName}
            {current.favorite && ' · favourite'}
          </small>
          <p className="voices-text">
            {words.length
              ? words.map((w) => w.t).join(' ')
              : current.wordsState === 'pending'
                ? 'Finding its words…'
                : 'No words found.'}
          </p>
        </div>
      </div>
      <div className="voices-voice-keys" role="group" aria-label="Voice">
        {status.voices.slice(0, 9).map((v, i) => (
          <button
            key={v.id}
            type="button"
            aria-pressed={current.voiceId === v.id}
            disabled={!status.canEdit}
            onClick={() => void edit({ voiceId: v.id }, `Voice set to ${v.name}.`)}
          >
            <kbd>{i + 1}</kbd> {v.name}
          </button>
        ))}
        {status.voices.length === 0 && (
          <span className="muted">Add voices in the Voices tab to label recordings.</span>
        )}
      </div>
      <form
        className="voices-inline"
        onSubmit={(e) => {
          e.preventDefault();
          const clean = tag.trim().slice(0, 40);
          if (clean) void edit({ addTags: [clean] }, `Tag “${clean}” added.`);
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
        <button type="submit" disabled={!status.canEdit}>
          <Plus size={13} /> Tag
        </button>
        {current.tags.length > 0 && <small className="muted">{current.tags.join(', ')}</small>}
      </form>
      <label className="voices-correct">
        Corrected text (saved with Keep)
        <textarea
          rows={2}
          maxLength={4000}
          disabled={!status.canEdit}
          placeholder={words.map((w) => w.t).join(' ') || 'What is said'}
          value={text}
          onChange={(e) => setText(e.target.value)}
        />
      </label>
      <div className="voices-review-actions">
        <button type="button" onClick={() => act(' ', false, false)}>
          <Headphones size={14} /> {player.previewing ? 'Stop' : 'Listen'}
        </button>
        <button
          type="button"
          className="primary"
          disabled={!status.canEdit}
          onClick={() => void keep()}
        >
          <Check size={14} /> Keep
        </button>
        <button type="button" onClick={() => act('s', false, false)}>
          <SkipForward size={14} /> Skip
        </button>
        <button
          type="button"
          disabled={queue.at === 0}
          onClick={() => act('ArrowLeft', false, false)}
        >
          Back
        </button>
        <button
          type="button"
          aria-pressed={current.favorite}
          disabled={!status.canEdit}
          onClick={() => act('f', false, false)}
        >
          <Star size={14} /> Favourite
        </button>
      </div>
    </section>
  );
}

/** Each voice's vocabulary (distinct words with counts) and the words people want from it. */
export function WordsAndWanted({ status }: { status: LibraryStatus }) {
  const [voiceId, setVoiceId] = useState(status.voices[0]?.id ?? '');
  const [prefix, setPrefix] = useState('');
  const [page, setPage] = useState(0);
  const [data, setData] = useState<ReturnType<typeof vocabularySchema.parse> | null>(null);
  const [wanted, setWanted] = useState('');
  useEffect(() => setPage(0), [voiceId, prefix]);
  useEffect(() => {
    let live = true;
    const timer = setTimeout(() => {
      controller
        .run(
          'libraryVocabulary',
          {
            ...(voiceId && { voiceId }),
            ...(prefix.trim() && { query: prefix.trim() }),
            offset: page * 100,
            limit: 100,
          },
          { quiet: true },
        )
        .then((raw) => live && setData(vocabularySchema.parse(raw)))
        .catch(() => live && setData(null));
    }, 150);
    return () => {
      live = false;
      clearTimeout(timer);
    };
  }, [voiceId, prefix, page, status.revision]);
  const voice = voiceOf(status.voices, voiceId);
  return (
    <section className="voices-words-tab" aria-label="Words">
      <div className="voices-filters">
        <select
          aria-label="Whose words"
          value={voiceId}
          onChange={(e) => setVoiceId(e.target.value)}
        >
          <option value="">Every voice</option>
          {status.voices.map((v) => (
            <option key={v.id} value={v.id}>
              {v.name}
            </option>
          ))}
        </select>
        <input
          aria-label="Words starting with"
          placeholder="Starts with…"
          maxLength={40}
          value={prefix}
          onChange={(e) => setPrefix(e.target.value)}
        />
      </div>
      {voice && (
        <div className="voices-wanted">
          <h3>Words wanted from {voice.name}</h3>
          <p className="muted">
            When a new recording of {voice.name}, or the live recording, has one of these, you get a
            quiet notice with a way to jump there. Nothing is captured or played for you.
          </p>
          <ul aria-label={`Wanted words for ${voice.name}`}>
            {(data?.wanted ?? []).map((w) => (
              <li key={w.id}>
                <span>{w.text}</span>
                <span className={`voices-available${w.available ? ' yes' : ''}`}>
                  {w.available ? `available (${w.count})` : 'not yet'}
                </span>
                {status.canEdit && (
                  <button
                    type="button"
                    aria-label={`Stop wanting “${w.text}”`}
                    onClick={() =>
                      void libraryChange(
                        'libraryWanted',
                        { action: 'remove', name: w.id },
                        `“${w.text}” is not wanted any more.`,
                      )
                    }
                  >
                    <X size={12} />
                  </button>
                )}
              </li>
            ))}
          </ul>
          {status.canEdit && (
            <form
              className="voices-inline"
              onSubmit={(e) => {
                e.preventDefault();
                if (!wanted.trim()) return;
                void libraryChange(
                  'libraryWanted',
                  { action: 'add', voiceId: voice.id, query: wanted.trim() },
                  `Wanting “${wanted.trim()}” from ${voice.name}.`,
                );
                setWanted('');
              }}
            >
              <input
                aria-label="Wanted word or phrase"
                placeholder="A word or phrase"
                maxLength={100}
                value={wanted}
                onChange={(e) => setWanted(e.target.value)}
              />
              <button type="submit">
                <Plus size={13} /> Want it
              </button>
            </form>
          )}
        </div>
      )}
      <h3>Vocabulary{voice ? ` of ${voice.name}` : ''}</h3>
      {!data ? (
        <p className="muted">Loading…</p>
      ) : data.words.length === 0 ? (
        <p className="muted">No words yet.</p>
      ) : (
        <>
          <ul className="voices-vocabulary" aria-label="Vocabulary">
            {data.words.map((w) => (
              <li key={w.text}>
                {w.text} <small>{w.count}</small>
              </li>
            ))}
          </ul>
          {data.total > 100 && (
            <div className="voices-pages">
              <button type="button" disabled={page === 0} onClick={() => setPage((p) => p - 1)}>
                Previous
              </button>
              <span>
                {page * 100 + 1}–{Math.min(data.total, (page + 1) * 100)} of {data.total} words
              </span>
              <button
                type="button"
                disabled={(page + 1) * 100 >= data.total}
                onClick={() => setPage((p) => p + 1)}
              >
                Next
              </button>
            </div>
          )}
        </>
      )}
    </section>
  );
}

/** Voices: a name, a colour and initials. Renamed, recoloured or removed by the host and friends, with Undo. */
export function VoiceManager({ status }: { status: LibraryStatus }) {
  const [name, setName] = useState('');
  return (
    <section className="voices-manage" aria-label="Voices">
      <ul>
        {status.voices.map((v) => (
          <VoiceRow key={v.id} voice={v} canEdit={status.canEdit} />
        ))}
      </ul>
      {status.voices.length === 0 && (
        <p className="muted">No voices yet. A voice is who a recording sounds like.</p>
      )}
      {status.canEdit && (
        <form
          className="voices-inline"
          onSubmit={(e) => {
            e.preventDefault();
            if (!name.trim()) return;
            void libraryChange(
              'libraryVoice',
              { action: 'create', name: name.trim() },
              `Voice “${name.trim()}” added.`,
            );
            setName('');
          }}
        >
          <input
            aria-label="New voice name"
            placeholder="New voice"
            maxLength={40}
            value={name}
            onChange={(e) => setName(e.target.value)}
          />
          <button type="submit">
            <Plus size={13} /> Add voice
          </button>
        </form>
      )}
    </section>
  );
}

function VoiceRow({
  voice,
  canEdit,
}: {
  voice: LibraryStatus['voices'][number];
  canEdit: boolean;
}) {
  const [name, setName] = useState(voice.name);
  useEffect(() => setName(voice.name), [voice.name]);
  return (
    <li className="voices-voice">
      <VoiceAvatar voice={voice} size={30} />
      <form
        className="voices-inline grow"
        onSubmit={(e) => {
          e.preventDefault();
          if (name.trim() && name.trim() !== voice.name)
            void libraryChange(
              'libraryVoice',
              { action: 'update', voiceId: voice.id, name: name.trim() },
              `Renamed to ${name.trim()}.`,
            );
        }}
      >
        <input
          aria-label={`Name of ${voice.name}`}
          maxLength={40}
          value={name}
          disabled={!canEdit}
          onChange={(e) => setName(e.target.value)}
        />
        <input
          type="color"
          aria-label={`Colour of ${voice.name}`}
          value={voice.color}
          disabled={!canEdit}
          onChange={(e) =>
            void libraryChange(
              'libraryVoice',
              { action: 'update', voiceId: voice.id, color: e.target.value.toLowerCase() },
              `${voice.name}'s colour changed.`,
            )
          }
        />
        <small>
          {voice.count} recording{voice.count === 1 ? '' : 's'}
        </small>
      </form>
      {canEdit && (
        <button
          type="button"
          aria-label={`Delete ${voice.name}`}
          disabled={voice.count > 0}
          title={voice.count > 0 ? 'Give its recordings another voice first' : undefined}
          onClick={() =>
            void libraryChange(
              'libraryVoice',
              { action: 'delete', voiceId: voice.id },
              `Voice “${voice.name}” deleted.`,
            )
          }
        >
          <Trash2 size={13} />
        </button>
      )}
    </li>
  );
}

/**
 * Host: which Soundpad categories are transcribed on this PC in the background (Gaming priority, one
 * file at a time, resumable, never changing the files), its progress and pause; and the experimental
 * speaker grouping switch.
 */
export function WordIndexSettings({ state, status }: { state: Snapshot; status: LibraryStatus }) {
  const chosen = status.settings.indexCategories ?? [];
  const [picked, setPicked] = useState<string[]>(chosen);
  useEffect(
    () => setPicked(status.settings.indexCategories ?? []),
    [JSON.stringify(status.settings.indexCategories)],
  );
  const categories = flattenCategories(state.library.categories).filter(
    ({ category }) => category.name,
  );
  const index = status.index;
  const changed = JSON.stringify([...picked].sort()) !== JSON.stringify([...chosen].sort());
  return (
    <section className="voices-index" aria-label="Word index">
      <p className="muted">
        Choose Soundpad categories whose sounds should be word-searchable. Their speech is
        recognised on this PC in the background, one file at a time at Gaming priority; files
        already done are skipped, and the sound files are only read, never changed.
      </p>
      <fieldset>
        <legend>Categories to index</legend>
        {categories.length === 0 && <p className="muted">No Soundpad categories.</p>}
        {categories.map(({ category, depth }) => (
          <label key={category.id} className="voices-check" style={{ paddingLeft: depth * 14 }}>
            <input
              type="checkbox"
              checked={picked.includes(category.name)}
              onChange={(e) =>
                setPicked((p) =>
                  e.target.checked ? [...p, category.name] : p.filter((n) => n !== category.name),
                )
              }
            />
            {category.name} <small>({category.soundIds.length})</small>
          </label>
        ))}
      </fieldset>
      <button
        type="button"
        className="primary"
        disabled={!changed}
        onClick={() =>
          void controller
            .run('libraryIndex', { categories: picked })
            .then(() => {
              controller.notify('Word index categories saved.', 'success');
              return refreshLibrary();
            })
            .catch(() => {})
        }
      >
        Save categories
      </button>
      {index.active && (
        <p role="status" className="voices-progress">
          {index.indexed} of {index.total} sounds indexed
          {index.failed ? `, ${index.failed} could not be read` : ''}
          {index.paused ? ' · paused' : index.current ? ` · now: ${index.current}` : ''}
          {status.worker && !index.paused ? ` · ${status.worker}` : ''}
        </p>
      )}
      {index.active && (
        <button
          type="button"
          onClick={() =>
            void controller
              .run('libraryIndex', { action: index.paused ? 'resume' : 'pause' })
              .then(() => refreshLibrary())
              .catch(() => {})
          }
        >
          {index.paused ? <Play size={13} /> : <Pause size={13} />}{' '}
          {index.paused ? 'Resume indexing' : 'Pause indexing'}
        </button>
      )}
      <div className="setting-row">
        <div>
          <strong>Group speakers (experimental)</strong>
          <p>
            Groups the phrases of this session by how they sound (“Speaker 1, 2…”), for people to
            name. A prototype measured only on synthetic voices: clear speech grouped well; speech
            over music and overlapping voices did not. Uncertain phrases stay Unassigned; nothing
            carries over to another session.
          </p>
        </div>
        <input
          type="checkbox"
          aria-label="Group speakers (experimental)"
          checked={status.settings.groupSpeakers}
          onChange={(e) =>
            void controller
              .run('librarySettings', { groupSpeakers: e.target.checked })
              .then(() => refreshLibrary())
              .catch(() => {})
          }
        />
      </div>
    </section>
  );
}
