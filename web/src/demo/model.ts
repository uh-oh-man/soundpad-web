import {
  isGuestAllowed,
  type Category,
  type Command,
  type Participant,
  type Snapshot,
  type Sound,
} from '../protocol';
import {
  DEFAULT_POLICY,
  effectivePermissions,
  forNewInvite,
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
import {
  clampSpeed,
  editedLengthMs,
  type ClipEdit,
  type Effect,
  type Level,
  type MixPart,
} from './dsp';
import { DemoHistory, loopMs, type Range } from './history';
import { DemoDrafts } from './drafts';
import { DemoVoices, LIBRARY_UNAVAILABLE } from './voices';
import { HOST_ONLY } from '../hosting';
import { DemoSentences } from './sentences';

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
  /** Makes `target` from a mix of parts (sentences, palette words); resolves with its length in ms. */
  mix(target: string, parts: MixPart[]): Promise<number>;
  /** A WAV (base64) of a mix, for a private preview. */
  previewMix(parts: MixPart[], maxMs: number): Promise<{ wav: string; seconds: number }>;
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
};

/**
 * The companion version the example reports, so the page shows the controls of that version. Stage G raised it
 * from 2.37 to 2.40 with the recording history (2.39: a virtual history over the example's loop, see
 * `history.ts`) and drafts (2.40: `drafts.ts`), then to 2.42 with the voice
 * library (2.41: `voices.ts`, three simulated voices from the example's own clips) and sentences with the
 * speaking palette (2.42: `sentences.ts`), then to 2.43 with Stage G's companion features: Kick's session
 * count (`counterSession`, kept like the companion does), Speed and effects on any example sound (trimAudio and
 * trimPreview take a `soundId` source), and host style sync, which the example never uses (its style stays on
 * this browser, so uiPrefsGet/uiPrefsSet are refused). Every op of every feature up to this version is answered
 * here, or refused with an honest reason on the allow-list in tests/unit/demo-coverage.test.ts, which fails when
 * that is not so. Raise it only together with the ops of the features it unlocks, and that test.
 */
