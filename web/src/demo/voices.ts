import type { Command } from '../protocol';
import { DEMO_SOUNDS } from './library';

/**
 * The example's voice library (Stage G, spec 11.3): three simulated voices made from the example's own
 * material (the two Apollo 11 clips and the audience laugh), with their words, tags, wanted words and a review
 * queue, all in memory and answered in the companion's shapes (`statusSchema`, `listSchema`, `getSchema`,
 * `searchSchema`, `vocabularySchema` in library/voices.ts). Changes can be undone, as on a real host. What needs
 * the host's PC (the Soundpad word index, adding kept clips, speaker groups) is refused with a reason.
 */

export const LIBRARY_UNAVAILABLE = {
  index: 'Not available in the example: the word index reads Soundpad categories on the host’s PC.',
  add: 'Not available in the example: adding a clip to the voice library keeps its file on the host’s PC.',
  speakers:
    'Not available in the example: grouping speakers listens to the host’s recording history on their PC.',
} as const;

export type LibraryWord = { s: number; e: number; t: string; u: boolean };
export type DemoRecording = {
  id: string;
  title: string;
  voiceId: string | null;
  tags: string[];
  favorite: boolean;
  correctedText: string | null;
  reviewed: boolean;
  durationMs: number;
  created: number;
  updated: number;
  byName: string;
  /** The example sound that holds its audio (also what plays it into the game). */
  soundId: string;
  words: LibraryWord[];
  removed: boolean;
};
type Voice = { id: string; name: string; color: string };
type Wanted = { id: string; voiceId: string; text: string; byName: string };
type Audit = {
  id: number;
  group: number;
  at: number;
  byName: string;
  target: 'recording' | 'voice' | 'wanted';
  targetId: string;
  field: string;
  before: string | null;
  after: string | null;
  undoOf: number | null;
};

const id32 = () =>
  Array.from(crypto.getRandomValues(new Uint8Array(16)), (b) =>
    b.toString(16).padStart(2, '0'),
  ).join('');
/** A word as the library indexes it: lower case, without punctuation around it. */
export const wordKey = (text: string) =>
  text.toLocaleLowerCase().replace(/^[^\p{L}\p{N}]+|[^\p{L}\p{N}]+$/gu, '');

/** The example's voices, in the colours of the voice tokens (--voice-1 …). */
const VOICES: { name: string; color: string; sound: string; title: string; tags: string[] }[] = [
  {
    name: 'Neil Armstrong (simulated)',
    color: '#f2a65a',
    sound: 'apollo11-small-step',
    title: 'One small step (Apollo 11)',
    tags: ['apollo', 'famous'],
  },
  {
    name: 'Tranquility Base (simulated)',
    color: '#7fd1ae',
    sound: 'apollo11-eagle-landed',
    title: 'The Eagle has landed (Apollo 11)',
    tags: ['apollo'],
  },
  {
    name: 'Studio Audience (simulated)',
    color: '#c792ea',
    sound: 'audience-laugh',
    title: 'Audience laugh',
    tags: ['nonverbal'],
  },
];
/** The audience laugh's named piece, "[laugh]" in a sentence. */
export const LAUGH = { name: 'laugh', startMs: 300, endMs: 2300 };

export class DemoVoices {
  /** Rises with every change: the snapshot's `voiceLibrary` (pages fetch libraryStatus when it moves). */
  revision = 1;
  voices: Voice[] = [];
  recordings: DemoRecording[] = [];
  private wanted: Wanted[] = [];
  private audit: Audit[] = [];
  private nextAudit = 1;
  private nextGroup = 1;
  guestLibraryEdit = true;

  /** `soundId(key)` names the example sound for a key; `duration` its decoded length (0: not loaded). */
  constructor(
    soundId: (key: string) => string,
    duration: (soundId: string) => number,
    now: number,
  ) {
    VOICES.forEach((v, i) => {
      const sound = soundId(v.sound);
      if (!duration(sound)) return;
      const voice = { id: id32(), name: v.name, color: v.color };
      this.voices.push(voice);
      this.recordings.push({
        id: id32(),
        title: v.title,
        voiceId: voice.id,
        tags: v.tags,
        favorite: i === 0,
        correctedText: null,
        // The laugh waits in the review queue, as a new recording would.
        reviewed: i < 2,
        durationMs: Math.round(duration(sound)),
        created: now - (VOICES.length - i) * 60000,
        updated: now - (VOICES.length - i) * 60000,
        byName: 'Example',
        soundId: sound,
        words: (DEMO_SOUNDS.find((s) => s.key === v.sound)?.words ?? []).map((w) => ({
          s: w.startMs,
          e: w.endMs,
          t: w.w,
          u: false,
        })),
        removed: false,
      });
    });
  }

