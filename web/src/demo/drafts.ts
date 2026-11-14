import type { DemoHistory, Range } from './history';

/**
 * The example's drafts (Stage G, spec 11.2): the host's editable selections on the virtual recording
 * history, and the selection of the simulated guest Alex, which appears a few seconds after the host's
 * first draft. The answers have the companion's shapes (`draftSchema`, `draftListSchema`). Audio is made
 * from the loop only when a draft is kept or played (see `DemoModel`): the loop is endless and every frame
 * maps onto it, so a draft's audio stays playable after its part of the history has rolled out of view.
 */

export type DemoDraft = {
  id: string;
  startFrame: number;
  endFrame: number;
  name: string | null;
  createdUtcMs: number;
  updatedUtcMs: number;
  sessionId: string;
  inTemp: boolean;
};
export type DemoBand = {
  id: string;
  ownerName: string;
  tag: string;
  startFrame: number;
  endFrame: number;
  name: string | null;
};

/** The simulated guest's selection appears this long after the host's first draft. */
export const ALEX_BAND_MS = 8000;
export const ALEX_BAND_OWNER = 'Alex (simulated)';
const MAX_DRAFTS = 200;

const id32 = () =>
  Array.from(crypto.getRandomValues(new Uint8Array(16)), (b) =>
    b.toString(16).padStart(2, '0'),
  ).join('');

export class DemoDrafts {
  /** Rises with every change; the snapshot's `drafts` (pages fetch draftList when it moves). */
  revision = 0;
  private drafts: DemoDraft[] = [];
  private band: DemoBand | null = null;
  private bandPlanned = false;

  constructor(private history: DemoHistory) {}

  private changed() {
    this.revision++;
  }

  list() {
    const sessionId = this.history.sessionId;
    if (!sessionId) throw Error('Mimic has not recorded anything yet.');
    return {
      drafts: [...this.drafts]
        .filter((d) => d.sessionId === sessionId)
        .sort((a, b) => b.updatedUtcMs - a.updatedUtcMs),
      others: this.band ? [this.band] : [],
      sessionId,
    };
  }

  find(id: string | undefined) {
    const draft = this.drafts.find((d) => d.id === id);
    if (!draft) throw Error('That draft is no longer there.');
    return draft;
  }

  /**
   * A new draft: the latest finished phrase, the last ten seconds, or a selection. Returns the draft and
   * whether it is the first one (the simulated guest's selection follows it).
   */
  create(
    c: {
      action?: string;
      startFrame?: number;
      endFrame?: number;
      name?: string;
      sourceId?: string;
    },
    now: number,
  ) {
    const h = this.history;
    if (!h.sessionId) throw Error('Mimic has not recorded anything yet. Start listening first.');
    if (this.drafts.length >= MAX_DRAFTS) throw Error('That is as many drafts as Mimic keeps.');
    let range: Range | null;
    if (c.action === 'phrase') {
      range = h.latestPhrase(now);
      if (!range) throw Error('No phrase has been finished yet. Wait for one, then try again.');
    } else if (c.action === 'recent') {
      range = h.recent(10, now);
      if (!range) throw Error('Mimic has not recorded enough yet.');
    } else {
      if (c.startFrame === undefined || c.endFrame === undefined)
        throw Error('Select a moment first.');
      range = h.check({ startFrame: c.startFrame, endFrame: c.endFrame }, c.sourceId, now);
    }
    const name = c.name?.trim().slice(0, 100) || h.wordsIn(range, now) || null;
    const draft: DemoDraft = {
      id: id32(),
      ...range,
      name,
      createdUtcMs: now,
      updatedUtcMs: now,
      sessionId: h.sessionId,
      inTemp: false,
    };
    this.drafts.push(draft);
    this.changed();
    const first = !this.bandPlanned;
    this.bandPlanned = true;
    return { draft, first };
  }

  update(
    c: {
      draftId?: string;
      startFrame?: number;
      endFrame?: number;
      name?: string;
      sourceId?: string;
    },
    now: number,
  ) {
    const draft = this.find(c.draftId);
    if (c.sourceId && c.sourceId !== draft.sessionId)
      throw Error('That draft is from another recording.');
    if (c.startFrame !== undefined || c.endFrame !== undefined) {
      // Edges may reach into the history around the draft, as far as it can be seen.
      const range = this.history.check(
        { startFrame: c.startFrame ?? draft.startFrame, endFrame: c.endFrame ?? draft.endFrame },
        draft.sessionId,
        now,
      );
      draft.startFrame = range.startFrame;
      draft.endFrame = range.endFrame;
    }
    if (c.name !== undefined) draft.name = c.name.trim().slice(0, 100) || null;
    draft.updatedUtcMs = now;
    this.changed();
    return draft;
  }

  /** The host's own draft, or (the host may) the simulated guest's selection. */
  discard(id: string | undefined) {
    if (this.band && this.band.id === id) {
      this.band = null;
      this.changed();
      return { discarded: true };
    }
    const draft = this.find(id);
    this.drafts = this.drafts.filter((d) => d !== draft);
    this.changed();
    return { discarded: true };
  }

  /** Someone else's selection, copied into the host's own drafts. */
  copy(id: string | undefined, now: number) {
    const band = this.band;
    if (!band || band.id !== id) throw Error('That selection is no longer there.');
    const draft: DemoDraft = {
      id: id32(),
      startFrame: band.startFrame,
      endFrame: band.endFrame,
      name: band.name,
      createdUtcMs: now,
      updatedUtcMs: now,
      sessionId: this.history.sessionId!,
      inTemp: false,
    };
    this.drafts.push(draft);
    this.changed();
    return draft;
  }

  /** After Keep in Temp: marked, or closed (gone from the list) when asked. */
  kept(draft: DemoDraft, close: boolean, now: number) {
    if (close) this.drafts = this.drafts.filter((d) => d !== draft);
    else {
      draft.inTemp = true;
      draft.updatedUtcMs = now;
    }
    this.changed();
  }

  touch(draft: DemoDraft, now: number) {
    draft.updatedUtcMs = now;
    this.changed();
  }

  /**
   * The simulated guest captures an older phrase: one that differs from the host's newest draft, or the
   * oldest one. Returns false when there is nothing to pick (nothing heard yet).
   */
  addBand(now: number) {
    const phrases = this.history.phrases(now);
    const newest = [...this.drafts].sort((a, b) => b.createdUtcMs - a.createdUtcMs)[0];
    const pick =
      phrases.find(
        (p) => !newest || p.endFrame <= newest.startFrame || p.startFrame >= newest.endFrame,
      ) ?? phrases[0];
    if (!pick) return false;
    this.band = {
      id: id32(),
      ownerName: ALEX_BAND_OWNER,
      tag: 'alex',
      ...pick,
      name: this.history.wordsIn(pick, now) || null,
    };
    this.changed();
    return true;
  }
}
