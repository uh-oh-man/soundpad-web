import {
  command,
  isGuestAllowed,
  type Category,
  type Command,
  type Participant,
  type Snapshot,
  type Sound,
} from '../protocol';
import {
  bounded,
  DEFAULT_POLICY,
  effectivePermissions,
  forNewInvite,
  MAX_EXTRA,
  MAX_PLACES,
  places,
  REQUEST_MS,
  type SessionPolicy,
} from '../network/policy';
import type { LobbyRequest, Transport } from '../network/session';
import {
  DEMO_CATEGORIES,
  DEMO_SOUNDS,
  MIMIC_LOOP_MS,
  MIMIC_PLACEMENTS,
  MIMIC_SOURCE,
  mimicLoopLines,
  wordsWithin,
  type DemoWord,
} from './library';
import { clampSpeed, editedLengthMs, type ClipEdit, type Effect, type Level } from './dsp';

/**
 * The example session: a stand-in for the companion and the guest room that runs entirely in
 * this page. It answers the same commands with the same data shapes as the companion, so the real
 * components show it unchanged. Audio comes from a `DemoEngine` (Web Audio in the browser, a fake in
 * the unit tests). Nothing here opens a socket, a relay or a peer connection.
 */

/** What the example plays and edits audio with. Clip ids are the library's sound ids. */
export interface DemoEngine {
  /** Length of a loaded clip in ms (0 when unknown). */
  duration(clip: string): number;
  /** Starts a clip, replacing whatever plays; `ended` runs when a non-repeating one reaches its end. */
  play(
    clip: string,
    fromMs: number,
    endMs: number | null,
    repeat: boolean,
    ended: () => void,
  ): void;
  stop(): void;
  pause(): void;
  resume(): void;
  seek(ms: number): void;
  /** Position of the playing (or paused) clip in ms. */
  position(): number;
  /** Makes `target` from part of `source` with an effect; resolves with its length in ms. */
  derive(target: string, source: string, edit: ClipEdit): Promise<number>;
  reverse(target: string, source: string): Promise<number>;
  /** Makes `target` from Mimic's loop between two times since listening started. */
  fromLoop(target: string, fromMs: number, toMs: number): Promise<number>;
  /** Mimic's level bars (0 to 1000) between two times since listening started. */
  loopLevels(fromMs: number, toMs: number, count: number): number[];
  /** Decodes an uploaded file into `target`; rejects when the browser cannot read it. */
  decode(target: string, bytes: Uint8Array): Promise<number>;
  /** A WAV (base64) of an edit of a clip, or of part of the loop, for the host's private preview. */
  previewClip(
    source: string,
    edit: ClipEdit,
    maxMs: number,
  ): Promise<{ wav: string; seconds: number }>;
  previewLoop(fromMs: number, toMs: number): Promise<{ wav: string; seconds: number }>;
  waveform(clip: string): Level[] | null;
  forget(clip: string): void;
  close(): void;
}

export type DemoSessionInfo = {
  guests: string[];
  people: Record<string, Participant>;
  links: Record<string, Transport>;
  requests: LobbyRequest[];
  policy: SessionPolicy;
};
export type DemoHooks = {
  state: (snapshot: Snapshot) => void;
  session: (info: DemoSessionInfo) => void;
  notify: (message: string, kind?: 'info' | 'success' | 'error') => void;
};
export type DemoOptions = {
  /** Artwork as PNG data URLs, by sound key. */
  art?: Record<string, string>;
  /** 0 to 1; the tests fix it. */
  random?: () => number;
  /** Whether the page is on screen; the simulated guest only plays while it is. */
  visible?: () => boolean;
};

/** The companion version the example reports, so every control the website has is shown. */
export const DEMO_COMPANION = '2.37.0';
export const DEMO_HOST_ID = 'de30'.padEnd(64, '0');
/** Features that need the host's PC: the example says so instead of pretending. */
export const UNAVAILABLE = {
  keep: 'Not available in the example: Keep permanently writes a file and a Soundpad entry on the host’s PC.',
  spotify:
    'Not available in the example: Spotify controls work with the Spotify app on the host’s PC.',
  hotkey:
    'Not available in the example: the hotkey listens for a key in every app on the host’s PC, which a web page cannot do.',
  library:
    'Not available in the example: Voice Replay Studio’s library is read from the host’s PC.',
  other: 'Not available in the example.',
} as const;
export const TRANSCRIPT_NOTE =
  'Example transcript: NASA’s published wording with hand-placed timings, not recognized live.';
export const MAX_COPY_MS = 60000;
export const MIMIC_WINDOW_MS = 30000;
const MIMIC_RATE = 48000;
const MIMIC_BARS = 300;
/** Words show in the transcript this long after they are said, as with live recognition. */
const HEARD_MS = 1500;
/** Simulated guests: Alex joins after a few seconds; Sam asks to join once the host chooses Ask me first. */
export const GUEST_JOIN_MS = 4000;
export const SAM_ASKS_MS = 3000;
/** Alex plays something at most this often, and only while nothing plays and the page is visible. */
export const ALEX_GAP_MS = 20000;
const ALEX_SPREAD_MS = 15000;
const ALEX_PICKS = ['rimshot', 'air-horn', 'boing', 'cat-meow', 'wrong-buzzer', 'door-knock'];
const TEMP_CATEGORY = { id: '90', name: 'Temp - Soundpad Web [example]' };
const PLAYBACK_OPS: Command['op'][] = [
  'play',
  'startAt',
  'region',
  'reverse',
  'audioPlay',
  'stop',
  'pause',
  'seek',
];

