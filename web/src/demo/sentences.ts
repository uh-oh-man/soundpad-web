import type { Command, PaletteRef, PhraseBlock, SentenceInput } from '../protocol';
import { LAUGH, wordKey, type DemoVoices } from './voices';
import type { MixPart } from './dsp';

/**
 * The example's sentences, speaking palette and performance sets (Stage G, spec 11.3): built only from the
 * example voice library (`voices.ts`), in memory, and answered in the companion's shapes (`sentenceSchema`,
 * `listSchema`, `alternativesSchema`, `paletteSchema`, `setsSchema` in library/sentences.ts). The rules follow
 * the companion's SentenceComposer: the text is matched word by word, longest recorded phrase first; a word no
 * recording has is a missing piece that plays as a gap or a bleep; "[laugh]" is the named nonverbal piece;
 * pieces nobody placed by hand flow one after another. `render` turns a sentence into parts for the engine.
 */

export const SENTENCE_UNAVAILABLE = {
  resolve: 'Not available in the example: it has no similar recorded words or splices to offer.',
} as const;

const MAX_PHRASE = 8;
const FLUENT_GAP_MS = 40;
const PAD_BEFORE_MS = 20,
  PAD_AFTER_MS = 10;
const MAX_PIECES = 400;
const MAX_TIMELINE_MS = 600000;
/** A missing word keeps roughly the time it would take to say. */
export const missingMs = (word: string) => Math.min(900, Math.max(250, 120 + 70 * word.length));
const NAME = /^[a-z][a-z ]{0,29}$/;

type Effect = { reverse: boolean; speed: number; keepPitch: boolean; echo: boolean };
type Source = { recordingId: string; startMs: number; endMs: number; voiceId: string | null };
export type DemoPiece = {
  id: string;
  kind: 'word' | 'named' | 'missing' | 'pause';
  tokenStart: number | null;
  tokenEnd: number | null;
  text: string;
  offsetMs: number;
  /** Pauses and missing words only; recorded pieces last as long as their source. */
  durationMs: number;
  manual: boolean;
  chosen: boolean;
  fadeInMs: number;
  fadeOutMs: number;
  gain: number | null;
  missing: 'gap' | 'bleep' | null;
  namedId: string | null;
  effect: Effect | null;
  source: Source | null;
  /** 2.45: a voice slot or a phrase block keeps these words to this voice. */
  voiceLock?: string | null;
};
type Settings = {
  chaos: number;
  timing: 'fluent' | 'original';
  balance: boolean;
  cleanup: boolean;
  altered: boolean;
  voiceIds: string[];
  effect: Effect | null;
};
type Version = {
  n: number;
  at: number;
  byName: string;
  durationMs: number;
  text: string;
  pieces: DemoPiece[];
  settings: Settings;
};
export type DemoSentence = {
  id: string;
  title: string;
  text: string;
  ownerName: string;
  origin: 'typed' | 'performance' | 'template';
  revision: number;
  kept: boolean;
  pinned: boolean;
  settings: Settings;
  pieces: DemoPiece[];
  versions: Version[];
  updated: number;
};
type Named = {
  id: string;
  name: string;
  recordingId: string;
  startMs: number;
  endMs: number;
  byName: string;
};
type Take = {
  recordingId: string;
  voiceId: string | null;
  startMs: number;
  endMs: number;
  text: string;
  index: number;
};
type PerfSet = {
  id: string;
  name: string;
  voiceIds: string[];
  favorites: PaletteRef[];
  templates: { id: string; name: string; text: string }[];
  blocks?: PhraseBlock[];
};

const hex = (bytes: number) =>
  Array.from(crypto.getRandomValues(new Uint8Array(bytes)), (b) =>
    b.toString(16).padStart(2, '0'),
  ).join('');
const copy = <T>(value: T): T => structuredClone(value);
/** Deterministic choice for chaos: the same sentence, word and setting picks the same take. */
function pick<T>(list: T[], seed: string, chaos: number) {
  if (list.length < 2 || chaos <= 0) return list[0];
  let hash = 2166136261;
  for (const ch of seed) hash = Math.imul(hash ^ ch.charCodeAt(0), 16777619) >>> 0;
  const reach = Math.max(1, Math.ceil((list.length * chaos) / 100));
  return list[hash % reach];
}

/** The text as words: "[laugh]" (any [name]) is a nonverbal piece; punctuation around words is dropped. */
export function tokens(text: string) {
  const result: { key: string; display: string }[] = [];
  for (const raw of text.split(/\s+/).filter(Boolean)) {
    if (result.length >= 400) break;
    const bare = raw.replace(/[.,!?;:]+$/, '');
    if (bare.length >= 3 && bare.startsWith('[') && bare.endsWith(']')) {
      const name = bare.slice(1, -1).trim().toLocaleLowerCase();
      if (NAME.test(name)) {
        result.push({ key: `[${name}]`, display: raw });
        continue;
      }
    }
    const key = wordKey(raw);
    if (key) result.push({ key, display: raw });
  }
  return result;
}

/** Each word of the text with where it ends in it (2.45: Add to sentence at a caret). */
function wordEnds(text: string) {
  const ends: { start: number; end: number }[] = [];
  for (const m of text.matchAll(/\S+/g))
    if (ends.length < 400 && tokens(m[0]).length === 1)
      ends.push({ start: m.index, end: m.index + m[0].length });
  return ends;
}

/** A template's voice marks (2.45): {@<voice id>} before a voice's words, {@} for the mix. */
export function voiceRuns(marked: string) {
  const runs: { text: string; voice: string | null }[] = [];
  let voice: string | null = null,
    at = 0;
  for (const m of marked.matchAll(/\{@([a-f0-9]{32})?\}/g)) {
    runs.push({ text: marked.slice(at, m.index).trim(), voice });
    voice = m[1] ?? null;
    at = m.index + m[0].length;
  }
  runs.push({ text: marked.slice(at).trim(), voice });
  return runs.filter((r) => r.text);
}