  /** The recordings that are in the library (not removed). */
  live() {
    return this.recordings.filter((r) => !r.removed);
  }

  get(id: string | undefined) {
    const r = this.live().find((x) => x.id === id);
    if (!r) throw Error('That recording is no longer in the voice library.');
    return r;
  }

  private changed() {
    this.revision++;
  }

  private text(r: DemoRecording) {
    return (r.correctedText ?? r.words.map((w) => w.t).join(' ')).slice(0, 400);
  }

  private view(r: DemoRecording) {
    return {
      id: r.id,
      title: r.title,
      voiceId: r.voiceId,
      tags: r.tags,
      favorite: r.favorite,
      correctedText: r.correctedText,
      reviewed: r.reviewed,
      wordsState: 'ready' as const,
      wordsError: null,
      durationMs: r.durationMs,
      created: r.created,
      updated: r.updated,
      source: 'example',
      sessionId: null,
      byName: r.byName,
      soundId: r.soundId,
      text: this.text(r),
      wordCount: r.words.length,
    };
  }

  private has(voiceId: string, text: string) {
    const key = text.split(/\s+/).map(wordKey).filter(Boolean).join(' ');
    return this.live().filter(
      (r) =>
        r.voiceId === voiceId &&
        ` ${r.words.map((w) => wordKey(w.t)).join(' ')} `.includes(` ${key} `),
    ).length;
  }

  status(canEdit: boolean) {
    const live = this.live();
    return {
      revision: this.revision,
      blocked: false,
      problem: null,
      count: live.length,
      unreviewed: live.filter((r) => !r.reviewed).length,
      pending: 0,
      voices: this.voices.map((v) => ({
        ...v,
        count: live.filter((r) => r.voiceId === v.id).length,
      })),
      unassigned: live.filter((r) => !r.voiceId).length,
      tags: [...new Set(live.flatMap((r) => r.tags))].sort(),
      wanted: this.wanted.map((w) => ({
        ...w,
        available: this.has(w.voiceId, w.text) > 0,
        count: this.has(w.voiceId, w.text),
      })),
      canEdit,
      settings: {
        guestLibraryEdit: this.guestLibraryEdit,
        groupSpeakers: false,
        indexCategories: null,
        indexPaused: false,
      },
      index: { total: 0, indexed: 0, failed: 0, paused: false, current: null, active: false },
      worker: 'Example library: answered in this browser.',
      notices: [],
    };
  }

  private filtered(c: Command) {
    const query = (c.query ?? '').trim().toLocaleLowerCase();
    return this.live()
      .filter((r) => (c.unassigned ? !r.voiceId : !c.voiceId || r.voiceId === c.voiceId))
      .filter((r) => !c.tag || r.tags.includes(c.tag))
      .filter((r) => !c.favorite || r.favorite)
      .filter((r) => c.reviewed === undefined || r.reviewed === c.reviewed)
      .filter((r) => !query || `${r.title} ${this.text(r)}`.toLocaleLowerCase().includes(query))
      .sort((a, b) => b.created - a.created);
  }

  list(c: Command) {
    const all = this.filtered({ ...c, query: undefined });
    const offset = c.offset ?? 0;
    return {
      total: all.length,
      offset,
      items: all.slice(offset, offset + (c.limit ?? 50)).map((r) => this.view(r)),
    };
  }

  getView(c: Command) {
    const r = this.get(c.recordingId);
    return {
      recording: this.view(r),
      words: r.words,
      audit: this.audit.filter((a) => a.targetId === r.id).slice(-500),
    };
  }

  /** Word search: recordings with the words in that order, each with the words around the first match. */
  search(c: Command) {
    const wanted = (c.query ?? '').split(/\s+/).map(wordKey).filter(Boolean);
    const results = wanted.length
      ? this.filtered({ ...c, query: undefined }).flatMap((r) => {
          const keys = r.words.map((w) => wordKey(w.t));
          const hits: number[] = [];
          for (let i = 0; i + wanted.length <= keys.length; i++)
            if (wanted.every((w, k) => keys[i + k].startsWith(w))) hits.push(i);
          if (!hits.length) return [];
          const first = hits[0];
          const from = Math.max(0, first - 8);
          const context = r.words.slice(from, from + 24).map((w, k) => ({
            i: from + k,
            s: w.s,
            e: w.e,
            t: w.t,
            hit: from + k >= first && from + k < first + wanted.length,
            u: w.u,
          }));
          return [
            {
              type: 'clip' as const,
              id: r.id,
              soundId: r.soundId,
              title: r.title,
              voiceId: r.voiceId,
              durationMs: r.durationMs,
              matches: hits.length,
              match: first,
              length: wanted.length,
              context,
            },
          ];
        })
      : [];
    const offset = c.offset ?? 0;
    return {
      total: results.length,
      offset,
      results: results.slice(offset, offset + (c.limit ?? 100)),
      sentences: [],
    };
  }

