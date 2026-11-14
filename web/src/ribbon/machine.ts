import type { Snapshot } from '../protocol';
import { makeBase, project, type PlayheadBase } from '../playhead';
import {
  detectRipple,
  envelopes,
  follow,
  freshFollow,
  ribbonKey,
  ribbonTarget,
  type FollowState,
  type PeakEntry,
  type RibbonMode,
} from './envelope';

/** How brightly each state draws (spec 6.6). */
export const STATE_ALPHA = { idle: 0.35, waiting: 0.5, live: 1, settling: 1, still: 1.6 } as const;
/** The constant level of "playing, shape unknown". */
export const WAITING_LEVEL = 0.25;
/** The frozen level of the still picture while something plays. */
export const STILL_LEVEL = 0.6;
/** Below this the settling ribbon counts as idle again. */
export const SETTLED = 0.01;

/**
 * The ribbon's state between frames (spec 6.3, 6.4, 6.6), apart from drawing: which playback it
 * follows, the playhead it projects, and the smoothed level with its ripples. HeaderRibbon feeds it
 * each playback report (`report`) and asks it for a frame (`frame`); it holds no React state.
 */
export class RibbonMachine {
  key: string | null = null;
  base: PlayheadBase | null = null;
  follow: FollowState = freshFollow();
  private playback: Snapshot['playback'] | null = null;
  private hostId: string | null | undefined = null;
  private held = false;
  /** True from a live frame until the level has settled after the key went away. */
  private moving = false;

  /**
   * A playback report. A new sound (a new key) starts from silence, so nothing of the previous one
   * carries over; losing the key (Pause, Stop, Panic, held) keeps the level so it can settle. A seek
   * (new revision) only moves the base.
   */
  report(
    playback: Snapshot['playback'],
    hostId: string | null | undefined,
    held: boolean,
    now: number,
  ) {
    const key = ribbonKey(playback, hostId, held);
    if (key !== this.key) {
      this.key = key;
      if (key) this.follow = freshFollow();
      this.base = makeBase(playback, now);
    } else if (this.base && playback.positionRevision !== this.base.positionRevision) {
      this.base = makeBase(playback, now);
      // No ripple from the jump itself: the next can start only after the usual gap.
      this.follow.lastRipple = now;
    } else this.base = makeBase(playback, now, this.base ?? undefined);
    this.playback = playback;
    this.hostId = hostId;
    this.held = held;
  }

  /** The playhead now, in the playing clip's own timeline. */
  position(now: number, hidden = false) {
    if (!this.base || !this.playback) return 0;
    return project(this.base, now, this.playback.durationMs, hidden);
  }

  /** What the ribbon follows now, from the cached peaks of `entry` (the playing sound's). */
  target(entry: PeakEntry | undefined, now: number) {
    if (!this.playback) return { mode: 'idle' as const, key: null };
    return ribbonTarget(this.playback, this.hostId, this.held, entry, this.position(now));
  }

  /**
   * Advances the level by `dtMs` toward what is playing and says which state to draw. `settling` is
   * the animated fall to silence after a live play stops or is replaced by something without a shape.
   */
  frame(entry: PeakEntry | undefined, now: number, dtMs: number) {
    const target = this.target(entry, now);
    let mode: RibbonMode | 'settling' = target.mode;
    if (target.mode === 'live') {
      const position = this.position(now);
      const { fast, slow } = envelopes(target.level, target.spanMs, position);
      follow(this.follow, fast, dtMs);
      detectRipple(this.follow, fast, slow, now);
      this.moving = true;
    } else if (this.moving) {
      follow(this.follow, 0, dtMs);
      this.follow.ripples = this.follow.ripples.filter((r) => now - r.t0 < 1200);
      if (this.follow.level < SETTLED && !this.follow.ripples.length) {
        this.moving = false;
        this.follow = freshFollow();
      } else mode = 'settling';
    }
    return { mode, key: target.key, level: this.follow.level, ripples: this.follow.ripples };
  }
}