export class DemoSentences {
  /** Rises with every change: the snapshot's `sentences` (pages fetch sentenceList when it moves). */
  revision = 1;
  private sentences: DemoSentence[] = [];
  named: Named[] = [];
  private sets: PerfSet[] = [
    { id: 'default', name: 'Default', voiceIds: [], favorites: [], templates: [] },
  ];
  private activeSet = 'default';
  private performance: { started: number; taps: { at: number; item: PaletteRef }[] } | null = null;

  constructor(private library: DemoVoices) {
    const laugh = library.live().find((r) => !r.words.length);
    if (laugh)
      this.named.push({
        id: hex(16),
        name: LAUGH.name,
        recordingId: laugh.id,
        startMs: LAUGH.startMs,
        endMs: Math.min(LAUGH.endMs, laugh.durationMs),
        byName: 'Example',
      });
  }

  private changed() {
    this.revision++;
  }

  // ---- The corpus: words and phrases of the library --------------------------------------------------------

  private allowed(voiceId: string | null, settings: Settings) {
    return !settings.voiceIds.length || (!!voiceId && settings.voiceIds.includes(voiceId));
  }

  /** Recorded takes of exactly these words, in library order. */
  private takes(keys: string[], settings: Settings): Take[] {
    const found: Take[] = [];
    for (const r of this.library.live()) {
      if (!this.allowed(r.voiceId, settings)) continue;
      const words = r.words.map((w) => wordKey(w.t));
      for (let i = 0; i + keys.length <= words.length; i++)
        if (keys.every((k, j) => words[i + j] === k))
          found.push({
            recordingId: r.id,
            voiceId: r.voiceId,
            startMs: Math.max(0, r.words[i].s - PAD_BEFORE_MS),
            endMs: r.words[i + keys.length - 1].e + PAD_AFTER_MS,
            text: r.words
              .slice(i, i + keys.length)
              .map((w) => w.t)
              .join(' '),
            index: found.length,
          });
    }
    return found;
  }

  private namedTakes(name: string, settings: Settings): Take[] {
    return this.named
      .filter((n) => n.name === name)
      .flatMap((n, index) => {
        const r = this.library.live().find((x) => x.id === n.recordingId);
        return r && this.allowed(r.voiceId, settings)
          ? [
              {
                recordingId: n.recordingId,
                voiceId: r.voiceId,
                startMs: n.startMs,
                endMs: n.endMs,
                text: `[${n.name}]`,
                index,
              },
            ]
          : [];
      });
  }

  /** Pieces for tokens [a, b): the longest recorded phrase at each place, else a missing word. */
  private match(
    id: string,
    toks: ReturnType<typeof tokens>,
    a: number,
    b: number,
    settings: Settings,
  ) {
    const pieces: DemoPiece[] = [];
    let i = a;
    while (i < b) {
      const token = toks[i];
      if (token.key.startsWith('[')) {
        const named = this.named.find((n) => `[${n.name}]` === token.key);
        const take =
          named && pick(this.namedTakes(named.name, settings), `${id}:${i}`, settings.chaos);
        pieces.push(
          take && named
            ? this.piece('named', i, i + 1, `[${named.name}]`, take, named.id)
            : this.missingPiece(i, token),
        );
        i++;
        continue;
      }
      let done = false;
      for (let len = Math.min(MAX_PHRASE, b - i); len >= 1 && !done; len--) {
        const keys = toks.slice(i, i + len).map((t) => t.key);
        if (keys.some((k) => k.startsWith('['))) continue;
        const takes = this.takes(keys, settings);
        if (!takes.length) continue;
        const take = pick(takes, `${id}:${i}:${len}`, settings.chaos);
        pieces.push(
          this.piece(
            'word',
            i,
            i + len,
            toks
              .slice(i, i + len)
              .map((t) => t.display)
              .join(' '),
            take,
          ),
        );
        i += len;
        done = true;
      }
      if (!done) {
        pieces.push(this.missingPiece(i, token));
        i++;
      }
    }
    return pieces;
  }

  private blank(): Omit<DemoPiece, 'kind' | 'tokenStart' | 'tokenEnd' | 'text'> {
    return {
      id: hex(6),
      offsetMs: 0,
      durationMs: 0,
      manual: false,
      chosen: false,
      fadeInMs: 0,
      fadeOutMs: 0,
      gain: null,
      missing: null,
      namedId: null,
      effect: null,
      source: null,
    };
  }

  private piece(
    kind: 'word' | 'named',
    start: number,
    end: number,
    text: string,
    take: Take,
    namedId: string | null = null,
  ): DemoPiece {
    return {
      ...this.blank(),
      kind,
      tokenStart: start,
      tokenEnd: end,
      text,
      namedId,
      source: {
        recordingId: take.recordingId,
        startMs: take.startMs,
        endMs: take.endMs,
        voiceId: take.voiceId,
      },
    };
  }

  private missingPiece(index: number, token: { key: string; display: string }): DemoPiece {
    return {
      ...this.blank(),
      kind: 'missing',
      tokenStart: index,
      tokenEnd: index + 1,
      text: token.display,
      missing: 'gap',
      durationMs: missingMs(token.key),
    };
  }

  // ---- Layout and views ------------------------------------------------------------------------------------

  static duration(p: DemoPiece) {
    const speed = p.effect?.speed && p.effect.speed > 0 ? p.effect.speed : 1;
    return p.source ? (p.source.endMs - p.source.startMs) / speed : p.durationMs;
  }

  /** Places the pieces nobody placed by hand one after another, in the order of their words. */
  private layout(s: DemoSentence, timing = s.settings.timing) {
    let cursor = 0;
    const flowing = s.pieces
      .filter((p) => p.tokenStart !== null)
      .sort((x, y) => x.tokenStart! - y.tokenStart! || x.offsetMs - y.offsetMs);
    for (const p of flowing) {
      if (!p.manual) p.offsetMs = Math.min(cursor, MAX_TIMELINE_MS);
      const gap =
        p.kind === 'pause' || p.kind === 'missing'
          ? 0
          : timing === 'original'
            ? 120
            : FLUENT_GAP_MS;
      cursor = p.offsetMs + DemoSentences.duration(p) + gap;
    }
    s.pieces.sort(
      (x, y) => x.offsetMs - y.offsetMs || (x.tokenStart ?? 1e9) - (y.tokenStart ?? 1e9),
    );
  }