type Guest = { peer: string; person: Participant; link: Transport };
export const ALEX: Guest = {
  peer: 'a1e5c0de-example',
  person: { id: '0e7a3b1e-0000-4000-8000-00000000a1e5', name: 'Alex' },
  link: 'direct',
};
export const SAM: Guest = {
  peer: '5a3c0de5-example',
  person: { id: '0e7a3b1e-0000-4000-8000-0000000005a3', name: 'Sam' },
  link: 'relay',
};

type Asset = {
  id: string;
  name: string;
  origin: 'native' | 'upload';
  uploader: string;
  uploaderId: string;
  createdAt: number;
  /** Sound id of the normal version (the native sound itself for a native source). */
  normal: string;
  normalVariant: string;
  reversed?: string;
  reversedVariant?: string;
};
type Playing = {
  soundId: string;
  epoch: string;
  requester: string;
  status: 'PLAYING' | 'PAUSED';
  regionStartMs: number;
  regionEndMs: number | null;
  repeat: boolean;
  positionRevision: number;
};
type Stats = {
  total: number;
  lastPlayedAt: number | null;
  participants: Record<string, { name: string; count: number }>;
};
type Activity = {
  commandId: string;
  soundId: string;
  at: number;
  participantId: string;
  name: string;
  source: 'host' | 'guest';
};

const superseded = (reason: 'newer' | 'suspended') => ({ superseded: true, reason, played: false });
const soundIdFor = (n: number) => 'de30' + n.toString(16).padStart(60, '0');
const effects: Effect[] = ['none', 'chipmunk', 'slowmo', 'echo', 'speed'];
const suffix = (edit: ClipEdit) =>
  edit.effect === 'none'
    ? 'trimmed'
    : edit.effect === 'chipmunk'
      ? 'chipmunk'
      : edit.effect === 'slowmo'
        ? 'slow-mo'
        : edit.effect === 'echo'
          ? 'echo'
          : `${Number(clampSpeed(edit.speed).toFixed(2))}x speed${edit.keepPitch ? ', same pitch' : ''}`;

export class DemoModel {
  private revision = 0;
  private libraryRevision = 1;
  private nextSound = DEMO_SOUNDS.length + 1;
  private sounds: Sound[];
  private words = new Map<string, DemoWord[]>();
  private categories: Category[];
  private assets: Asset[] = [];
  private tempRevision = 0;
  private playing?: Playing;
  /** Every playback intent counts here on arrival; a slow one plays only if it is still the newest. */
  private intent = 0;
  private suspended = false;
  private counter = 0;
  private permissions: Snapshot['permissions'] = {
    remotePlayback: true,
    guestCounter: false,
    guestSeeking: true,
    guestSpotify: true,
    guestUploads: true,
    guestTransforms: true,
    guestMimic: false,
  };
  private stats = new Map<string, Stats>();
  private activity: Activity[] = [];
  private statisticsVersion = 0;
  private mimic = {
    active: false,
    startedAt: 0,
    ring: '',
    transcribing: false,
    recent: [] as { assetId: string; name: string; by: string; at: number }[],
  };
  private guests: Guest[] = [];
  private requests: (LobbyRequest & { guest: Guest })[] = [];
  private policy: SessionPolicy = { ...DEFAULT_POLICY };
  private samAsked = false;
  private timers = new Set<ReturnType<typeof setTimeout>>();
  private tickTimer?: ReturnType<typeof setTimeout>;
  private closed = false;
  private random: () => number;
  private visible: () => boolean;

  constructor(
    private engine: DemoEngine,
    private hooks: DemoHooks,
    options: DemoOptions = {},
  ) {
    this.random = options.random ?? Math.random;
    this.visible =
      options.visible ??
      (() => typeof document === 'undefined' || document.visibilityState === 'visible');
    const art = options.art ?? {};
    // A sound this browser could not decode is left out of the library.
    const loaded = (key: string) => engine.duration(DemoModel.soundId(key)) > 0;
    this.sounds = DEMO_SOUNDS.flatMap((s, i) => {
      const id = soundIdFor(i + 1);
      if (!loaded(s.key)) return [];
      if (s.words) this.words.set(id, s.words);
      return [
        {
          id,
          name: s.title,
          icon: art[s.key] ?? null,
          durationMs: Math.round(engine.duration(id)),
          nativePlays: null,
          nativeLastPlayed: null,
        },
      ];
    });
    this.categories = DEMO_CATEGORIES.map((c, i) => ({
      id: String(i + 1),
      name: c.name,
      icon: art[c.art] ?? null,
      soundIds: DEMO_SOUNDS.flatMap((s, j) =>
        s.category === c.name && loaded(s.key) ? [soundIdFor(j + 1)] : [],
      ),
      children: [],
    }));
  }

  /** The library's sound id for a sound key (tests and the engine use it). */
  static soundId(key: string) {
    const index = DEMO_SOUNDS.findIndex((s) => s.key === key);
    if (index < 0) throw Error(`No example sound ${key}.`);
    return soundIdFor(index + 1);
  }

  /** Shows the session and starts the simulated guests. */
  begin() {
    this.publish();
    this.publishSession();
    this.later(GUEST_JOIN_MS, () => this.arrive(ALEX));
    this.scheduleAlex();
  }

  close() {
    this.closed = true;
    for (const timer of this.timers) clearTimeout(timer);
    this.timers.clear();
    clearTimeout(this.tickTimer);
    this.engine.close();
  }

