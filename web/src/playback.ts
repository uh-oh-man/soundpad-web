import { useSyncExternalStore } from 'react';
import { controller } from './controller';
import { dismissNotice } from './notifications';
import { Disconnected, type Command, type Snapshot } from './protocol';
import { SeekQueue } from './seek';
import { siteRelaysIntents, supports } from './version';

/**
 * The one path every Game playback click takes (2.35): tiles, waveforms, regions, Temp, Mimic, Stop,
 * Panic and the keyboard. Each click is an intent, sent at once and never retried. The companion
 * decides which intent is newest; this module only keeps the page quiet about the ones that lost.
 */

/** What the module needs from the app; the tests supply their own. */
export type PlaybackHost = {
  /** The latest view, read when a click is sent and never taken from a render. */
  view(): { mode: 'landing' | 'host' | 'guest'; status: string; state: Snapshot };
  /** Website version of the host's open tab ('' when unknown). The host itself needs no relay. */
  hostSite(): string;
  /** Sends one command now and shows nothing; rejects with the refusal. */
  send(op: Command['op'], fields: Partial<Command>): Promise<unknown>;
  /** Sends one command the usual way: a refusal is shown as an error notice, then rejects. */
  run(op: Command['op'], fields: Partial<Command>): Promise<unknown>;
  /** Shows an error notice and returns its id. */
  notify(message: string): string;
  dismiss(id: string): void;
  now?(): number;
};

export type Outcome = {
  /**
   * `played`: it started. `done`: a stop, pause, seek or Panic went through. `superseded`: something
   * newer won, or it was stale, expired or held by the host. `dropped`: never sent. `failed`: refused.
   */
  result: 'played' | 'done' | 'superseded' | 'dropped' | 'failed';
  data?: unknown;
};
/** Commands that make a clip and can play it when it is ready. */
export type MakeOp = 'trimAudio' | 'mimicClip' | 'mimicLibraryImport';

/** How long a repeat of the same failure stays unannounced: the life of an error notice. */
const FAILURE_QUIET_MS = 8000;
/** Older companions refuse a seek or pause aimed at a sound that is no longer the one playing. */
const STALE = /^Playback changed\b/;
const DROPPED: Outcome = { result: 'dropped' };

const field = (data: unknown, key: string) =>
  data && typeof data === 'object' ? (data as Record<string, unknown>)[key] : undefined;

/** Keys for `usePendingIntent`: which control a click that is still unanswered belongs to. */
export const intentKey = {
  play: (soundId: string) => `play:${soundId}`,
  reverse: (soundId: string) => `reverse:${soundId}`,
  asset: (assetId: string, orientation: 'normal' | 'reversed') => `asset:${assetId}:${orientation}`,
  stop: 'stop',
  panic: 'panic',
} as const;

/** True for a guest while the host's Panic holds guest playback: play controls ignore presses. */
export const guestHeld = (state: Snapshot, host: boolean) =>
  !host && state.playbackSuspended === true;
/** What a held play control says on hover, and what the guest's status line reads. */
export const HELD_BY_HOST = 'The host paused guest playback';

export class PlaybackIntents {
  private sequence = 0;
  private pending = { key: '', sequence: 0, slow: false };
  private listeners = new Set<() => void>();
  private failure = { message: '', at: -Infinity, id: '' };
  private resumes = new Map<string, Promise<Outcome>>();
  private seeks = new SeekQueue((soundId, epoch, positionMs) =>
    this.adjust('seek', { soundId, epoch, positionMs }),
  );
  constructor(private host: PlaybackHost) {}

  subscribe = (listener: () => void) => {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  };
  /** Key of the click still waiting for the companion's answer, or ''. Playing comes from the snapshot. */
  getPending = () => this.pending.key;
  private setPending(key: string, sequence: number, slow = false) {
    this.pending = { key, sequence, slow };
    for (const listener of this.listeners) listener();
  }
  private settle(sequence: number) {
    if (this.pending.sequence === sequence && this.pending.key) this.setPending('', 0);
  }