  vocabulary(c: Command) {
    const prefix = wordKey(c.query ?? '');
    const counts = new Map<string, number>();
    for (const r of this.live())
      if (!c.voiceId || r.voiceId === c.voiceId)
        for (const w of r.words) {
          const key = wordKey(w.t);
          if (key && key.startsWith(prefix)) counts.set(key, (counts.get(key) ?? 0) + 1);
        }
    const words = [...counts]
      .map(([text, count]) => ({ text, count }))
      .sort((a, b) => b.count - a.count || a.text.localeCompare(b.text));
    const offset = c.offset ?? 0;
    return {
      total: words.length,
      offset,
      words: words.slice(offset, offset + (c.limit ?? 100)),
      wanted: this.wanted
        .filter((w) => !c.voiceId || w.voiceId === c.voiceId)
        .map((w) => ({
          id: w.id,
          text: w.text,
          available: this.has(w.voiceId, w.text) > 0,
          count: this.has(w.voiceId, w.text),
        })),
    };
  }

  auditOf(c: Command) {
    return {
      entries: this.audit.filter((a) => !c.recordingId || a.targetId === c.recordingId).slice(-500),
    };
  }

  /** Records one group of changes; returns its number (0 when nothing changed). */
  private record(
    byName: string,
    changes: Omit<Audit, 'id' | 'group' | 'at' | 'byName' | 'undoOf'>[],
    undoOf: number | null = null,
  ) {
    if (!changes.length) return 0;
    const group = this.nextGroup++;
    for (const change of changes)
      this.audit.push({
        ...change,
        id: this.nextAudit++,
        group,
        at: Date.now(),
        byName,
        undoOf,
      });
    if (this.audit.length > 2000) this.audit = this.audit.slice(-2000);
    this.changed();
    return group;
  }

  edit(c: Command, byName: string) {
    const ids = c.recordingIds ?? (c.recordingId ? [c.recordingId] : []);
    if (!ids.length) throw Error('Choose a recording.');
    const changes: Omit<Audit, 'id' | 'group' | 'at' | 'byName' | 'undoOf'>[] = [];
    for (const id of ids) {
      const r = this.get(id);
      const set = (field: string, before: unknown, after: unknown, apply: () => void) => {
        if (JSON.stringify(before) === JSON.stringify(after)) return;
        changes.push({
          target: 'recording',
          targetId: r.id,
          field,
          before: JSON.stringify(before),
          after: JSON.stringify(after),
        });
        apply();
      };
      if (c.title !== undefined) {
        const title = c.title.trim().slice(0, 200);
        set('title', r.title, title, () => (r.title = title));
      }
      if (c.voiceId !== undefined) {
        const voiceId = c.voiceId || null;
        if (voiceId && !this.voices.some((v) => v.id === voiceId)) throw Error('Choose a voice.');
        set('voiceId', r.voiceId, voiceId, () => (r.voiceId = voiceId));
      }
      if (c.favorite !== undefined)
        set('favorite', r.favorite, c.favorite, () => (r.favorite = !!c.favorite));
      if (c.reviewed !== undefined)
        set('reviewed', r.reviewed, c.reviewed, () => (r.reviewed = !!c.reviewed));
      if (c.correctedText !== undefined) {
        const text = c.correctedText.trim().slice(0, 4000) || null;
        set('correctedText', r.correctedText, text, () => (r.correctedText = text));
      }
      const tags = c.tags
        ? [...new Set(c.tags)]
        : c.addTags || c.removeTags
          ? [
              ...new Set([
                ...r.tags.filter((t) => !c.removeTags?.includes(t)),
                ...(c.addTags ?? []),
              ]),
            ]
          : null;
      if (tags) set('tags', r.tags, tags.slice(0, 32), () => (r.tags = tags.slice(0, 32)));
      if (changes.length) r.updated = Date.now();
    }
    return { group: this.record(byName, changes) };
  }

  remove(c: Command, byName: string) {
    const ids = c.recordingIds ?? (c.recordingId ? [c.recordingId] : []);
    const changes = ids.map((id) => {
      const r = this.get(id);
      r.removed = true;
      return {
        target: 'recording' as const,
        targetId: r.id,
        field: 'removed',
        before: 'false',
        after: 'true',
      };
    });
    return { group: this.record(byName, changes) };
  }