  private later(ms: number, run: () => void) {
    const timer = setTimeout(() => {
      this.timers.delete(timer);
      if (!this.closed) run();
    }, ms);
    this.timers.add(timer);
  }

  // ---- What the page sees -------------------------------------------------------------------

  snapshot(): Snapshot {
    const playing = this.playing;
    const sound = playing && this.sounds.find((s) => s.id === playing.soundId);
    const temp = this.sounds.filter((s) => s.temporary && !s.internalVariant).map((s) => s.id);
    return {
      v: 1,
      revision: this.revision,
      available: true,
      error: null,
      protocol: 2,
      companionVersion: DEMO_COMPANION,
      hostId: DEMO_HOST_ID,
      managedAudioVersion: this.tempRevision,
      statisticsVersion: this.statisticsVersion,
      library: {
        version: `example-${this.libraryRevision}`,
        sounds: this.sounds,
        categories: temp.length
          ? [...this.categories, { ...TEMP_CATEGORY, icon: null, soundIds: temp, children: [] }]
          : this.categories,
      },
      playback: playing
        ? {
            status: playing.status,
            positionMs: Math.round(this.engine.position()),
            durationMs: sound?.durationMs ?? null,
            soundId: playing.soundId,
            epoch: playing.epoch,
            requester: playing.requester,
            sampledAt: Date.now(),
            identitySource: 'web',
            regionEndMs: playing.regionEndMs,
            repeat: playing.repeat,
            positionRevision: playing.positionRevision,
          }
        : {
            status: 'STOPPED',
            positionMs: 0,
            durationMs: null,
            soundId: null,
            epoch: null,
            requester: null,
            sampledAt: Date.now(),
            identitySource: 'web',
          },
      counter: this.counter,
      permissions: this.permissions,
      playbackSuspended: this.suspended,
      mimic: {
        active: this.mimic.active,
        source: this.mimic.active ? MIMIC_SOURCE.name : null,
        status: this.mimic.active ? 'Listening' : 'Off',
        windowMs: MIMIC_WINDOW_MS,
        transcribing: this.mimic.transcribing,
        transcriptModel: true,
        transcriptStatus: this.mimic.transcribing ? TRANSCRIPT_NOTE : 'Off',
        hotkey: false,
        hotkeyStatus: 'Off',
        hotkeySeconds: 10,
        hotkeyKey: 'F6',
        recent: this.mimic.recent,
      },
    };
  }

  private publish() {
    if (this.closed) return;
    this.revision++;
    this.hooks.state(this.snapshot());
    // While something plays the page hears its position once a second, like the companion's ticks.
    clearTimeout(this.tickTimer);
    if (this.playing?.status === 'PLAYING') this.tickTimer = setTimeout(() => this.publish(), 1000);
  }

  private publishSession() {
    if (this.closed) return;
    this.hooks.session({
      guests: this.guests.map((g) => g.peer),
      people: Object.fromEntries(this.guests.map((g) => [g.peer, g.person])),
      links: Object.fromEntries(this.guests.map((g) => [g.peer, g.link])),
      requests: this.requests.map(({ id, name, kind, at, expiresAt }) => ({
        id,
        name,
        kind,
        at,
        expiresAt,
      })),
      policy: this.policy,
    });
  }

  private libraryChanged() {
    this.libraryRevision++;
  }

  // ---- Commands -----------------------------------------------------------------------------