  private recompose(s: DemoSentence) {
    const keep = s.pieces.filter((p) => p.tokenStart === null || p.voiceLock);
    if (keep.some((p) => p.voiceLock)) {
      // Words a voice slot chose a voice for stay; the rest is matched again around them.
      const toks = tokens(s.text);
      const covered = new Set(keep.flatMap((p) => range(p.tokenStart ?? 0, p.tokenEnd ?? 0)));
      const fresh: DemoPiece[] = [];
      for (let i = 0; i < toks.length;) {
        if (covered.has(i)) {
          i++;
          continue;
        }
        let j = i;
        while (j < toks.length && !covered.has(j)) j++;
        fresh.push(...this.match(s.id, toks, i, j, s.settings));
        i = j;
      }
      s.pieces = [...keep, ...fresh];
      this.layout(s);
      return;
    }
    s.pieces = [...keep, ...this.match(s.id, tokens(s.text), 0, tokens(s.text).length, s.settings)];
    this.layout(s);
  }

  /** The text changed: pieces of the unchanged words before and after stay; only the changed words match again. */
  private rematch(s: DemoSentence, text: string) {
    const before = tokens(s.text),
      after = tokens(text);
    let head = 0;
    while (head < before.length && head < after.length && before[head].key === after[head].key)
      head++;
    let tail = 0;
    while (
      tail < before.length - head &&
      tail < after.length - head &&
      before[before.length - 1 - tail].key === after[after.length - 1 - tail].key
    )
      tail++;
    const shift = after.length - before.length;
    const kept: DemoPiece[] = [];
    let from = head,
      to = after.length - tail;
    for (const p of s.pieces) {
      if (p.tokenStart === null || p.tokenEnd === null) kept.push(p);
      else if (p.tokenEnd <= head) kept.push(p);
      else if (p.tokenStart >= before.length - tail)
        kept.push({ ...p, tokenStart: p.tokenStart + shift, tokenEnd: p.tokenEnd + shift });
      else {
        // A piece across the change is matched again whole.
        from = Math.min(from, p.tokenStart);
        to = Math.max(to, Math.min(after.length, p.tokenEnd + shift));
      }
    }
    s.text = text;
    s.pieces = [
      ...kept.filter((p) => p.tokenStart === null || p.tokenEnd! <= from || p.tokenStart >= to),
      ...this.match(s.id, after, from, to, s.settings),
    ];
    this.layout(s);
  }

  private sourceView(src: Source) {
    const r = this.library.recordings.find((x) => x.id === src.recordingId);
    return {
      ...src,
      label: null,
      recordingMs: r?.durationMs ?? null,
      state: r && !r.removed ? ('ok' as const) : ('missing' as const),
    };
  }

  private end(pieces: DemoPiece[]) {
    return pieces.reduce((m, p) => Math.max(m, p.offsetMs + DemoSentences.duration(p)), 0);
  }

  view(s: DemoSentence) {
    const ordered = [...s.pieces].sort((a, b) => a.offsetMs - b.offsetMs);
    const overlaps: { first: string; second: string; ms: number }[] = [];
    for (let i = 0; i < ordered.length; i++)
      for (let j = i + 1; j < ordered.length; j++) {
        const endI = ordered[i].offsetMs + DemoSentences.duration(ordered[i]);
        if (ordered[j].offsetMs >= endI) break;
        overlaps.push({
          first: ordered[i].id,
          second: ordered[j].id,
          ms: Math.round((endI - ordered[j].offsetMs) * 10) / 10,
        });
      }
    return {
      id: s.id,
      title: s.title,
      text: s.text,
      ownerName: s.ownerName,
      mine: true,
      origin: s.origin,
      revision: s.revision,
      kept: s.kept,
      pinned: s.pinned,
      canEdit: true,
      settings: s.settings,
      tokens: tokens(s.text).map((t) => t.display),
      pieces: ordered.map((p) => ({
        id: p.id,
        kind: p.kind,
        tokenStart: p.tokenStart,
        tokenEnd: p.tokenEnd,
        text: p.text,
        offsetMs: Math.round(p.offsetMs * 10) / 10,
        durationMs: Math.round(DemoSentences.duration(p) * 10) / 10,
        manual: p.manual,
        chosen: p.chosen,
        fadeInMs: p.fadeInMs,
        fadeOutMs: p.fadeOutMs,
        gain: p.gain,
        missing: p.missing,
        substitute: null,
        namedId: p.namedId,
        effect: p.effect,
        voiceId: p.source?.voiceId ?? null,
        voiceLock: p.voiceLock ?? null,
        source: p.source ? this.sourceView(p.source) : null,
        parts: null,
      })),
      overlaps,
      durationMs: Math.round(this.end(ordered) * 10) / 10,
      missing: s.pieces.filter((p) => p.kind === 'missing').length,
      suggestContrast: false,
      versions: [...s.versions].reverse().map((v) => ({
        n: v.n,
        at: v.at,
        byName: v.byName,
        durationMs: v.durationMs,
        text: v.text,
        affected: [],
      })),
    };
  }

  list() {
    return {
      revision: this.revision,
      canBuild: true,
      canKeep: true,
      problem: null,
      sentences: [...this.sentences]
        .sort((a, b) => b.updated - a.updated)
        .map((s) => ({
          id: s.id,
          title: s.title,
          text: s.text.slice(0, 400),
          ownerName: s.ownerName,
          mine: true,
          kept: s.kept,
          pinned: s.pinned,
          updated: s.updated,
          origin: s.origin,
          versions: s.versions.length,
          durationMs: Math.round(this.end(s.pieces)),
          missing: s.pieces.filter((p) => p.kind === 'missing').length,
          canEdit: true,
        })),
      named: this.named.map((n) => ({ ...n })),
      // 2.45: the example has no guests.
      guestUnkept: [],
    };
  }