  /** Puts a group of changes back, newest first; the undo is itself a group (and can be undone). */
  undo(c: Command, byName: string) {
    const entries = this.audit.filter((a) => a.group === c.auditId).reverse();
    if (!entries.length) throw Error('That change is no longer in the history.');
    const back: Omit<Audit, 'id' | 'group' | 'at' | 'byName' | 'undoOf'>[] = [];
    for (const a of entries) {
      const value = a.before === null ? null : JSON.parse(a.before);
      if (a.target === 'recording') {
        const r = this.recordings.find((x) => x.id === a.targetId);
        if (!r) continue;
        if (a.field === 'removed') r.removed = value === true;
        else (r as Record<string, unknown>)[a.field] = value;
      } else if (a.target === 'voice') {
        if (a.field === 'voice') {
          this.voices = this.voices.filter((v) => v.id !== a.targetId);
          if (value) this.voices.push(value as Voice);
        } else {
          const v = this.voices.find((x) => x.id === a.targetId);
          if (v) (v as Record<string, unknown>)[a.field] = value;
        }
      } else if (a.target === 'wanted') {
        this.wanted = this.wanted.filter((w) => w.id !== a.targetId);
        if (value) this.wanted.push(value as Wanted);
      }
      back.push({
        target: a.target,
        targetId: a.targetId,
        field: a.field,
        before: a.after,
        after: a.before,
      });
    }
    return { group: this.record(byName, back, c.auditId ?? null) };
  }

  voice(c: Command, byName: string) {
    if (c.action === 'create') {
      const name = (c.name ?? c.title ?? '').trim().slice(0, 40);
      if (!name) throw Error('Name the voice.');
      if (this.voices.length >= 256) throw Error('That is as many voices as the library holds.');
      const voice = { id: id32(), name, color: c.color ?? '#5ad1e6' };
      this.voices.push(voice);
      const group = this.record(byName, [
        {
          target: 'voice',
          targetId: voice.id,
          field: 'voice',
          before: null,
          after: JSON.stringify(voice),
        },
      ]);
      return { group, voiceId: voice.id };
    }
    const voice = this.voices.find((v) => v.id === c.voiceId);
    if (!voice) throw Error('That voice is no longer in the library.');
    if (c.action === 'delete') {
      const changes: Omit<Audit, 'id' | 'group' | 'at' | 'byName' | 'undoOf'>[] = [
        {
          target: 'voice',
          targetId: voice.id,
          field: 'voice',
          before: JSON.stringify(voice),
          after: null,
        },
      ];
      for (const r of this.recordings.filter((x) => x.voiceId === voice.id)) {
        changes.push({
          target: 'recording',
          targetId: r.id,
          field: 'voiceId',
          before: JSON.stringify(voice.id),
          after: 'null',
        });
        r.voiceId = null;
      }
      this.voices = this.voices.filter((v) => v !== voice);
      return { group: this.record(byName, changes), voiceId: voice.id };
    }
    const changes: Omit<Audit, 'id' | 'group' | 'at' | 'byName' | 'undoOf'>[] = [];
    const name = c.name?.trim().slice(0, 40);
    if (name && name !== voice.name) {
      changes.push({
        target: 'voice',
        targetId: voice.id,
        field: 'name',
        before: JSON.stringify(voice.name),
        after: JSON.stringify(name),
      });
      voice.name = name;
    }
    if (c.color && c.color !== voice.color) {
      changes.push({
        target: 'voice',
        targetId: voice.id,
        field: 'color',
        before: JSON.stringify(voice.color),
        after: JSON.stringify(c.color),
      });
      voice.color = c.color;
    }
    return { group: this.record(byName, changes), voiceId: voice.id };
  }

  want(c: Command, byName: string) {
    if (c.action === 'add') {
      const text = (c.query ?? c.text ?? '').trim().slice(0, 100);
      if (!text) throw Error('Type the word you want.');
      if (!this.voices.some((v) => v.id === c.voiceId)) throw Error('Choose a voice.');
      const wanted = { id: id32(), voiceId: c.voiceId!, text, byName };
      this.wanted.push(wanted);
      return {
        group: this.record(byName, [
          {
            target: 'wanted',
            targetId: wanted.id,
            field: 'wanted',
            before: null,
            after: JSON.stringify(wanted),
          },
        ]),
      };
    }
    const wanted = this.wanted.find((w) => w.id === (c.name ?? c.recordingId));
    if (!wanted) throw Error('That word is not wanted any more.');
    this.wanted = this.wanted.filter((w) => w !== wanted);
    return {
      group: this.record(byName, [
        {
          target: 'wanted',
          targetId: wanted.id,
          field: 'wanted',
          before: JSON.stringify(wanted),
          after: null,
        },
      ]),
    };
  }

  settings(c: Command) {
    if (c.groupSpeakers === true) throw Error(LIBRARY_UNAVAILABLE.speakers);
    if (c.guestLibraryEdit !== undefined) this.guestLibraryEdit = c.guestLibraryEdit;
    this.changed();
  }
}