  /** Runs one command for the host (no `guest`) or a simulated guest; resolves with the ack data. */
  async send(c: Command, person: Participant, guest?: Guest): Promise<unknown> {
    if (this.closed) throw Error('The example has ended.');
    if (guest) {
      if (this.suspended && (PLAYBACK_OPS.includes(c.op) || c.autoplay))
        return superseded('suspended');
      const permissions = effectivePermissions(
        this.permissions,
        this.policy.overrides[guest.person.id],
      );
      if (!isGuestAllowed(c, { ...this.snapshot(), permissions }))
        throw Error('The host has disabled this action.');
    }
    const host = !guest;
    const by = { id: person.id, name: person.name, source: host ? 'host' : 'guest' } as const;
    switch (c.op) {
      case 'play':
        this.intent++;
        return this.start(this.sound(c.soundId).id, 0, null, false, by, c.id);
      case 'startAt':
        this.intent++;
        return this.start(this.sound(c.soundId).id, c.positionMs ?? 0, null, false, by, c.id);
      case 'region': {
        const sound = this.sound(c.soundId);
        const from = c.positionMs ?? 0,
          to = c.endMs ?? 0;
        if (to - from < 50) throw Error('Choose a region of at least 50 ms.');
        this.intent++;
        return this.start(sound.id, from, to, c.repeat === true, by, c.id);
      }
      case 'stop':
        this.intent++;
        this.halt();
        return { stopped: true };
      case 'pause':
      case 'seek':
        return this.adjust(c);
      case 'panic':
        if (!host) throw Error('Only the host can do that.');
        if (c.action === 'disable') {
          this.suspended = false;
          this.publish();
          return { suspended: false };
        }
        this.intent++;
        this.suspended = true;
        this.halt();
        return { suspended: true };
      case 'counter':
        this.counter = Math.max(0, this.counter + (c.delta ?? 0));
        this.publish();
        return { counter: this.counter };
      case 'reset':
        this.hostOnly(host);
        this.counter = 0;
        this.publish();
        return { counter: 0 };
      case 'permissions':
        this.hostOnly(host);
        if (c.permissions) this.permissions = { ...this.permissions, ...c.permissions };
        this.publish();
        return undefined;
      case 'statistics':
        return this.statistics(c.soundId);
      case 'clearStatistics':
        this.hostOnly(host);
        this.stats.clear();
        this.activity = [];
        this.statisticsVersion++;
        this.publish();
        return undefined;
      case 'icon': {
        this.hostOnly(host);
        const sound = this.sound(c.soundId);
        if (c.image && !/^data:image\/(png|jpeg);base64,[A-Za-z0-9+/=]+$/.test(c.image))
          throw Error('Choose a PNG or JPEG image.');
        this.sounds = this.sounds.map((s) =>
          s.id === sound.id ? { ...s, icon: c.image ?? null } : s,
        );
        this.libraryChanged();
        this.publish();
        return undefined;
      }
      case 'refresh':
      case 'beginSession':
      case 'endSession':
      case 'revokePeer':
      case 'retryAudio':
      case 'retryCleanup':
        this.publish();
        return undefined;
      case 'cleanupCategories':
        return { removed: 0 };
      case 'waveform':
        return this.waveform(this.sound(c.soundId).id);
      case 'spotify':
        if (c.action && c.action !== 'status') throw Error(UNAVAILABLE.spotify);
        return {
          enabled: true,
          available: false,
          canToggle: false,
          canNext: false,
          canPrevious: false,
          error: UNAVAILABLE.spotify,
        };
      case 'reverse':
        return this.reverse(c, by);
      case 'audioAssets':
        return this.audioAssets(host);
      case 'audioPlay':
        return this.audioPlay(c, by);
      case 'removeAudio':
        return this.removeAudio(c, person, host);
      case 'clearTemp':
        this.hostOnly(host);
        return this.clearTemp();
      case 'keepAudio':
        throw Error(UNAVAILABLE.keep);
      case 'trimAudio':
        return this.trim(c, by, !!guest);
      case 'trimPreview':
        this.hostOnly(host);
        return this.trimPreview(c);
      case 'mimicSources':
        this.hostOnly(host);
        return { sources: [MIMIC_SOURCE] };
      case 'mimicStart':
        this.hostOnly(host);
        if (c.sourceId !== MIMIC_SOURCE.id) throw Error('That source is not available.');
        this.mimic = {
          ...this.mimic,
          active: true,
          startedAt: Date.now(),
          ring: crypto.randomUUID(),
          // Transcripts start on, so the example shows them at once; the checkbox turns them off.
          transcribing: true,
        };
        this.publish();
        return undefined;
      case 'mimicStop':
        this.hostOnly(host);
        this.mimic = { ...this.mimic, active: false, ring: '' };
        this.publish();
        return undefined;
      case 'mimicTranscribe':
        this.hostOnly(host);
        this.mimic.transcribing = c.action !== 'disable';
        this.publish();
        return undefined;
      case 'mimicHotkey':
        throw Error(UNAVAILABLE.hotkey);
      case 'mimicLibrary':
      case 'mimicLibraryImport':
        throw Error(UNAVAILABLE.library);
      case 'mimicPeek':
        return this.peek();
      case 'mimicClip':
        return this.mimicClip(c, by, !!guest);
      case 'mimicPreview':
        this.hostOnly(host);
        return this.engine.previewLoop(...this.selection(c));
      default:
        throw Error(UNAVAILABLE.other);
    }
  }

  private hostOnly(host: boolean) {
    if (!host) throw Error('Only the host can do that.');
  }

  private sound(id: string | undefined) {
    const sound = this.sounds.find((s) => s.id === id);
    if (!sound) throw Error('That sound is no longer in the library.');
    return sound;
  }

  // ---- Playback -----------------------------------------------------------------------------

  private start(
    soundId: string,
    fromMs: number,
    endMs: number | null,
    repeat: boolean,
    by: { id: string; name: string; source: 'host' | 'guest' },
    commandId: string,
  ) {
    const duration = this.engine.duration(soundId);
    const from = Math.max(0, Math.min(fromMs, Math.max(0, duration - 1)));
    const end = endMs === null ? null : Math.max(from + 1, Math.min(endMs, duration));
    const epoch = crypto.randomUUID();
    this.playing = {
      soundId,
      epoch,
      requester: by.name,
      status: 'PLAYING',
      regionStartMs: from,
      regionEndMs: end,
      repeat: end !== null && repeat,
      positionRevision: 0,
    };
    this.engine.play(soundId, from, end, end !== null && repeat, () => this.ended(epoch));
    this.count(soundId, by, commandId);
    this.publish();
    return { played: true, epoch };
  }

  private ended(epoch: string) {
    if (this.playing?.epoch !== epoch) return;
    this.playing = undefined;
    this.publish();
  }

  /** Stops whatever plays, without counting as an intent (callers count it). */
  private halt() {
    this.engine.stop();
    this.playing = undefined;
    this.publish();
  }

  private adjust(c: Command) {
    const playing = this.playing;
    // Aimed at playback that has since been replaced: answered quietly, as the companion does.
    if (!playing || playing.soundId !== c.soundId || playing.epoch !== c.epoch)
      return superseded('newer');
    if (c.op === 'pause') {
      if (playing.status === 'PLAYING') {
        this.engine.pause();
        playing.status = 'PAUSED';
      } else {
        this.engine.resume();
        playing.status = 'PLAYING';
      }
    } else {
      const duration = this.engine.duration(playing.soundId);
      this.engine.seek(Math.max(0, Math.min(c.positionMs ?? 0, Math.max(0, duration - 1))));
      playing.positionRevision++;
    }
    this.publish();
    return { epoch: playing.epoch };
  }