  find(id: string | undefined) {
    const s = this.sentences.find((x) => x.id === id);
    if (!s) throw Error('That sentence is no longer there.');
    return s;
  }

  private touch(s: DemoSentence) {
    s.revision++;
    s.updated = Date.now();
    this.changed();
  }

  create(input: SentenceInput | undefined, ownerName: string) {
    const marked = (input?.text ?? '').trim().slice(0, 4000);
    // A template's voice slots and phrase blocks (2.45) arrive as voice marks in its text.
    const runs = input?.origin === 'template' && marked.includes('{@') ? voiceRuns(marked) : null;
    const text = runs ? runs.map((r) => r.text).join(' ') : marked;
    // An empty sentence is allowed: Add to sentence (2.45) starts one and adds to it.
    if (this.sentences.length >= 100)
      throw Error('That is as many sentences as the example keeps.');
    const s: DemoSentence = {
      id: hex(16),
      title: '',
      text,
      ownerName,
      origin: input?.origin ?? 'typed',
      revision: 1,
      kept: false,
      pinned: false,
      settings: {
        chaos: 0,
        timing: 'fluent',
        balance: false,
        cleanup: false,
        altered: false,
        voiceIds: [],
        effect: null,
      },
      pieces: [],
      versions: [],
      updated: Date.now(),
    };
    if (runs) {
      const toks = tokens(text);
      let at = 0;
      for (const run of runs) {
        const count = tokens(run.text).length;
        const settings = run.voice ? { ...s.settings, voiceIds: [run.voice] } : s.settings;
        for (const p of this.match(s.id, toks, at, at + count, settings))
          s.pieces.push({ ...p, voiceLock: run.voice });
        at += count;
      }
      this.layout(s);
    } else this.recompose(s);
    this.sentences.push(s);
    this.changed();
    return this.view(s);
  }

  text(c: Command) {
    const s = this.find(c.sentenceId);
    const text = (c.sentence?.text ?? '').trim().slice(0, 4000);
    this.rematch(s, text);
    this.touch(s);
    return this.view(s);
  }

  discard(c: Command) {
    // 2.45: clearing a guest's unkept sentences; the example has no guests.
    if (!c.sentenceId && c.guestId) return { removed: 0 };
    const s = this.find(c.sentenceId);
    this.sentences = this.sentences.filter((x) => x !== s);
    this.changed();
    return { removed: true };
  }

  private pieceOf(s: DemoSentence, id: string | undefined) {
    const p = s.pieces.find((x) => x.id === id);
    if (!p) throw Error('That piece is no longer in the sentence.');
    return p;
  }

  private alternatives(s: DemoSentence, p: DemoPiece) {
    if (p.kind === 'named') {
      const named = this.named.find((n) => n.id === p.namedId);
      return named ? this.namedTakes(named.name, s.settings) : [];
    }
    if (p.kind !== 'word' || p.tokenStart === null || p.tokenEnd === null) return [];
    return this.takes(
      tokens(s.text)
        .slice(p.tokenStart, p.tokenEnd)
        .map((t) => t.key),
      s.settings,
    );
  }

  private current(p: DemoPiece, takes: Take[]) {
    return takes.findIndex(
      (t) =>
        t.recordingId === p.source?.recordingId &&
        t.startMs === p.source.startMs &&
        t.endMs === p.source.endMs,
    );
  }

  alternativesView(c: Command) {
    const s = this.find(c.sentenceId);
    const p = this.pieceOf(s, c.sentence?.pieces?.[0]);
    const takes = this.alternatives(s, p);
    const current = this.current(p, takes);
    return {
      pieceId: p.id,
      current,
      takes: takes.slice(0, 60).map((t, i) => ({
        index: i,
        recordingId: t.recordingId,
        voiceId: t.voiceId,
        text: t.text,
        durationMs: t.endMs - t.startMs,
        title: this.library.recordings.find((r) => r.id === t.recordingId)?.title ?? '',
        altered: false,
        current: i === current,
      })),
      similar: [],
      splices: [],
    };
  }

