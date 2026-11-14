import { DEMO_SOUNDS, MIMIC_LOOP_MS, MIMIC_PLACEMENTS, wordLines } from './library';

/**
 * The example's Mimic recording history (Stage G, spec 11.1): what a 2.39 companion keeps of one output,
 * answered from the example's 20-second loop of Apollo 11 clips. It is virtual: Mimic "records" at 48 kHz
 * while listening, frame f holds the loop's sample at f mod the loop's length, and so nothing is buffered
 * however long the example runs. Only the part since listening first started exists, and only the newest
 * `seconds` of it (600 by default) can be seen. Pure apart from the clock, which every call takes.
 *
 * The answers have the companion's shapes (`timelineSchema`, `levelsSchema`, `transcriptSchema` and
 * `historySearchSchema` in the website), so the real Mimic window shows them unchanged.
 */

/** The history's frame rate, as the companion's capture. */
export const HISTORY_RATE = 48000;
/** Words show in the transcript this long after they are said, as with live recognition. */
export const HEARD_MS = 1500;
export const HISTORY_SECONDS = 600;
export const MAX_CLIP_MS = 30000;
export const MIN_CLIP_MS = 150;
export const LOOP_FRAMES = (MIMIC_LOOP_MS * HISTORY_RATE) / 1000;

export const msToFrames = (ms: number) => Math.round((ms * HISTORY_RATE) / 1000);
/** Milliseconds on the endless loop for a history frame (the engine's loop helpers take these). */
export const loopMs = (frame: number) => (frame * 1000) / HISTORY_RATE;

export type Range = { startFrame: number; endFrame: number };
type Run = {
  startFrame: number;
  wallStart: number;
  endFrame: number | null;
  wallEnd: number | null;
};

/** One word of the loop, in loop milliseconds, with its line (phrase) and its place in the loop. */
type LoopWord = { index: number; text: string; startMs: number; endMs: number; line: number };

/** The loop's words in order, each with the transcript line it belongs to. */
export const LOOP_WORDS: LoopWord[] = (() => {
  const words: LoopWord[] = [];
  let line = 0;
  for (const { key, atMs } of MIMIC_PLACEMENTS) {
    const said = DEMO_SOUNDS.find((s) => s.key === key)?.words ?? [];
    for (const l of wordLines(said, atMs)) {
      for (const w of said)
        if (w.startMs + atMs >= l.startMs && w.endMs + atMs <= l.endMs)
          words.push({
            index: words.length,
            text: w.w,
            startMs: w.startMs + atMs,
            endMs: w.endMs + atMs,
            line,
          });
      line++;
    }
  }
  return words;
})();
const LINES = LOOP_WORDS.reduce((n, w) => Math.max(n, w.line + 1), 0);
/** How many words each line of the loop has: a phrase is finished once all of them were heard. */
const LINE_WORDS = Array.from(
  { length: LINES },
  (_, l) => LOOP_WORDS.filter((w) => w.line === l).length,
);

export type HistoryWord = {
  id: number;
  start: number;
  end: number;
  text: string;
  probability: number;
  stretchMs: number;
  pauseBefore: boolean;
  pauseAfter: boolean;
  status: 'done';
  phraseId: number;
  revision: number;
  corrected: boolean;
  speaker: null;
};

const hex = (bytes: number) =>
  Array.from(crypto.getRandomValues(new Uint8Array(bytes)), (b) =>
    b.toString(16).padStart(2, '0'),
  ).join('');