export const DEMO_COMPANION = '2.43.0';
export const DEMO_HOST_ID = 'de30'.padEnd(64, '0');
/** Features that need the host's PC: the example says so instead of pretending. */
export const UNAVAILABLE = {
  ...HOST_ONLY,
  share: 'Not available in the example: the example is a solo session.',
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
const TEMP_CATEGORY = { id: '90', name: 'Temp - Soundpad Web [example]' };
/** The storage figures the example reports (budgetStatus): fixed, since nothing is written to a disk. */
const TEMP_MB = 100,
  KEPT_MB = 200;
const MB = 1024 * 1024;
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
  /** Kick presses since the example started (the companion's 2.43 session count, never stored). */
  private counterSession = 0;
  private history = new DemoHistory();
  private drafts = new DemoDrafts(this.history);
  /** The clip each draft last played into the game, made from the loop when it was played. */
  private draftClips = new Map<string, string>();
  private library: DemoVoices;
  private composer: DemoSentences;
  private permissions: Snapshot['permissions'] = {
    remotePlayback: true,
    guestCounter: false,
    guestSeeking: true,
    guestSpotify: true,
    guestUploads: true,
    guestTransforms: true,
    guestMimic: false,
    guestKeep: false,
    guestLibraryEdit: true,
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
  private policy: SessionPolicy = { ...DEFAULT_POLICY };
  private tickTimer?: ReturnType<typeof setTimeout>;
  private closed = false;

  constructor(
    private engine: DemoEngine,
    private hooks: DemoHooks,
    options: DemoOptions = {},
  ) {
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
    this.library = new DemoVoices(DemoModel.soundId, (id) => engine.duration(id), Date.now());
    this.composer = new DemoSentences(this.library);
  }

  /** The library's sound id for a sound key (tests and the engine use it). */
  static soundId(key: string) {
    const index = DEMO_SOUNDS.findIndex((s) => s.key === key);
    if (index < 0) throw Error(`No example sound ${key}.`);
    return soundIdFor(index + 1);
  }

  /** Shows the solo example. */
  begin() {
    this.publish();
    this.publishSession();
  }

  close() {
    this.closed = true;
    clearTimeout(this.tickTimer);
    this.engine.close();
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
            // A sentence or palette word is no library sound: its length is the audio made for it.
            durationMs:
              sound?.durationMs ?? (Math.round(this.engine.duration(playing.soundId)) || null),
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
      counterSession: this.counterSession,
      drafts: this.drafts.revision,
      budgets: 1,
      voiceLibrary: this.library.revision,
      sentences: this.composer.revision,
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
        recent: this.mimic.recent.slice(0, 20),
        moreRecent: this.mimic.recent.slice(20),
        history: this.history.snapshot(),
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
      guests: [],
      people: {},
      links: {},
      requests: [],
      policy: this.policy,
    });
  }

  private libraryChanged() {
    this.libraryRevision++;
  }

  // ---- Commands -----------------------------------------------------------------------------

  /** Runs an example command. Tests can supply a guest to exercise permission checks. */
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
        this.counterSession = Math.max(0, this.counterSession + (c.delta ?? 0));
        this.publish();
        return { counter: this.counter };
      case 'reset':
        this.hostOnly(host);
        this.counter = 0;
        this.counterSession = 0;
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
        this.history.start(Date.now());
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
        this.history.stop(Date.now());
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
      case 'mimicPreview': {
        this.hostOnly(host);
        // A 2.39 page names frames of the recording history, or (2.40) a draft; the older view its 30 s window.
        if (!c.draftId && c.startFrame === undefined)
          return this.engine.previewLoop(...this.selection(c));
        const range = c.draftId ? this.drafts.find(c.draftId) : this.historyRange(c);
        return this.engine.previewLoop(loopMs(range.startFrame), loopMs(range.endFrame));
      }
      // ---- The recording history (2.39) and drafts (2.40): see history.ts and drafts.ts.
      case 'historyTimeline':
        return this.history.timeline(Date.now(), this.mimic.transcribing);
      case 'historyLevels':
        this.sameRecording(c);
        return this.history.levels(this.frames(c), c.bins ?? 300, Date.now(), (from, to, n) =>
          this.engine.loopLevels(from, to, n),
        );
      case 'historyTranscript':
        this.sameRecording(c);
        return this.history.transcript(this.frames(c), Date.now(), this.mimic.transcribing);
      case 'historyCorrect':
        this.hostOnly(host);
        this.history.correct(c.wordId ?? 0, c.text ?? '', Date.now());
        return { corrected: true };
      case 'historySettings':
        this.hostOnly(host);
        this.history.settings(c.seconds, c.subtitles);
        this.publish();
        return undefined;
      case 'historyDiscard':
        this.hostOnly(host);
        return this.history.discard();
      case 'historyShare':
        this.hostOnly(host);
        throw Error(UNAVAILABLE.share);
      case 'historyUnshare':
        this.hostOnly(host);
        return undefined; // Nothing is ever shared in the example.
      case 'revokePeer':
        this.hostOnly(host);
        return undefined; // The solo example has no guest join time to end.
      case 'draftCreate':
        return this.draftCreate(c);
      case 'draftList':
        return this.drafts.list();
      case 'draftUpdate': {
        const draft = this.drafts.update(c, Date.now());
        this.publish();
        return draft;
      }
      case 'draftDiscard': {
        const result = this.drafts.discard(c.draftId);
        this.publish();
        return result;
      }
      case 'draftCopy':
        throw Error(UNAVAILABLE.share);
      case 'draftKeep':
        // Into the voice library is a permanent keep: it writes files on the host's PC.
        if (c.action === 'library') throw Error(UNAVAILABLE.keep);
        return this.draftKeep(c, by);
      case 'draftPlay':
        return this.draftPlay(c, by, !!guest);
      case 'budgetStatus':
        return this.budgetStatus(person, host);
      // ---- The voice library (2.41): see voices.ts.
      case 'libraryStatus':
        return this.library.status(host || this.permissions.guestLibraryEdit !== false);
      case 'libraryList':
        return this.library.list(c);
      case 'libraryGet':
        return this.library.getView(c);
      case 'librarySearch':
        return this.library.search(c);
      case 'libraryVocabulary':
        return this.library.vocabulary(c);
      case 'libraryAudit':
        return this.library.auditOf(c);
      case 'libraryEdit':
        return this.changeLibrary(() => this.library.edit(c, person.name));
      case 'libraryRemove':
        return this.changeLibrary(() => this.library.remove(c, person.name));
      case 'libraryUndo':
        return this.changeLibrary(() => this.library.undo(c, person.name));
      case 'libraryVoice':
        return this.changeLibrary(() => this.library.voice(c, person.name));
      case 'libraryWanted':
        return this.changeLibrary(() => this.library.want(c, person.name));
      case 'libraryRetry':
        this.library.get(c.recordingId);
        return { queued: false }; // Its words are hand-placed and always ready.
      case 'libraryPreview':
        return this.libraryPreview(c);
      case 'librarySettings':
        this.hostOnly(host);
        return this.changeLibrary(() => this.library.settings(c));
      case 'libraryAdd':
        throw Error(LIBRARY_UNAVAILABLE.add);
      case 'libraryIndex':
        throw Error(LIBRARY_UNAVAILABLE.index);
      case 'historySearch':
        this.sameRecording(c);
        return this.history.search(c.query ?? '', Date.now());
      case 'speakerGroups':
        return { enabled: false, groups: [], unassigned: 0 };
      case 'speakerName':
      case 'speakerLink':
        throw Error(LIBRARY_UNAVAILABLE.speakers);
      // ---- Sentences and the speaking palette (2.42): see sentences.ts.
      case 'sentenceList':
        return this.composer.list();
      case 'sentenceGet':
        return this.composer.view(this.composer.find(c.sentenceId));
      case 'sentenceAlternatives':
        return this.composer.alternativesView(c);
      case 'sentenceCreate':
        return this.changeSentences(() => this.composer.create(c.sentence, person.name));
      case 'sentenceText':
        return this.changeSentences(() => this.composer.text(c));
      case 'sentenceEdit':
        return this.changeSentences(() => this.composer.edit(c));
      case 'sentenceDiscard':
        return this.changeSentences(() => this.composer.discard(c));
      case 'sentenceKeep':
        // Kept for this example only: the version's audio is made again from its pieces when it plays.
        return this.changeSentences(() => this.composer.keep(c, person.name));
      case 'sentencePreview': {
        const { pieces, settings, sentence } = this.composer.piecesFor(c);
        const heard = await this.engine.previewMix(this.composer.render(pieces, settings), 30000);
        return { ...heard, revision: sentence.revision };
      }
      case 'sentencePlay':
        return this.sentencePlay(c, by, !!guest);
      case 'paletteSearch':
        return this.composer.palette(c);
      case 'paletteTap':
        return this.paletteTap(c, by, !!guest);
      case 'paletteMark':
        return this.changeSentences(() => this.composer.mark(c, person.name));
      case 'performance':
        return this.changeSentences(() => this.composer.perform(c, person.name));
      case 'performanceSets':
        return c.action
          ? this.changeSentences(() => this.composer.setsCommand(c))
          : this.composer.setsCommand(c);
      case 'keepSettings':
      case 'budgetGrant':
      case 'tempRecover':
        this.hostOnly(host);
        throw Error(UNAVAILABLE.other);
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
    const { source, name: original } = this.copySource(c);
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
        : `${original} (${suffix(edit)})`)
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
    const { source } = this.copySource(c);
    const edit = this.edit(c, this.engine.duration(source.id));
    return this.engine.previewClip(source.id, edit, 20000);
  }

  /**
   * What a copy is made from: a Temp asset, or (2.43, gate soundEdit) any sound on the board, as the companion
   * takes them. A Temp sound named by its sound id is copied from its asset; sentences and palette words are not
   * sounds to edit.
   */
  private copySource(c: Command) {
    if ((c.assetId === undefined) === (c.soundId === undefined))
      throw Error('Choose one source to copy.');
    if (c.assetId !== undefined) {
      const asset = this.asset(c.assetId);
      return { source: this.sound(asset.normal), name: asset.name };
    }
    const id = String(c.soundId);
    if (id.startsWith('sentence:') || id.startsWith('palette:'))
      throw Error(
        'Sentences and palette words are made from recordings; edit them in Sentences or Voices instead.',
      );
    const sound = this.sound(id);
    const asset = sound.assetId ? this.assets.find((a) => a.id === sound.assetId) : undefined;
    return asset
      ? { source: this.sound(asset.normal), name: asset.name }
      : { source: sound, name: sound.name };
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
    // A 2.39 page names the selection by frames of the recording history; the older 30 s view by its window.
    const [from, to] =
      c.startFrame !== undefined || c.draftId
        ? this.loopRange(c.draftId ? this.drafts.find(c.draftId) : this.historyRange(c))
        : this.selection(c);
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
    ].slice(0, 100);
    this.publish();
    if (!c.autoplay) return { assetId: asset.id, state: 'temporary' };
    if (mine !== this.intent)
      return { assetId: asset.id, state: 'temporary', ...superseded('newer') };
    if (guest && this.suspended)
      return { assetId: asset.id, state: 'temporary', ...superseded('suspended') };
    return { assetId: asset.id, state: 'temporary', ...this.start(id, 0, null, false, by, c.id) };
  }

  // ---- Recording history and drafts ---------------------------------------------------------

  /** The frames a history command asks about (levels, transcript). */
  private frames(c: Command): Range {
    const startFrame = Math.max(0, c.startFrame ?? 0);
    return { startFrame, endFrame: Math.max(startFrame, c.endFrame ?? startFrame) };
  }

  /** A history command about another recording than the current one is refused, as the companion does. */
  private sameRecording(c: Command) {
    if (c.sourceId && c.sourceId !== this.history.sessionId)
      throw Error('That is from another recording. Select the moment again.');
  }

  /** A selection on the history, checked: on this recording, within what can be seen, at most 30 s. */
  private historyRange(c: Command): Range {
    return this.history.check(
      { startFrame: c.startFrame ?? 0, endFrame: c.endFrame ?? 0 },
      c.sourceId,
      Date.now(),
    );
  }

  /** History frames as times on the endless loop, which is what the engine cuts clips from. */
  private loopRange(r: Range): [number, number] {
    return [loopMs(r.startFrame), loopMs(r.endFrame)];
  }

  private draftCreate(c: Command) {
    const draft = this.drafts.create(c, Date.now());
    this.publish();
    return draft;
  }

  /** Keep in Temp: the draft's audio becomes a Temp clip, like Mimic it; with `close` the draft goes. */
  private async draftKeep(c: Command, by: { id: string; name: string; source: 'host' | 'guest' }) {
    const draft = this.drafts.find(c.draftId);
    if (c.sourceId && c.sourceId !== draft.sessionId)
      throw Error('That draft is from another recording.');
    const [from, to] = this.loopRange(draft);
    const words = this.loopWords(from, to);
    const id = soundIdFor(this.nextSound++);
    const length = await this.engine.fromLoop(id, from, to);
    if (this.closed) throw Error('The example has ended.');
    const name = (draft.name?.trim() || words.map((w) => w.w).join(' ') || 'Mimic clip').slice(
      0,
      100,
    );
    const asset = this.addUpload(id, name, length, by, words);
    this.mimic.recent = [
      { assetId: asset.id, name, by: by.name, at: Date.now() },
      ...this.mimic.recent,
    ].slice(0, 100);
    const close = c.action === 'close';
    this.drafts.kept(draft, close, Date.now());
    this.publish();
    return { assetId: asset.id, draftId: draft.id, closed: close };
  }

  /**
   * Play into game: the draft's audio is cut from the loop and played like any sound, with a fresh epoch and
   * the website as its identity, so the player and the header follow it. Only the newest click plays.
   */
  private async draftPlay(
    c: Command,
    by: { id: string; name: string; source: 'host' | 'guest' },
    guest: boolean,
  ) {
    const draft = this.drafts.find(c.draftId);
    if (c.sourceId && c.sourceId !== draft.sessionId)
      throw Error('That draft is from another recording.');
    const mine = ++this.intent;
    const [from, to] = this.loopRange(draft);
    const id = soundIdFor(this.nextSound++);
    const length = await this.engine.fromLoop(id, from, to);
    if (this.closed) throw Error('The example has ended.');
    // The clip it played before goes, unless it is still the one playing.
    const before = this.draftClips.get(draft.id);
    if (before && this.playing?.soundId !== before) {
      this.engine.forget(before);
      this.sounds = this.sounds.filter((s) => s.id !== before);
    }
    this.draftClips.set(draft.id, id);
    this.sounds = [
      ...this.sounds,
      {
        id,
        name: (draft.name?.trim() || 'Mimic draft').slice(0, 100),
        icon: null,
        durationMs: Math.round(length),
        audioKind: 'generated',
        internalVariant: true,
        temporary: true,
        uploadedBy: by.name,
      },
    ];
    this.libraryChanged();
    this.drafts.touch(draft, Date.now());
    if (mine !== this.intent) {
      this.publish();
      return { draftId: draft.id, ...superseded('newer') };
    }
    if (guest && this.suspended) {
      this.publish();
      return { draftId: draft.id, ...superseded('suspended') };
    }
    return { draftId: draft.id, ...this.start(id, 0, null, false, by, c.id) };
  }

  // ---- Voice library and sentences ----------------------------------------------------------

  private changeLibrary<T>(change: () => T) {
    const result = change();
    this.publish();
    return result;
  }

  private changeSentences<T>(change: () => T) {
    const result = change();
    this.publish();
    return result;
  }

  /** A private preview of a recording or a sound, or a word of it (heard only in this browser). */
  private libraryPreview(c: Command) {
    const clip = c.recordingId ? this.library.get(c.recordingId).soundId : this.sound(c.soundId).id;
    const length = this.engine.duration(clip);
    const from = Math.max(0, Math.min(c.positionMs ?? 0, length));
    const to = Math.max(from + 1, Math.min(c.endMs ?? length, length));
    return this.engine.previewClip(clip, { fromMs: from, toMs: to, effect: 'none' }, 30000);
  }

  /**
   * Play into game: the sentence (or a kept version) is mixed from its pieces and played with a `sentence:` id, as
   * a real host does, so the player names the sentence. Only the newest click plays.
   */
  private async sentencePlay(
    c: Command,
    by: { id: string; name: string; source: 'host' | 'guest' },
    guest: boolean,
  ) {
    const mine = ++this.intent;
    const { pieces, settings, sentence } = this.composer.piecesFor(c);
    const parts = this.composer.render(pieces, settings);
    if (!parts.length) throw Error('There is nothing to play in this sentence yet.');
    const id = 'sentence:' + sentence.id;
    await this.engine.mix(id, parts);
    if (this.closed) throw Error('The example has ended.');
    if (mine !== this.intent) return superseded('newer');
    if (guest && this.suspended) return superseded('suspended');
    return { ...this.start(id, 0, null, false, by, c.id), detail: { revision: sentence.revision } };
  }

  /** A palette tap: into the game (a `palette:` id), or heard privately; noted in a performance being recorded. */
  private async paletteTap(
    c: Command,
    by: { id: string; name: string; source: 'host' | 'guest' },
    guest: boolean,
  ) {
    if (!c.sentence?.item) throw Error('Choose a palette item.');
    const { recording, startMs, endMs, label } = this.composer.tapped(c.sentence.item);
    const parts = [
      { clip: recording.soundId, atMs: 0, fromMs: startMs, toMs: endMs, fadeInMs: 3, fadeOutMs: 5 },
    ];
    if (c.action !== 'game')
      return { ...(await this.engine.previewMix(parts, 30000)), text: label };
    const mine = ++this.intent;
    const id = 'palette:' + recording.id;
    await this.engine.mix(id, parts);
    if (this.closed) throw Error('The example has ended.');
    if (mine !== this.intent) return superseded('newer');
    if (guest && this.suspended) return superseded('suspended');
    return { ...this.start(id, 0, null, false, by, c.id), detail: { text: label } };
  }

  /** Storage figures: fixed in the example, since nothing is kept on a disk. */
  private budgetStatus(person: Participant, host: boolean) {
    const usage = (p: Participant) => ({
      id: p.id,
      name: p.name,
      tempBytes: 0,
      tempLimitBytes: TEMP_MB * MB,
      keptBytes: 0,
      keptLimitBytes: KEPT_MB * MB,
      paused: false,
      waiting: null,
    });
    if (!host) return { guestKeep: false, own: usage(person), budgetMode: 'session' };
    return {
      settings: {
        guestKeep: this.permissions.guestKeep === true,
        destination: null,
        categoryId: null,
        categoryName: null,
        tempMb: TEMP_MB,
        keptMb: KEPT_MB,
        budgetMode: 'session',
      },
      guests: [],
      requests: [],
    };
  }

  // ---- Who is in the session ----------------------------------------------------------------

  getPolicy() {
    return this.policy;
  }

  setPolicy(policy: SessionPolicy) {
    this.policy = policy;
    this.publishSession();
  }

  // The controller exposes the same room interface, but the example has no guest peers.
  async letIn() {}
  turnAway() {}
  disconnect() {}

  rotate() {
    this.setPolicy(forNewInvite(this.policy));
  }
}