  /** One change to a sentence (sentenceEdit); answers the sentence, with what the change found when it says. */
  edit(c: Command) {
    const s = this.find(c.sentenceId);
    const input = c.sentence ?? {};
    const ids = input.pieces ?? [];
    const one = () => this.pieceOf(s, ids[0]);
    let result: Record<string, unknown> | undefined;
    const fade = (ms: number | undefined) =>
      ms === undefined ? undefined : Math.round(Math.max(0, Math.min(2000, ms)) * 10) / 10;
    switch (input.do) {
      case 'move': {
        const p = one();
        if (input.offsetMs === undefined || input.offsetMs > MAX_TIMELINE_MS)
          throw Error('That is outside the timeline.');
        p.offsetMs = Math.round(input.offsetMs * 10) / 10;
        p.manual = true;
        s.pieces.sort((a, b) => a.offsetMs - b.offsetMs);
        break;
      }
      case 'edges': {
        const p = one();
        if (!p.source) throw Error('Only recorded pieces have edges.');
        const r = this.library.get(p.source.recordingId);
        const start = input.startMs ?? p.source.startMs,
          end = input.endMs ?? p.source.endMs;
        if (start < 0 || end <= start + 10 || end > r.durationMs + 50 || end - start > 30000)
          throw Error('Those edges are outside the recording.');
        p.source.startMs = start;
        p.source.endMs = end;
        p.manual = true;
        if (input.fadeInMs !== undefined) p.fadeInMs = fade(input.fadeInMs)!;
        if (input.fadeOutMs !== undefined) p.fadeOutMs = fade(input.fadeOutMs)!;
        break;
      }
      case 'fade':
        for (const id of ids) {
          const p = this.pieceOf(s, id);
          if (input.fadeInMs !== undefined) p.fadeInMs = fade(input.fadeInMs)!;
          if (input.fadeOutMs !== undefined) p.fadeOutMs = fade(input.fadeOutMs)!;
        }
        break;
      case 'gain':
        for (const id of ids) {
          const p = this.pieceOf(s, id);
          if (input.balanced) p.gain = null;
          else if (input.gain !== undefined) p.gain = Math.round(input.gain * 1000) / 1000;
          else throw Error('Choose a volume between 0 and 8 times.');
        }
        break;
      case 'delete':
        // Delete leaves an editable pause of the same length; deleting a pause removes it.
        for (const id of ids) {
          const p = this.pieceOf(s, id);
          if (p.kind === 'pause') {
            s.pieces = s.pieces.filter((x) => x !== p);
            continue;
          }
          const length = Math.min(60000, Math.max(10, DemoSentences.duration(p)));
          Object.assign(p, {
            kind: 'pause',
            durationMs: Math.round(length * 10) / 10,
            source: null,
            missing: null,
            namedId: null,
            effect: null,
            manual: true,
            chosen: false,
          });
        }
        break;
      case 'pause': {
        const p = one();
        if (p.kind !== 'pause' && p.kind !== 'missing')
          throw Error('Only pauses and missing words have a length of their own.');
        const length = input.durationMs ?? 0;
        if (length < 10 || length > 60000) throw Error('A pause lasts 10 ms to 60 s.');
        p.durationMs = Math.round(length * 10) / 10;
        if (!p.manual) this.layout(s);
        break;
      }
      case 'insertPause': {
        if (s.pieces.length >= MAX_PIECES)
          throw Error('This sentence has as many pieces as it can hold.');
        const p: DemoPiece = {
          ...this.blank(),
          kind: 'pause',
          tokenStart: null,
          tokenEnd: null,
          text: '',
          offsetMs: Math.max(0, Math.min(MAX_TIMELINE_MS, input.offsetMs ?? 0)),
          durationMs: Math.max(10, Math.min(60000, input.durationMs ?? 300)),
          manual: true,
        };
        s.pieces.push(p);
        result = { pieceId: p.id };
        this.layout(s);
        break;
      }
      case 'missing': {
        const p = one();
        if (p.kind !== 'missing') throw Error('Only a missing word plays as a gap or a bleep.');
        p.missing = input.mode === 'bleep' ? 'bleep' : 'gap';
        break;
      }
      case 'effects': {
        const e = input.effect;
        const effect =
          e && (e.reverse || e.echo || e.speed !== 1)
            ? {
                reverse: e.reverse,
                speed: Math.max(0.25, Math.min(4, e.speed)),
                keepPitch: e.keepPitch,
                echo: e.echo,
              }
            : null;
        if (!ids.length) s.settings.effect = effect;
        else
          for (const id of ids) {
            const p = this.pieceOf(s, id);
            p.effect = p.kind === 'pause' || p.kind === 'missing' ? null : copy(effect);
          }
        this.layout(s);
        break;
      }
      case 'settings': {
        let again = false;
        if (input.chaos !== undefined) {
          again ||= input.chaos !== s.settings.chaos;
          s.settings.chaos = input.chaos;
        }
        if (input.voiceIds) {
          const voices = [...new Set(input.voiceIds)];
          again ||= JSON.stringify(voices) !== JSON.stringify(s.settings.voiceIds);
          s.settings.voiceIds = voices;
        }
        if (input.altered !== undefined) s.settings.altered = input.altered;
        if (input.balance !== undefined) s.settings.balance = input.balance;
        if (input.cleanup !== undefined) s.settings.cleanup = input.cleanup;
        if (input.timing) s.settings.timing = input.timing;
        if (again) {
          // Choices a person made by hand stay.
          const decided = s.pieces.filter(
            (p) => p.tokenStart === null || p.manual || p.chosen || p.kind === 'pause',
          );
          const toks = tokens(s.text);
          const covered = new Set(
            decided.flatMap((p) => (p.tokenStart === null ? [] : range(p.tokenStart, p.tokenEnd!))),
          );
          const fresh: DemoPiece[] = [];
          let i = 0;
          while (i < toks.length) {
            if (covered.has(i)) {
              i++;
              continue;
            }
            let j = i;
            while (j < toks.length && !covered.has(j)) j++;
            fresh.push(...this.match(s.id, toks, i, j, s.settings));
            i = j;
          }
          s.pieces = [...decided, ...fresh];
        }
        this.layout(s);
        break;
      }
      case 'swap': {
        const p = one();
        const takes = this.alternatives(s, p);
        const now = this.current(p, takes);
        const index = input.alternative ?? (takes.length > 1 ? (now + 1) % takes.length : -1);
        const take = takes[index];
        if (!take || index === now) throw Error('There is no other take of that.');
        p.source = {
          recordingId: take.recordingId,
          startMs: take.startMs,
          endMs: take.endMs,
          voiceId: take.voiceId,
        };
        p.chosen = true;
        this.layout(s);
        break;
      }
      case 'resolve':
        throw Error(SENTENCE_UNAVAILABLE.resolve);
      case 'shuffle': {
        let changed = 0;
        for (const p of ids.length ? ids.map((id) => this.pieceOf(s, id)) : s.pieces) {
          const takes = this.alternatives(s, p);
          if (takes.length < 2) continue;
          const now = this.current(p, takes);
          const take =
            takes[(now + 1 + Math.floor(Math.random() * (takes.length - 1))) % takes.length];
          p.source = {
            recordingId: take.recordingId,
            startMs: take.startMs,
            endMs: take.endMs,
            voiceId: take.voiceId,
          };
          changed++;
        }
        this.layout(s);
        result = { changed };
        break;
      }
      case 'contrast':
        result = { changed: 0 };
        break;
      case 'rematch': {
        const before = s.pieces.filter((p) => p.kind === 'missing').length;
        this.rematch(s, s.text);
        result = { found: before - s.pieces.filter((p) => p.kind === 'missing').length };
        break;
      }
      case 'restore': {
        const v = s.versions.find((x) => x.n === input.version);
        if (!v) throw Error('That version is no longer there.');
        s.text = v.text;
        s.pieces = copy(v.pieces);
        s.settings = copy(v.settings);
        break;
      }
      case 'pin':
        if (!s.kept) throw Error('Keep the sentence before pinning it to the board.');
        s.pinned = input.pinned ?? !s.pinned;
        break;
      case 'insert': {
        if (input.caret !== undefined || !input.item) {
          result = this.insertAt(s, input);
          break;
        }
        if (!input.item) throw Error('Choose a palette item.');
        if (s.pieces.length >= MAX_PIECES)
          throw Error('This sentence has as many pieces as it can hold.');
        const p = this.fromItem(
          input.item,
          input.offsetMs ?? (s.pieces.length ? this.end(s.pieces) + FLUENT_GAP_MS : 0),
        );
        s.pieces.push(p);
        result = { pieceId: p.id };
        this.layout(s);
        break;
      }
      case 'title':
        s.title = (c.title ?? '').trim().slice(0, 200);
        break;
      default:
        throw Error('Unsupported sentence change.');
    }
    this.touch(s);
    const view = this.view(s);
    return result ? { sentence: view, result } : view;
  }