  private count(
    soundId: string,
    by: { id: string; name: string; source: 'host' | 'guest' },
    commandId: string,
  ) {
    const at = Date.now();
    const stats = this.stats.get(soundId) ?? { total: 0, lastPlayedAt: null, participants: {} };
    const person = stats.participants[by.id] ?? { name: by.name, count: 0 };
    this.stats.set(soundId, {
      total: stats.total + 1,
      lastPlayedAt: at,
      participants: { ...stats.participants, [by.id]: { name: by.name, count: person.count + 1 } },
    });
    this.activity = [
      ...this.activity,
      { commandId, soundId, at, participantId: by.id, name: by.name, source: by.source },
    ].slice(-100);
    this.statisticsVersion++;
  }

  private statistics(soundId?: string) {
    if (!soundId)
      return {
        totals: Object.fromEntries(
          this.sounds.map((s) => {
            const stats = this.stats.get(s.id);
            return [s.id, { total: stats?.total ?? 0, lastPlayedAt: stats?.lastPlayedAt ?? null }];
          }),
        ),
        recent: this.activity.slice(-20),
      };
    return {
      statistics: this.stats.get(soundId) ?? { total: 0, lastPlayedAt: null, participants: {} },
      recent: this.activity.filter((a) => a.soundId === soundId).slice(-20),
    };
  }

  private waveform(soundId: string) {
    const levels = this.engine.waveform(soundId);
    const durationMs = Math.round(this.engine.duration(soundId));
    if (!levels || !durationMs)
      return {
        soundId,
        version: 'example',
        status: 'unavailable',
        error: 'This sound has no audio in the example.',
      };
    return {
      soundId,
      version: 'example',
      status: 'ready',
      durationMs,
      analyzedMs: durationMs,
      levels,
    };
  }

  // ---- Temp ---------------------------------------------------------------------------------

  private addSound(sound: Omit<Sound, 'id' | 'icon'>, words?: DemoWord[]) {
    const id = soundIdFor(this.nextSound++);
    this.sounds = [...this.sounds, { id, icon: null, ...sound }];
    if (words?.length) this.words.set(id, words);
    this.libraryChanged();
    return id;
  }

  private tempChanged() {
    this.tempRevision++;
  }

  /** The Temp source for a sound: its own asset, or a new native one for a library sound. */
  private assetFor(sound: Sound, by: { id: string; name: string }) {
    const own = this.assets.find((a) => a.id === sound.assetId || a.normal === sound.id);
    if (own) return own;
    const asset: Asset = {
      id: crypto.randomUUID(),
      name: sound.name,
      origin: 'native',
      uploader: by.name,
      uploaderId: by.id,
      createdAt: Date.now(),
      normal: sound.id,
      normalVariant: crypto.randomUUID(),
    };
    this.assets = [...this.assets, asset];
    return asset;
  }

  /** Makes the reversed version of a source if it has none yet. */
  private async ensureReversed(asset: Asset) {
    if (asset.reversed) return asset.reversed;
    const source = this.sound(asset.normal);
    const id = soundIdFor(this.nextSound++);
    const length = await this.engine.reverse(id, source.id);
    if (this.closed) throw Error('The example has ended.');
    if (asset.reversed) {
      this.engine.forget(id);
      return asset.reversed;
    }
    this.sounds = [
      ...this.sounds,
      {
        id,
        name: `${asset.name} (reversed)`,
        icon: null,
        durationMs: Math.round(length),
        assetId: asset.id,
        orientation: 'reversed',
        audioKind: 'generated',
        internalVariant: true,
        temporary: true,
        uploadedBy: asset.uploader,
      },
    ];
    this.libraryChanged();
    asset.reversed = id;
    asset.reversedVariant = crypto.randomUUID();
    this.tempChanged();
    return id;
  }

  /** Plays a clip that may need making first, only if no newer playback was asked for meanwhile. */
  private async playWhenReady(
    make: () => Promise<string>,
    by: { id: string; name: string; source: 'host' | 'guest' },
    commandId: string,
  ) {
    const mine = ++this.intent;
    const id = await make();
    if (mine !== this.intent) return superseded('newer');
    if (by.source === 'guest' && this.suspended) return superseded('suspended');
    return this.start(id, 0, null, false, by, commandId);
  }

  private reverse(c: Command, by: { id: string; name: string; source: 'host' | 'guest' }) {
    const sound = this.sound(c.soundId);
    const asset = this.assetFor(sound, by);
    return this.playWhenReady(() => this.ensureReversed(asset), by, c.id);
  }

  private asset(id: string | undefined) {
    const asset = this.assets.find((a) => a.id === id);
    if (!asset) throw Error('That audio is no longer in Temp.');
    return asset;
  }

  private audioPlay(c: Command, by: { id: string; name: string; source: 'host' | 'guest' }) {
    const asset = this.asset(c.assetId);
    return this.playWhenReady(
      async () => (c.orientation === 'reversed' ? this.ensureReversed(asset) : asset.normal),
      by,
      c.id,
    );
  }