const normal = (text: string) => text.toLocaleLowerCase().replace(/[^\p{L}\p{N}']+/gu, '');

export class DemoHistory {
  sessionId: string | null = null;
  seconds = HISTORY_SECONDS;
  subtitles: 'gaming' | 'balanced' | 'fast' = 'gaming';
  private runs: Run[] = [];
  private corrections = new Map<number, string>();

  get recording() {
    return this.runs.at(-1)?.endFrame === null;
  }

  /** Listening starts: the first time it starts the history; later runs follow a "Stopped" gap. */
  start(now: number) {
    if (this.recording) return;
    this.sessionId ??= hex(16);
    this.runs.push({ startFrame: this.live(now), wallStart: now, endFrame: null, wallEnd: null });
  }

  stop(now: number) {
    const run = this.runs.at(-1);
    if (!run || run.endFrame !== null) return;
    run.endFrame = this.live(now);
    run.wallEnd = now;
  }

  /** The newest frame: everything recorded so far (gaps take no frames). */
  live(now: number) {
    const run = this.runs.at(-1);
    if (!run) return 0;
    return run.endFrame ?? run.startFrame + msToFrames(Math.max(0, now - run.wallStart));
  }

  /** The oldest frame that can still be seen: the window is the newest `seconds`. */
  oldest(now: number) {
    return Math.max(0, this.live(now) - this.seconds * HISTORY_RATE);
  }

  /** The parts recorded without a break (one per listening run), clipped to what can be seen. */
  private parts(now: number) {
    const oldest = this.oldest(now),
      live = this.live(now);
    return this.runs
      .map((r) => ({
        run: r,
        startFrame: Math.max(oldest, r.startFrame),
        endFrame: r.endFrame ?? live,
      }))
      .filter((p) => p.endFrame > p.startFrame);
  }

  /** True when the whole range can be seen and was recorded without a break. */
  holds(range: Range, now: number) {
    return this.parts(now).some(
      (p) => range.startFrame >= p.startFrame && range.endFrame <= p.endFrame,
    );
  }

  /** Checks a selection like the companion: its recording, what can be seen, and its length. */
  check(range: Range, sourceId: string | undefined, now: number) {
    if (!this.sessionId) throw Error('Mimic has not recorded anything yet.');
    if (sourceId && sourceId !== this.sessionId)
      throw Error('That selection is from another recording. Select the moment again.');
    const ms = loopMs(range.endFrame - range.startFrame);
    if (ms < MIN_CLIP_MS) throw Error(`Choose at least ${MIN_CLIP_MS} ms.`);
    if (ms > MAX_CLIP_MS + 1) throw Error(`Choose at most ${MAX_CLIP_MS / 1000} seconds.`);
    if (!this.holds(range, now))
      throw Error('That part is no longer in the recording history, or runs into a gap.');
    return range;
  }

  timeline(now: number, transcribing: boolean) {
    const live = this.live(now);
    const oldest = this.oldest(now);
    const last = this.runs.at(-1);
    return {
      sessionId: this.sessionId,
      rate: HISTORY_RATE,
      liveFrame: live,
      liveWallUtcMs: !last ? null : this.recording ? now : last.wallEnd,
      nowUtcMs: now,
      recording: this.recording,
      windowSeconds: this.seconds,
      available: live > oldest ? [{ startFrame: oldest, endFrame: live }] : [],
      spans: this.parts(now).map((p) => ({
        startFrame: p.startFrame,
        endFrame: p.endFrame,
        wallStartUtcMs: p.run.wallStart + loopMs(p.startFrame - p.run.startFrame),
      })),
      gaps: this.runs.slice(1).flatMap((r, i) =>
        r.startFrame >= oldest
          ? [
              {
                atFrame: r.startFrame,
                wallFromUtcMs: this.runs[i].wallEnd ?? r.wallStart,
                wallToUtcMs: r.wallStart,
                reason: 'stopped',
              },
            ]
          : [],
      ),
      fromFrame: null,
      shared: [] as Range[],
      transcribing,
    };
  }

  /**
   * Waveform levels for a range: `bins` (min, max) pairs in ±32767, from the loop's block peaks (`peaks`
   * gives 0 to 1000 per bin for loop milliseconds). Bins outside what can be seen are silent.
   */
  levels(
    range: Range,
    bins: number,
    now: number,
    peaks: (fromMs: number, toMs: number, count: number) => number[],
  ) {
    const count = Math.max(1, Math.min(2048, Math.round(bins)));
    const start = Math.max(0, Math.round(range.startFrame)),
      end = Math.max(start, Math.round(range.endFrame));
    const values =
      end > start ? peaks(loopMs(start), loopMs(end), count) : new Array(count).fill(0);
    const parts = this.parts(now);
    const levels: number[] = [];
    for (let i = 0; i < count; i++) {
      const mid = start + ((i + 0.5) * (end - start)) / count;
      const seen = parts.some((p) => mid >= p.startFrame && mid < p.endFrame);
      const v = seen ? Math.round(((values[i] ?? 0) * 32767) / 1000) : 0;
      levels.push(-v, v);
    }
    return { start, end, bins: count, levels };
  }

  /** Every word heard in what can be seen (HEARD_MS after it was said), oldest first. */
  heard(now: number): HistoryWord[] {
    if (!this.sessionId) return [];
    const live = this.live(now);
    const parts = this.parts(now);
    if (!parts.length) return [];
    const first = Math.floor(parts[0].startFrame / LOOP_FRAMES);
    const words: HistoryWord[] = [];
    for (let rep = first; rep * LOOP_FRAMES <= live; rep++)
      for (const w of LOOP_WORDS) {
        const start = rep * LOOP_FRAMES + msToFrames(w.startMs),
          end = rep * LOOP_FRAMES + msToFrames(w.endMs);
        if (end + msToFrames(HEARD_MS) > live) continue;
        if (!parts.some((p) => start >= p.startFrame && end <= p.endFrame)) continue;
        const id = rep * 1000 + w.index + 1;
        const corrected = this.corrections.get(id);
        words.push({
          id,
          start,
          end,
          text: corrected ?? w.text,
          probability: 0.95,
          stretchMs: 0,
          pauseBefore: false,
          pauseAfter: false,
          status: 'done',
          phraseId: rep * LINES + w.line,
          revision: corrected === undefined ? 0 : 1,
          corrected: corrected !== undefined,
          speaker: null,
        });
      }
    return words;
  }

  /** The transcript of a range: the hand-placed words of every repetition of the loop in it. */
  transcript(range: Range, now: number, transcribing: boolean) {
    const words = transcribing
      ? this.heard(now).filter((w) => w.end > range.startFrame && w.start < range.endFrame)
      : [];
    return { words, pending: [] as Range[], transcribing };
  }

  /** The words a range holds, as a name (the companion names drafts and clips this way). */
  wordsIn(range: Range, now: number) {
    return this.heard(now)
      .filter(
        (w) => (w.start + w.end) / 2 >= range.startFrame && (w.start + w.end) / 2 <= range.endFrame,
      )
      .map((w) => w.text)
      .join(' ')
      .slice(0, 100);
  }

  /** The most recent finished phrase (a line of the transcript) that can be seen, with a little air. */
  latestPhrase(now: number): Range | null {
    return this.phrases(now).at(-1) ?? null;
  }

  /** Finished phrases (every word heard) that can be seen, oldest first. */
  phrases(now: number): Range[] {
    const byPhrase = new Map<number, HistoryWord[]>();
    for (const w of this.heard(now))
      byPhrase.set(w.phraseId, [...(byPhrase.get(w.phraseId) ?? []), w]);
    return [...byPhrase.entries()].flatMap(([phrase, ws]) => {
      if (ws.length < LINE_WORDS[phrase % LINES]) return [];
      const r = this.padded(ws[0].start, ws.at(-1)!.end, now);
      return r ? [r] : [];
    });
  }

  private padded(start: number, end: number, now: number): Range | null {
    const part = this.parts(now).find((p) => start >= p.startFrame && end <= p.endFrame);
    if (!part) return null;
    return {
      startFrame: Math.max(part.startFrame, start - msToFrames(150)),
      endFrame: Math.min(part.endFrame, end + msToFrames(150)),
    };
  }

  /** The last `seconds` (Quick Mimic's "Last 10 seconds"), within the current part. */
  recent(seconds: number, now: number): Range | null {
    const part = this.parts(now).at(-1);
    if (!part) return null;
    const startFrame = Math.max(part.startFrame, part.endFrame - seconds * HISTORY_RATE);
    return part.endFrame - startFrame >= msToFrames(MIN_CLIP_MS)
      ? { startFrame, endFrame: part.endFrame }
      : null;
  }

  /** Word search in what can be seen: consecutive words that match the query, newest first. */
  search(query: string, now: number) {
    const wanted = query.split(/\s+/).map(normal).filter(Boolean);
    const words = this.heard(now);
    const matches: { startFrame: number; endFrame: number; text: string; context: string }[] = [];
    if (wanted.length)
      for (let i = words.length - wanted.length; i >= 0 && matches.length < 50; i--) {
        const run = words.slice(i, i + wanted.length);
        if (!run.every((w, k) => normal(w.text).includes(wanted[k]))) continue;
        const phrase = words.filter((w) => w.phraseId === run[0].phraseId);
        matches.push({
          startFrame: Math.max(0, run[0].start - msToFrames(20)),
          endFrame: run.at(-1)!.end + msToFrames(10),
          text: run.map((w) => w.text).join(' '),
          context: phrase.map((w) => w.text).join(' '),
        });
      }
    return { sessionId: this.sessionId ?? '', matches };
  }

  /** A transcript correction: the word's text only, kept in memory. */
  correct(wordId: number, text: string, now: number) {
    if (!this.heard(now).some((w) => w.id === wordId))
      throw Error('That word is no longer in the transcript.');
    const clean = text.trim().slice(0, 100);
    if (!clean) throw Error('Type the word.');
    this.corrections.set(wordId, clean);
  }

  settings(seconds: number | undefined, subtitles: DemoHistory['subtitles'] | undefined) {
    if (seconds !== undefined) this.seconds = Math.max(60, Math.min(3600, Math.round(seconds)));
    if (subtitles) this.subtitles = subtitles;
  }

  /**
   * Discard (the P1 rule): only history a crash recovered goes, never what this run recorded. The example
   * never crashes, so nothing is recovered and nothing goes.
   */
  discard() {
    return { discarded: 0 };
  }

  snapshot() {
    return {
      sessionId: this.sessionId,
      seconds: this.seconds,
      subtitles: this.subtitles,
      sourceLost: false,
      recoveredMs: 0,
    };
  }
}