  /** Connected to the companion (host) or to the host (guest): the same test the page shows. */
  private ready() {
    const { mode, status } = this.host.view();
    return mode === 'host' ? status === 'Host ready' : mode === 'guest' && status === 'Connected';
  }
  private held() {
    const { mode, state } = this.host.view();
    return guestHeld(state, mode === 'host');
  }
  /**
   * Whether a "make and play" can go as one command. It needs a 2.35 companion, and for a guest a host
   * tab that knows the field: an older tab's strict command check would disconnect the guest.
   */
  autoplays() {
    const { mode, state } = this.host.view();
    return (
      supports(state.companionVersion, 'intents') &&
      (mode === 'host' || siteRelaysIntents(this.host.hostSite()))
    );
  }

  /**
   * One playback problem on screen at a time: a repeat of the one already shown is not announced
   * again, and a different one replaces it. Losing the connection shows nothing here, because the
   * page already says so.
   */
  private fail(e: unknown) {
    if (e instanceof Disconnected) return;
    const message = e instanceof Error && e.message ? e.message : 'Playback failed.';
    const now = (this.host.now ?? Date.now)();
    if (message === this.failure.message && now - this.failure.at < FAILURE_QUIET_MS) return;
    if (this.failure.id) this.host.dismiss(this.failure.id);
    this.failure = { message, at: now, id: this.host.notify(message) };
  }

  /** A click that starts or stops audio. Sent now; from here on only the newest one may complain. */
  private async intent(
    key: string,
    op: Command['op'],
    fields: Partial<Command> = {},
  ): Promise<Outcome> {
    // Clicks made while disconnected (or held by the host) are dropped, never kept for later.
    if (!this.ready() || this.held()) return DROPPED;
    const sequence = ++this.sequence;
    this.setPending(key, sequence);
    try {
      const data = await this.host.send(op, fields);
      if (field(data, 'superseded') === true) return { result: 'superseded', data };
      return { result: op === 'stop' || op === 'panic' ? 'done' : 'played', data };
    } catch (e) {
      if (sequence === this.sequence) this.fail(e);
      return { result: 'failed' };
    } finally {
      this.settle(sequence);
    }
  }
  /** A seek or pause of what is playing: it changes that playback and replaces no other click. */
  private async adjust(op: 'seek' | 'pause', fields: Partial<Command>): Promise<Outcome> {
    if (!this.ready() || this.held()) return DROPPED;
    const sequence = this.sequence;
    try {
      const data = await this.host.send(op, fields);
      return { result: field(data, 'superseded') === true ? 'superseded' : 'done', data };
    } catch (e) {
      const stale = e instanceof Error && STALE.test(e.message);
      if (!stale && sequence === this.sequence) this.fail(e);
      return { result: stale ? 'superseded' : 'failed' };
    }
  }
  private library() {
    return this.host.view().state.library.version;
  }

  play(soundId: string) {
    return this.intent(intentKey.play(soundId), 'play', {
      soundId,
      libraryVersion: this.library(),
    });
  }
  startAt(soundId: string, positionMs: number) {
    return this.intent(intentKey.play(soundId), 'startAt', {
      soundId,
      libraryVersion: this.library(),
      positionMs,
    });
  }
  region(soundId: string, positionMs: number, endMs: number, repeat?: boolean) {
    return this.intent(intentKey.play(soundId), 'region', {
      soundId,
      libraryVersion: this.library(),
      positionMs,
      endMs,
      repeat,
    });
  }
  reverse(soundId: string) {
    return this.intent(intentKey.reverse(soundId), 'reverse', {
      soundId,
      libraryVersion: this.library(),
    });
  }
  audioPlay(assetId: string, orientation: 'normal' | 'reversed' = 'normal') {
    return this.intent(intentKey.asset(assetId, orientation), 'audioPlay', {
      assetId,
      orientation,
    });
  }
  stop() {
    return this.intent(intentKey.stop, 'stop');
  }
  /** Host only: stop Soundpad now and hold guest playback until `resume`. */
  panic() {
    return this.intent(intentKey.panic, 'panic', { action: 'enable' });
  }
  /** Host only: let guests play again. It starts nothing, so it replaces no pending click. */
  async resume(): Promise<Outcome> {
    if (!this.ready()) return DROPPED;
    try {
      return { result: 'done', data: await this.host.send('panic', { action: 'disable' }) };
    } catch (e) {
      this.fail(e);
      return { result: 'failed' };
    }
  }