  private audioAssets(host: boolean) {
    const variant = (asset: Asset, orientation: 'normal' | 'reversed') => {
      const soundId = orientation === 'normal' ? asset.normal : asset.reversed;
      if (!soundId) return null;
      const sound = this.sounds.find((s) => s.id === soundId);
      return {
        id: orientation === 'normal' ? asset.normalVariant : asset.reversedVariant!,
        soundId,
        orientation,
        state: 'ready',
        ready: true,
        // A native sound is already in the library for good; everything the session made is temporary.
        permanent: orientation === 'normal' && asset.origin === 'native',
        legacyApproval: false,
        current: true,
        nativeLabel: sound?.name ?? null,
      };
    };
    const owned = (a: Asset) =>
      [a.origin === 'upload' ? a.normal : null, a.reversed ?? null].filter((x): x is string => !!x);
    const views = this.assets.map((a) => {
      const normal = variant(a, 'normal'),
        reversed = variant(a, 'reversed');
      return {
        id: a.id,
        name: a.name,
        origin: a.origin,
        uploader: a.uploader,
        state: 'temporary',
        createdAt: a.createdAt,
        normal,
        reversed,
        variants: [normal, reversed].filter((v) => !!v),
        hasTemporary: owned(a).length > 0,
      };
    });
    const bytes = this.assets
      .flatMap(owned)
      .reduce((sum, id) => sum + Math.round(this.engine.duration(id) * 176.4), 0);
    return {
      manager: {
        revision: this.tempRevision,
        assets: views,
        issues: [],
        blocked: false,
        temporaryBytes: bytes,
      },
      locations: host
        ? this.assets.flatMap((a) =>
            owned(a).map((id) => ({
              assetId: a.id,
              variantId: id === a.reversed ? a.reversedVariant! : a.normalVariant,
              path: 'This browser tab’s memory (nothing is saved on a disk)',
              storage: 'example',
            })),
          )
        : null,
    };
  }

  private belongsTo(asset: Asset) {
    return [asset.normal, asset.reversed].includes(this.playing?.soundId);
  }

  private removeAudio(c: Command, person: Participant, host: boolean) {
    const asset = this.asset(c.assetId);
    if (!host && asset.uploaderId !== person.id)
      throw Error('Guests can remove only audio they added.');
    if (this.belongsTo(asset)) {
      if (c.action !== 'stopIfPlaying') throw Error('Stop playback before removing this audio.');
      this.intent++;
      this.engine.stop();
      this.playing = undefined;
    }
    this.drop(asset);
    this.publish();
    return { removed: true };
  }

  private drop(asset: Asset) {
    const gone = [asset.origin === 'upload' ? asset.normal : '', asset.reversed ?? ''].filter(
      Boolean,
    );
    for (const id of gone) {
      this.engine.forget(id);
      this.words.delete(id);
    }
    this.sounds = this.sounds.filter((s) => !gone.includes(s.id));
    this.assets = this.assets.filter((a) => a !== asset);
    this.mimic.recent = this.mimic.recent.filter((r) => r.assetId !== asset.id);
    this.libraryChanged();
    this.tempChanged();
  }

  private clearTemp() {
    const cleared = this.assets.length;
    if (this.assets.some((a) => this.belongsTo(a))) {
      this.intent++;
      this.engine.stop();
      this.playing = undefined;
    }
    for (const asset of [...this.assets]) this.drop(asset);
    this.publish();
    return { cleared, pending: 0 };
  }

  /** Adds a finished clip to Temp as an upload, and to the library's Temp category. */
  private addUpload(
    id: string,
    name: string,
    length: number,
    by: { id: string; name: string },
    words?: DemoWord[],
  ) {
    const asset: Asset = {
      id: crypto.randomUUID(),
      name,
      origin: 'upload',
      uploader: by.name,
      uploaderId: by.id,
      createdAt: Date.now(),
      normal: id,
      normalVariant: crypto.randomUUID(),
    };
    this.sounds = [
      ...this.sounds,
      {
        id,
        name,
        icon: null,
        durationMs: Math.round(length),
        assetId: asset.id,
        orientation: 'normal',
        audioKind: 'upload',
        temporary: true,
        uploadedBy: by.name,
      },
    ];
    if (words?.length) this.words.set(id, words);
    this.assets = [...this.assets, asset];
    this.libraryChanged();
    this.tempChanged();
    return asset;
  }

  private edit(c: Command, length: number): ClipEdit {
    const effect = (c.action ?? 'none') as Effect;
    if (!effects.includes(effect)) throw Error('Unknown effect.');
    const fromMs = Math.max(0, Math.min(c.positionMs ?? 0, length));
    const toMs = Math.max(0, Math.min(c.endMs ?? length, length));
    if (toMs - fromMs < 100) throw Error('Choose at least 0.1 s.');
    const edit: ClipEdit = {
      fromMs,
      toMs,
      effect,
      ...(effect === 'speed' && { speed: clampSpeed(c.speed), keepPitch: c.keepPitch === true }),
    };
    if (editedLengthMs(edit) > MAX_COPY_MS)
      throw Error(
        `In the example, an edited copy can be at most ${MAX_COPY_MS / 1000} s long. Choose a shorter part or a faster speed.`,
      );
    return edit;
  }

  private async trim(
    c: Command,
    by: { id: string; name: string; source: 'host' | 'guest' },
    guest: boolean,
  ) {
    const asset = this.asset(c.assetId);
    const source = this.sound(asset.normal);
    const edit = this.edit(c, this.engine.duration(source.id));
    const mine = c.autoplay ? ++this.intent : 0;
    const id = soundIdFor(this.nextSound++);
    const length = await this.engine.derive(id, source.id, edit);
    if (this.closed) throw Error('The example has ended.');
    const words =
      edit.effect === 'none' ? wordsWithin(this.words.get(source.id), edit.fromMs, edit.toMs) : [];
    // Unnamed plain trims of speech are named after their words while transcripts are on (2.24).
    const name = (
      c.name?.trim() ||
      (words.length && this.mimic.transcribing && edit.toMs - edit.fromMs <= 30000
        ? words.map((w) => w.w).join(' ')
        : `${asset.name} (${suffix(edit)})`)
    ).slice(0, 100);
    const made = this.addUpload(id, name, length, by, words);
    this.publish();
    if (!c.autoplay) return { assetId: made.id, state: 'temporary' };
    if (mine !== this.intent)
      return { assetId: made.id, state: 'temporary', ...superseded('newer') };
    if (guest && this.suspended)
      return { assetId: made.id, state: 'temporary', ...superseded('suspended') };
    return { assetId: made.id, state: 'temporary', ...this.start(id, 0, null, false, by, c.id) };
  }