  /**
   * Add to sentence (2.45): a palette item (that exact take) or a phrase block's words (in its voice)
   * into the text at the caret, after the word or phrase the caret is in. Nothing plays.
   */
  private insertAt(s: DemoSentence, input: SentenceInput) {
    if (s.pieces.length >= MAX_PIECES)
      throw Error('This sentence has as many pieces as it can hold.');
    const label = input.item
      ? this.resolve(input.item).label
      : (input.text ?? '').trim().slice(0, 400);
    const count = tokens(label).length;
    if (!count) throw Error('There is nothing to add.');
    const ends = wordEnds(s.text);
    if (ends.length + count > 400) throw Error('A sentence has at most 400 words.');
    const caret = Math.max(0, Math.min(s.text.length, input.caret ?? s.text.length));
    let at = ends.filter((w) => w.start < caret).length;
    for (let moved = true; moved;) {
      moved = false;
      for (const p of s.pieces)
        if (p.tokenStart !== null && p.tokenEnd !== null && p.tokenStart < at && at < p.tokenEnd) {
          at = p.tokenEnd;
          moved = true;
        }
    }
    const place = at === 0 ? 0 : ends[at - 1].end + 1;
    const next =
      at === 0
        ? s.text.trim()
          ? `${label} ${s.text}`
          : label
        : `${s.text.slice(0, ends[at - 1].end)} ${label}${s.text.slice(ends[at - 1].end)}`;
    if (next.length > 4000) throw Error('A sentence is at most 4000 characters.');
    for (const p of s.pieces)
      if (p.tokenStart !== null && p.tokenEnd !== null && p.tokenStart >= at) {
        p.tokenStart += count;
        p.tokenEnd += count;
      }
    s.text = next;
    let added: DemoPiece[];
    if (input.item) added = [{ ...this.fromItem(input.item, 0, at, at + count), manual: false }];
    else {
      const voice = input.voiceIds?.[0] ?? null;
      const settings = voice ? { ...s.settings, voiceIds: [voice] } : s.settings;
      added = this.match(s.id, tokens(next), at, at + count, settings).map((p) => ({
        ...p,
        voiceLock: voice,
      }));
    }
    s.pieces.push(...added);
    this.layout(s);
    return { pieces: added.map((p) => p.id), pieceId: added[0]?.id, caret: place + label.length };
  }

  // ---- Keep, render ----------------------------------------------------------------------------------------

  keep(c: Command, byName: string) {
    const s = this.find(c.sentenceId);
    if (!s.pieces.length) throw Error('There is nothing to keep yet.');
    const n = (s.versions.at(-1)?.n ?? 0) + 1;
    const durationMs = Math.round(this.end(s.pieces));
    s.versions = [
      ...s.versions,
      {
        n,
        at: Date.now(),
        byName,
        durationMs,
        text: s.text,
        pieces: copy(s.pieces),
        settings: copy(s.settings),
      },
    ].slice(-50);
    s.kept = true;
    this.touch(s);
    return { version: n, durationMs, revision: s.revision };
  }

  /** The pieces to hear: a kept version, some pieces (moved to start at 0), a take to compare, or all of it. */
  piecesFor(c: Command) {
    const s = this.find(c.sentenceId);
    const input = c.sentence ?? {};
    if (input.version !== undefined) {
      const v = s.versions.find((x) => x.n === input.version);
      if (!v) throw Error('That version is no longer there.');
      return { pieces: v.pieces, settings: v.settings, sentence: s };
    }
    if (input.pieces?.length) {
      let pieces = input.pieces.map((id) => copy(this.pieceOf(s, id)));
      if (input.alternative !== undefined && pieces.length === 1) {
        const take = this.alternatives(s, this.pieceOf(s, input.pieces[0]))[input.alternative];
        if (!take) throw Error('That take is no longer in the voice library.');
        pieces[0].source = {
          recordingId: take.recordingId,
          startMs: take.startMs,
          endMs: take.endMs,
          voiceId: take.voiceId,
        };
      }
      const first = Math.min(...pieces.map((p) => p.offsetMs));
      pieces = pieces.map((p) => ({ ...p, offsetMs: p.offsetMs - first }));
      return { pieces, settings: s.settings, sentence: s };
    }
    if (input.timing) {
      const preview = copy(s);
      this.layout(preview, input.timing);
      return { pieces: preview.pieces, settings: s.settings, sentence: s };
    }
    return { pieces: s.pieces, settings: s.settings, sentence: s };
  }