  /** Pauses or resumes what the latest report says is playing. */
  pause() {
    const { soundId, epoch } = this.host.view().state.playback;
    if (!soundId || !epoch) return Promise.resolve(DROPPED);
    return this.adjust('pause', { soundId, epoch });
  }
  /** Moves what is playing to `positionMs`; rapid seeks collapse to the newest position. */
  async seek(positionMs: number) {
    const { soundId, epoch } = this.host.view().state.playback;
    if (soundId && epoch) await this.seeks.seek(soundId, epoch, positionMs);
  }
  /**
   * A click on a sound's waveform: seek if that sound is the one playing, else start it there. Decided
   * now from the latest report, not from the render the tile was drawn in, so two people alternating
   * do not send each other's old playback ids. A start of our own still on its way wins over the report.
   */
  async position(soundId: string, positionMs: number) {
    const { soundId: current, epoch, status } = this.host.view().state.playback;
    const starting = !!this.pending.key && !this.pending.slow;
    if (!starting && current === soundId && epoch && status !== 'STOPPED') {
      const sequence = this.sequence;
      await this.seeks.seek(soundId, epoch, positionMs);
      // The playback this seek aimed at was replaced on its way (someone else started a sound): this click
      // is still the newest input, and it meant "this sound, here", so start it there (2.35).
      const now = this.host.view().state.playback;
      if (sequence === this.sequence && now.epoch !== epoch)
        return void (await this.startAt(soundId, positionMs));
      // Resume only a sound that was paused when the click was made, as 2.34 did; one paused meanwhile stays paused.
      if (status === 'PAUSED') await this.resumeAfterSeek(soundId, epoch);
    } else await this.startAt(soundId, positionMs);
  }
  /** A seek on a paused sound also resumes it; one toggle per paused position, however many seeks. */
  private async resumeAfterSeek(soundId: string, epoch: string) {
    const current = this.host.view().state.playback;
    if (current.soundId !== soundId || current.epoch !== epoch || current.status !== 'PAUSED')
      return;
    const key = epoch + ':' + (current.positionRevision ?? 0);
    let task = this.resumes.get(key);
    if (!task) {
      task = this.adjust('pause', { soundId, epoch });
      this.resumes.set(key, task);
      void task.then((outcome) => {
        if (outcome.result !== 'done') this.resumes.delete(key);
      });
      if (this.resumes.size > 128) this.resumes.delete(this.resumes.keys().next().value!);
    }
    await task;
  }

  /**
   * Makes a clip (an edited copy, a Mimic clip, an import) and plays it when it is ready.
   *
   * With a 2.35 companion this is one command: the companion notes the click when it arrives and
   * starts the clip only if nothing newer was asked for by anyone meanwhile. Otherwise it is two
   * commands as before, and the second is skipped when this person clicked something else meanwhile.
   * A refusal to make the clip is shown and thrown, like any other command; `played` says whether the
   * new clip started.
   */
  async make(
    op: MakeOp,
    fields: Partial<Command>,
    key = 'make',
  ): Promise<{ data: unknown; played: boolean }> {
    if (!this.ready() || this.held())
      return { data: await this.host.run(op, fields), played: false };
    const sequence = ++this.sequence;
    this.setPending(key, sequence, true);
    try {
      if (this.autoplays()) {
        const data = await this.host.run(op, { ...fields, autoplay: true });
        return { data, played: field(data, 'played') === true };
      }
      const data = await this.host.run(op, fields);
      const assetId = field(data, 'assetId');
      if (typeof assetId !== 'string' || sequence !== this.sequence) return { data, played: false };
      return { data, played: (await this.audioPlay(assetId)).result === 'played' };
    } finally {
      this.settle(sequence);
    }
  }
}

export const playback = new PlaybackIntents({
  view: () => controller.getSnapshot(),
  hostSite: () => controller.hostSite(),
  send: (op, fields) => controller.run(op, fields, { quiet: true }),
  run: (op, fields) => controller.run(op, fields),
  notify: (message) => controller.notify(message, 'error'),
  dismiss: dismissNotice,
});

/** Key of the click this page is still waiting on ('' when none); see `intentKey`. */
export function usePendingIntent() {
  return useSyncExternalStore(playback.subscribe, playback.getPending);
}