  private trimPreview(c: Command) {
    const asset = this.asset(c.assetId);
    const edit = this.edit(c, this.engine.duration(asset.normal));
    return this.engine.previewClip(asset.normal, edit, 20000);
  }

  /** Uploads go straight into Temp, decoded by this browser. */
  async upload(bytes: Uint8Array, name: string, person: Participant) {
    if (this.closed) throw Error('The example has ended.');
    const id = soundIdFor(this.nextSound++);
    let length: number;
    try {
      length = await this.engine.decode(id, bytes);
    } catch {
      throw Error('This browser could not read that audio file.');
    }
    const clean =
      name
        .replace(/\.[^.]{1,8}$/, '')
        .replace(/[\p{Cc}\p{Cf}]/gu, '')
        .trim()
        .slice(0, 100) || 'Upload';
    const asset = this.addUpload(id, clean, length, person);
    this.publish();
    return { assetId: asset.id, state: 'temporary' };
  }

  // ---- Mimic --------------------------------------------------------------------------------

  private listening() {
    if (!this.mimic.active) throw Error('Mimic is not listening.');
    return Date.now() - this.mimic.startedAt;
  }

  private peek() {
    const elapsed = this.listening();
    const viewStart = elapsed - MIMIC_WINDOW_MS;
    const lines: { startMs: number; endMs: number; text: string }[] = [];
    if (this.mimic.transcribing) {
      const loop = mimicLoopLines();
      for (let k = Math.floor(viewStart / MIMIC_LOOP_MS); k * MIMIC_LOOP_MS <= elapsed; k++)
        for (const line of loop) {
          const start = k * MIMIC_LOOP_MS + line.startMs,
            end = k * MIMIC_LOOP_MS + line.endMs;
          if (start < 0 || end + HEARD_MS > elapsed || end <= viewStart) continue;
          lines.push({
            startMs: Math.round(start - viewStart),
            endMs: Math.round(end - viewStart),
            text: line.text,
          });
        }
    }
    return {
      end: Math.round((elapsed * MIMIC_RATE) / 1000),
      rate: MIMIC_RATE,
      windowMs: MIMIC_WINDOW_MS,
      levels: this.engine.loopLevels(viewStart, elapsed, MIMIC_BARS),
      active: true,
      transcript: lines,
      ringId: this.mimic.ring,
    };
  }

  /** A Mimic selection as times since listening started. */
  private selection(c: Command): [number, number] {
    const elapsed = this.listening();
    if (c.sourceId && c.sourceId !== this.mimic.ring)
      throw Error('Mimic started again since then. Select the moment again.');
    const viewEnd = ((c.mimicEnd ?? 0) * 1000) / MIMIC_RATE;
    const from = Math.max(0, viewEnd - MIMIC_WINDOW_MS + (c.positionMs ?? 0));
    const to = Math.min(elapsed, viewEnd - MIMIC_WINDOW_MS + (c.endMs ?? 0));
    if (to - from < 150) throw Error('Mimic had not started listening yet at that moment.');
    if (to - from > MIMIC_WINDOW_MS) throw Error('Choose at most 30 seconds.');
    return [from, to];
  }

  /** Words spoken between two times since listening started. */
  private loopWords(from: number, to: number) {
    const words: DemoWord[] = [];
    for (let k = Math.floor(from / MIMIC_LOOP_MS); k * MIMIC_LOOP_MS < to; k++)
      for (const { key, atMs } of MIMIC_PLACEMENTS) {
        const offset = k * MIMIC_LOOP_MS + atMs;
        const shifted = (DEMO_SOUNDS.find((s) => s.key === key)?.words ?? []).map((w) => ({
          ...w,
          startMs: w.startMs + offset,
          endMs: w.endMs + offset,
        }));
        words.push(...wordsWithin(shifted, from, to));
      }
    return words.sort((a, b) => a.startMs - b.startMs);
  }

  private async mimicClip(
    c: Command,
    by: { id: string; name: string; source: 'host' | 'guest' },
    guest: boolean,
  ) {
    const [from, to] = this.selection(c);
    const words = this.loopWords(from, to);
    const mine = c.autoplay ? ++this.intent : 0;
    const id = soundIdFor(this.nextSound++);
    const length = await this.engine.fromLoop(id, from, to);
    if (this.closed) throw Error('The example has ended.');
    const name = (c.name?.trim() || words.map((w) => w.w).join(' ') || 'Mimic clip').slice(0, 100);
    const asset = this.addUpload(id, name, length, by, words);
    this.mimic.recent = [
      { assetId: asset.id, name, by: by.name, at: Date.now() },
      ...this.mimic.recent,
    ].slice(0, 20);
    this.publish();
    if (!c.autoplay) return { assetId: asset.id, state: 'temporary' };
    if (mine !== this.intent)
      return { assetId: asset.id, state: 'temporary', ...superseded('newer') };
    if (guest && this.suspended)
      return { assetId: asset.id, state: 'temporary', ...superseded('suspended') };
    return { assetId: asset.id, state: 'temporary', ...this.start(id, 0, null, false, by, c.id) };
  }