  /**
   * Parts for the engine: each recorded piece from its example sound, a bleep tone for a bleeped missing word,
   * silence for gaps and pauses. A piece that starts while an earlier one still sounds cuts that one off.
   */
  render(pieces: DemoPiece[], settings: Settings): MixPart[] {
    const ordered = [...pieces].sort((a, b) => a.offsetMs - b.offsetMs);
    return ordered.flatMap((p, i): MixPart[] => {
      const next = ordered[i + 1]?.offsetMs ?? Infinity;
      const length = Math.min(DemoSentences.duration(p), next - p.offsetMs);
      if (length <= 0) return [];
      const effect = p.effect ?? settings.effect;
      if (p.kind === 'missing' && p.missing === 'bleep')
        return [{ tone: 1000, atMs: p.offsetMs, fromMs: 0, toMs: length, gain: 0.25 }];
      if (!p.source) return [];
      const r = this.library.recordings.find((x) => x.id === p.source!.recordingId && !x.removed);
      if (!r) return [];
      const speed = effect?.speed && effect.speed > 0 ? effect.speed : 1;
      return [
        {
          clip: r.soundId,
          atMs: p.offsetMs,
          fromMs: p.source.startMs,
          toMs: p.source.startMs + length * speed,
          gain: p.gain ?? 1,
          fadeInMs: p.fadeInMs,
          fadeOutMs: p.fadeOutMs,
          speed,
          reverse: !!effect?.reverse,
        },
      ];
    });
  }

  /** A palette reference checked against the library: a range of a recording, or a named piece. */
  resolve(item: PaletteRef) {
    if (item.namedId) {
      const n = this.named.find((x) => x.id === item.namedId);
      if (!n) throw Error('That named piece is no longer in the voice library.');
      const r = this.library.get(n.recordingId);
      return {
        recording: r,
        startMs: n.startMs,
        endMs: n.endMs,
        namedId: n.id,
        label: `[${n.name}]`,
      };
    }
    const r = this.library.get(item.recordingId ?? undefined);
    if (
      item.startMs < 0 ||
      item.endMs <= item.startMs ||
      item.endMs - item.startMs > 30000 ||
      item.startMs >= r.durationMs
    )
      throw Error('That part is outside the recording.');
    return {
      recording: r,
      startMs: item.startMs,
      endMs: item.endMs,
      namedId: null,
      label: item.text.trim().slice(0, 100) || r.title,
    };
  }

  private fromItem(
    item: PaletteRef,
    offsetMs: number,
    tokenStart: number | null = null,
    tokenEnd: number | null = null,
  ): DemoPiece {
    const { recording, startMs, endMs, namedId, label } = this.resolve(item);
    return {
      ...this.blank(),
      kind: namedId ? 'named' : 'word',
      tokenStart,
      tokenEnd,
      text: label,
      namedId,
      offsetMs: Math.max(0, Math.min(MAX_TIMELINE_MS, offsetMs)),
      manual: true,
      chosen: true,
      source: { recordingId: recording.id, startMs, endMs, voiceId: recording.voiceId },
    };
  }

  // ---- The speaking palette, performances and sets ----------------------------------------------------------

  private active() {
    return this.sets.find((s) => s.id === this.activeSet) ?? this.sets[0];
  }

  private favorite(ref: Omit<PaletteRef, 'text'>) {
    return this.active().favorites.some((f) =>
      ref.namedId
        ? f.namedId === ref.namedId
        : f.recordingId === ref.recordingId && f.startMs === ref.startMs && f.endMs === ref.endMs,
    );
  }

  palette(c: Command) {
    const set = this.active();
    const filter: Settings = {
      chaos: 0,
      timing: 'fluent',
      balance: false,
      cleanup: false,
      altered: false,
      voiceIds: set.voiceIds,
      effect: null,
    };
    const items: {
      kind: 'word' | 'phrase' | 'named';
      text: string;
      namedId: string | null;
      recordingId: string | null;
      startMs: number;
      endMs: number;
      voiceId: string | null;
      durationMs: number;
      favorite: boolean;
    }[] = [];
    for (const n of this.named) {
      const r = this.library.live().find((x) => x.id === n.recordingId);
      if (!r || !this.allowed(r.voiceId, filter)) continue;
      items.push({
        kind: 'named',
        text: `[${n.name}]`,
        namedId: n.id,
        recordingId: null,
        startMs: n.startMs,
        endMs: n.endMs,
        voiceId: r.voiceId,
        durationMs: n.endMs - n.startMs,
        favorite: this.favorite({ namedId: n.id, startMs: 0, endMs: 0 }),
      });
    }
    const wanted = (c.query ?? '').split(/\s+/).map(wordKey).filter(Boolean);
    if (wanted.length) {
      const phrases = [wanted];
      if (wanted.length === 1) {
        const longer = new Set<string>();
        for (const r of this.library.live())
          for (const w of r.words) {
            const key = wordKey(w.t);
            if (key.length > wanted[0].length && key.startsWith(wanted[0])) longer.add(key);
          }
        phrases.push(...[...longer].slice(0, 8).map((k) => [k]));
      }
      for (const phrase of phrases)
        for (const take of this.takes(phrase, filter).slice(0, 3))
          items.push({
            kind: phrase.length > 1 ? 'phrase' : 'word',
            text: take.text,
            namedId: null,
            recordingId: take.recordingId,
            startMs: take.startMs,
            endMs: take.endMs,
            voiceId: take.voiceId,
            durationMs: take.endMs - take.startMs,
            favorite: this.favorite(take),
          });
    } else
      for (const f of set.favorites.filter((x) => !x.namedId)) {
        const r = this.library.live().find((x) => x.id === f.recordingId);
        if (r)
          items.push({
            kind: 'word',
            text: f.text,
            namedId: null,
            recordingId: r.id,
            startMs: f.startMs,
            endMs: f.endMs,
            voiceId: r.voiceId,
            durationMs: f.endMs - f.startMs,
            favorite: true,
          });
      }
    return { items: items.slice(0, 200), activeSet: set.id };
  }

  /** A tap is noted in the performance being recorded (only the host's own; the example has one person). */
  tapped(item: PaletteRef) {
    const resolved = this.resolve(item);
    const p = this.performance;
    if (p && p.taps.length < 400)
      p.taps.push({
        at: Date.now() - p.started,
        item: {
          recordingId: resolved.namedId ? null : resolved.recording.id,
          namedId: resolved.namedId,
          startMs: resolved.startMs,
          endMs: resolved.endMs,
          text: resolved.label,
        },
      });
    return resolved;
  }

  mark(c: Command, byName: string) {
    if (c.action === 'remove') {
      const n = this.named.find((x) => x.id === c.sentence?.item?.namedId);
      if (!n) throw Error('That named piece is no longer there.');
      this.named = this.named.filter((x) => x !== n);
      this.changed();
      return { removed: true };
    }
    const name = (c.name ?? '')
      .trim()
      .replace(/^\[|\]$/g, '')
      .trim()
      .toLocaleLowerCase();
    if (!NAME.test(name)) throw Error('Name it with a word or two, like laugh, gasp or breath.');
    const r = this.library.get(c.recordingId);
    const start = c.sentence?.startMs ?? -1,
      end = c.sentence?.endMs ?? -1;
    if (start < 0 || end <= start + 20 || end - start > 30000 || end > r.durationMs + 50)
      throw Error('Mark a part of the recording (up to 30 s).');
    const named = { id: hex(16), name, recordingId: r.id, startMs: start, endMs: end, byName };
    this.named.push(named);
    this.changed();
    return { namedId: named.id, name };
  }

  perform(c: Command, ownerName: string) {
    switch (c.action) {
      case 'start':
        this.performance = { started: Date.now(), taps: [] };
        return { recording: true, taps: 0 };
      case 'cancel':
        this.performance = null;
        return { recording: false };
      case 'stop': {
        const p = this.performance;
        if (!p) throw Error('No performance is being recorded.');
        if (!p.taps.length) {
          this.performance = null;
          throw Error('Nothing was tapped while recording.');
        }
        const first = p.taps[0].at;
        const words: string[] = [];
        const pieces: DemoPiece[] = [];
        let token = 0;
        for (const { at, item } of p.taps) {
          const count = tokens(item.text).length;
          pieces.push(
            this.fromItem(item, at - first, count ? token : null, count ? token + count : null),
          );
          token += count;
          words.push(item.text);
        }
        const s: DemoSentence = {
          id: hex(16),
          title: `Performance ${new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}`,
          text: words.join(' ').slice(0, 4000),
          ownerName,
          origin: 'performance',
          revision: 1,
          kept: false,
          pinned: false,
          settings: {
            chaos: 0,
            timing: 'fluent',
            balance: false,
            cleanup: false,
            altered: false,
            voiceIds: [],
            effect: null,
          },
          pieces,
          versions: [],
          updated: Date.now(),
        };
        this.sentences.push(s);
        this.performance = null;
        this.changed();
        return { sentenceId: s.id, taps: p.taps.length };
      }
      default: {
        const p = this.performance;
        return p
          ? { recording: true, taps: p.taps.length, sinceMs: Date.now() - p.started }
          : { recording: false, taps: 0, sinceMs: 0 };
      }
    }
  }

  setsCommand(c: Command) {
    const input = c.set ?? {};
    const find = () => {
      const set = this.sets.find((s) => s.id === input.id);
      if (!set) throw Error('That performance set is no longer there.');
      return set;
    };
    const apply = (set: PerfSet) => {
      if (input.voiceIds) set.voiceIds = [...new Set(input.voiceIds)].slice(0, 256);
      if (input.favorites)
        set.favorites = input.favorites
          .slice(0, 200)
          .map((f) => ({ ...f, text: f.text.slice(0, 100) }));
      if (input.templates)
        set.templates = input.templates
          .filter((t) => t.text.trim())
          .slice(0, 100)
          .map((t) => ({
            id: /^[a-f0-9]{32}$/.test(t.id) ? t.id : hex(16),
            name: t.name.trim() || 'Template',
            text: t.text.slice(0, 400),
          }));
      if (input.blocks) {
        const names = new Set<string>();
        const next: PhraseBlock[] = [];
        for (const b of input.blocks) {
          if (!b.text.trim()) continue;
          const name = b.name.trim().slice(0, 40) || 'Phrase';
          if (names.has(name.toLocaleLowerCase()))
            throw Error(`Another phrase block is called “${name}”.`);
          names.add(name.toLocaleLowerCase());
          next.push({
            id: /^[a-f0-9]{32}$/.test(b.id) ? b.id : hex(16),
            name,
            text: b.text.trim().slice(0, 400),
            voiceId: b.voiceId ?? null,
          });
        }
        if (next.length > 100) throw Error('A set holds at most 100 phrase blocks.');
        set.blocks = next.length ? next : undefined;
      }
    };
    switch (c.action) {
      case 'create': {
        if (this.sets.length >= 32) throw Error('You have 32 performance sets.');
        const set: PerfSet = {
          id: hex(16),
          name: input.name?.trim().slice(0, 40) || 'New set',
          voiceIds: [],
          favorites: [],
          templates: [],
        };
        apply(set);
        this.sets.push(set);
        this.activeSet = set.id;
        this.changed();
        break;
      }
      case 'update': {
        const set = find();
        if (input.name?.trim()) set.name = input.name.trim().slice(0, 40);
        apply(set);
        this.changed();
        break;
      }
      case 'delete': {
        const set = find();
        if (set.id === 'default') throw Error('The default set stays.');
        this.sets = this.sets.filter((s) => s !== set);
        if (this.activeSet === set.id) this.activeSet = 'default';
        this.changed();
        break;
      }
      case 'activate':
        this.activeSet = find().id;
        this.changed();
        break;
    }
    return { active: this.activeSet, sets: copy(this.sets) };
  }
}

function range(a: number, b: number) {
  return Array.from({ length: Math.max(0, b - a) }, (_, i) => a + i);
}