  // ---- Who is in the session ----------------------------------------------------------------

  getPolicy() {
    return this.policy;
  }

  setPolicy(policy: SessionPolicy) {
    const asking = policy.admission === 'ask' && this.policy.admission !== 'ask';
    this.policy = policy;
    this.publishSession();
    // The example's second friend asks to join once the host wants to be asked first.
    if (asking && !this.samAsked && !this.present(SAM)) {
      this.samAsked = true;
      this.later(SAM_ASKS_MS, () => this.arrive(SAM));
    }
  }

  private present(guest: Guest) {
    return this.guests.some((g) => g.peer === guest.peer);
  }

  private arrive(guest: Guest) {
    if (this.present(guest) || this.requests.some((r) => r.id === guest.person.id)) return;
    if (this.policy.turnedAway.some((p) => p.id === guest.person.id)) return;
    const admitted = this.policy.admitted.includes(guest.person.id);
    if (
      (this.policy.admission === 'ask' && !admitted) ||
      this.guests.length >= places(this.policy)
    ) {
      const at = Date.now();
      const full = this.guests.length >= places(this.policy);
      this.requests = [
        ...this.requests,
        {
          id: guest.person.id,
          name: guest.person.name,
          kind: full ? 'extra' : 'admit',
          at,
          expiresAt: at + REQUEST_MS,
          guest,
        },
      ];
      this.later(REQUEST_MS, () => {
        if (!this.requests.some((r) => r.id === guest.person.id)) return;
        this.requests = this.requests.filter((r) => r.id !== guest.person.id);
        this.publishSession();
      });
      this.hooks.notify(
        `${guest.person.name} ${full ? 'asks for an extra place' : 'wants to join'}. Let them in or turn them away (a simulated guest).`,
      );
      this.publishSession();
      return;
    }
    this.join(guest);
  }

  private join(guest: Guest) {
    this.guests = [...this.guests, guest];
    this.policy = {
      ...this.policy,
      admitted: bounded([
        ...this.policy.admitted.filter((id) => id !== guest.person.id),
        guest.person.id,
      ]),
    };
    this.hooks.notify(
      `${guest.person.name} joined (a simulated guest, ${guest.link === 'relay' ? 'relayed' : 'direct'}).`,
      'success',
    );
    this.publishSession();
  }

  async letIn(id: string) {
    const request = this.requests.find((r) => r.id === id);
    const away = this.policy.turnedAway.find((p) => p.id === id);
    if (!request && !away) return;
    let policy: SessionPolicy = {
      ...this.policy,
      turnedAway: this.policy.turnedAway.filter((p) => p.id !== id),
    };
    if (request && this.guests.length >= places(policy)) {
      if (places(policy) >= MAX_PLACES + MAX_EXTRA)
        throw Error(`The session is at its largest (${MAX_PLACES + MAX_EXTRA} guests).`);
      policy = { ...policy, extra: policy.extra + 1 };
    }
    this.policy = policy;
    this.requests = this.requests.filter((r) => r.id !== id);
    if (request) this.join(request.guest);
    else this.publishSession();
  }

  turnAway(id: string) {
    const request = this.requests.find((r) => r.id === id);
    if (!request) return;
    this.requests = this.requests.filter((r) => r.id !== id);
    this.policy = {
      ...this.policy,
      turnedAway: bounded([
        ...this.policy.turnedAway.filter((p) => p.id !== id),
        request.guest.person,
      ]),
      admitted: this.policy.admitted.filter((a) => a !== id),
    };
    this.publishSession();
  }

  disconnect(peer: string) {
    const guest = this.guests.find((g) => g.peer === peer);
    if (!guest) return;
    this.guests = this.guests.filter((g) => g !== guest);
    this.hooks.notify(`${guest.person.name} was disconnected.`);
    this.publishSession();
  }

  /** Replace invite: everyone is disconnected; Alex comes back with the new invitation. */
  rotate() {
    this.guests = [];
    this.requests = [];
    this.policy = forNewInvite(this.policy);
    this.samAsked = false;
    this.publishSession();
    this.later(GUEST_JOIN_MS, () => this.arrive(ALEX));
  }

  private scheduleAlex() {
    this.later(ALEX_GAP_MS + this.random() * ALEX_SPREAD_MS, () => {
      this.alexTurn();
      this.scheduleAlex();
    });
  }

  /** Alex plays a short sound now and then: only while the page is visible and nothing plays. */
  private alexTurn() {
    const alex = this.guests.find((g) => g.peer === ALEX.peer);
    if (!alex || !this.visible() || this.playing || this.suspended) return;
    const allowed = effectivePermissions(this.permissions, this.policy.overrides[alex.person.id]);
    if (!allowed.remotePlayback) return;
    const picks = ALEX_PICKS.map((key) => DemoModel.soundId(key)).filter((id) =>
      this.sounds.some((s) => s.id === id),
    );
    if (!picks.length) return;
    const sound = this.sound(picks[Math.floor(this.random() * picks.length) % picks.length]);
    void this.send(command('play', { soundId: sound.id }), alex.person, alex)
      .then((result) => {
        if ((result as { played?: boolean })?.played)
          this.hooks.notify(`${alex.person.name} played “${sound.name}”.`);
      })
      .catch(() => {});
  }
}
