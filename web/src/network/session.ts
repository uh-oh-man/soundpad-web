import { joinRoom, selfId, type JsonValue, type RequestAction } from 'trystero';
import { z } from 'zod';
import {
  commandSchema,
  controlOps,
  Disconnected,
  parseSnapshot,
  ackSchema,
  isGuestAllowed,
  RateLimit,
  type Command,
  type Snapshot,
  participantSchema,
  type Participant,
  slowOps,
} from '../protocol';
import { parseOrganization, type SharedLayout, type Organization } from '../shared-layout';
import { cleanName } from '../protocol';
import { APP_VERSION, siteRelaysDrafts, siteRelaysIntents, supports } from '../version';
import { sign, verify, type HostIdentity, type Invite } from './identity';
import {
  CHALLENGE_PATTERN,
  GUEST_KEY_PATTERN,
  guestIdOf,
  guestProof,
  loadGuestKey,
  newChallenge,
  signGuest,
  verifyGuest,
  type GuestKey,
} from './guest-key';
import type { RelayLink, RelayLinkState, SendOptions, SendResult } from './relay';
import {
  diagnose,
  judgeIce,
  NO_WEBRTC,
  probeIce,
  type Diagnosis,
  type IceReport,
  type IceVerdict,
} from './diagnostics';
import {
  bounded,
  DEFAULT_POLICY,
  effectivePermissions,
  HOLD_MS,
  LIST,
  MAX_EXTRA,
  MAX_PLACES,
  places,
  REQUEST_MS,
  type SessionPolicy,
} from './policy';

type Message = {
  v: 1;
  type: string;
  nonce?: string;
  signature?: string;
  state?: Snapshot;
  libraryVersion?: string;
  command?: Command;
  id?: string;
  ok?: boolean;
  error?: string;
  data?: JsonValue;
  person?: Participant;
  hostNow?: number;
  publicKey?: string;
  /** Website version of the sender's page (2.35), in hello and welcome. Older pages send none. */
  site?: string;
  /** Lobby (2.37): what the host decided about a guest who is not in yet. */
  lobby?: LobbyState;
  /** Lobby (2.37): in a "full" answer, whether the guest may ask for an extra place. */
  more?: boolean;
  /** Hello (2.37): the guest asks for an extra place in a full session. */
  extra?: boolean;
  /** Hello (2.37): the guest asks again after a request lapsed. */
  again?: boolean;
  /** Welcome (2.40): commands the host's page relays beyond what its version says, e.g. ['drafts']. */
  relays?: string[];
  /** Hello (2.41): the guest browser's public key (see guest-key.ts). */
  key?: string;
  /** Hello (2.41): the host's challenge the proof answers. Challenge message: a new one for this connection. */
  challenge?: string;
  /** Hello (2.41): the guest's signature over `guestProof` for this connection. */
  proof?: string;
};
/** Most guests one host session admits at once, unless the host grants extra places (2.37). */
export const MAX_GUESTS = MAX_PLACES;
/**
 * What the host tab allows one guest per second (2.35). Playback and transport commands have their
 * own generous budget, because two people alternating clicks is normal use. Queries and slow work
 * keep the moderate one. Going over either is answered with a refusal. Only a total no person could
 * produce, or a message that is not this protocol at all, disconnects the guest.
 */
export const GUEST_RATES = { control: 100, other: 12, abuse: 400 } as const;
/** A guest only listens to the host, whose acks and reports can come in bursts; this is a flood guard. */
const HOST_FLOOD = 1000;
/**
 * A host tab that loaded before 2.35 disconnects a guest who sends it 25 messages within a second. A
 * guest facing one stays under that by itself, leaving room for its hellos.
 */
const OLDER_HOST_RATE = 20;
/** Most playback commands one guest may have unanswered: a bound, not a limit anyone should meet. */
const MAX_PENDING_CONTROL = 256;
const OLDER_HOST_PAGE = 'The host’s page is older than yours. Ask the host to reload Soundpad Web.';
/** Relay peers (2.37) are named apart from WebRTC peers, so the two can never be confused. */
export const RELAY = 'relay:';
const isRelay = (peer: string) => peer.startsWith(RELAY);
/** Largest message a host accepts from a guest; layouts over the relay may be larger (below). */
const GUEST_MESSAGE_BYTES = 8192;
const LAYOUT_BYTES = 2 * 1024 * 1024;
/** How long a guest tries direct WebRTC before it also tries the relay (2.37). */
export const FALLBACK_MS = 12000;
/** Shown when a guest connected through the relay tries to upload (2.37). */
export const UPLOAD_RELAY =
  'Uploads need a direct connection to the host. This connection goes through a relay, which is too slow for audio files. Everything else works.';
/** Shown when the host answered but its welcome does not match this invitation (2.37). */
export const ENDED_INVITE = 'This invitation has ended. Ask the host for a new link.';
export type LobbyState = 'waiting' | 'full' | 'turnedAway' | 'expired';
const LOBBY_STATES: readonly LobbyState[] = ['waiting', 'full', 'turnedAway', 'expired'];
/** What a guest who is not in yet is shown (2.37). The first sentence is the header line. */
export const LOBBY_TEXT: Record<LobbyState, string> = {
  waiting: 'Waiting for the host to let you in',
  full: 'This session is full. You can ask the host for an extra place.',
  turnedAway:
    'The host didn’t let you in. That’s all right: if you think it was a mistake, ask them for a new invitation.',
  expired: 'The host didn’t answer in time. You can ask again.',
};
/** A guest waiting for the host's decision, as the host sees it (2.37). */
export type LobbyRequest = {
  id: string;
  name: string;
  kind: 'admit' | 'extra';
  at: number;
  expiresAt: number;
  /** 2.41: from a page that proved no key (older than 2.41): new to the host on every page load. */
  unverified?: boolean;
};
/** Makes a relay link for this session; `createRelayLink` in production, an in-memory hub in tests. */
export type RelayFactory = (options: {
  room: string;
  secret: string;
  selfId: string;
  role: 'host' | 'guest';
  maxReceiveBytes: number;
  maxPayloadBytes: number;
}) => RelayLink;
export type Transport = 'direct' | 'relay';
/** Everything the page shows about how this session is connected (2.37); see "Connection details". */
export type ConnectionInfo = {
  /** Guest: how it reaches the host right now. Host: undefined. */
  transport?: Transport;
  webrtc: boolean;
  startedAt: number;
  /** Guest: when it began looking for the host (page load, or the loss that started reconnecting). */
  since: number;
  connectedAt?: number;
  /** Guest: it was connected and lost the host; it keeps trying by itself. */
  reconnecting: boolean;
  /** Guest: just reached the host again after a loss (the welcome verified with this invitation). */
  reconnected?: boolean;
  /** Guest: the host answered with a welcome this invitation cannot verify. */
  ended?: boolean;
  lobby?: LobbyState;
  /** Guest: a "full" answer said an extra place may be asked for. */
  canAskExtra?: boolean;
  relay?: {
    state: RelayLinkState;
    startedAt: number;
    relays: { url: string; state: string; latencyMs?: number; error?: string }[];
    rttMs?: number;
  };
  ice?: IceReport;
  iceVerdict?: IceVerdict;
  diagnosis?: Diagnosis;
  /** Host: each guest's connection, by participant id. */
  links?: Record<string, { transport: Transport; rttMs?: number }>;
  /**
   * Guest (2.41): whether this browser keeps its guest key ('saved'), so the host knows it again after a reload,
   * or has one for this page only ('page'). Undefined until the key is ready.
   */
  guestKey?: 'saved' | 'page';
};
export type SessionOptions = {
  /** The relay link to use (2.37). Without one the session is WebRTC only, as up to 2.36. */
  relay?: RelayFactory;
  /** False when this browser has no WebRTC: a guest then uses only the relay. */
  direct?: boolean;
  /** Host: the admission and permission settings to start with (kept by a 2.37 companion). */
  policy?: SessionPolicy;
  policyChanged?: (policy: SessionPolicy) => void;
  lobby?: (requests: LobbyRequest[]) => void;
  connection?: (info: ConnectionInfo) => void;
  /** Gathers ICE candidates for the diagnosis; tests pass their own. */
  probe?: () => Promise<IceReport | undefined>;
  fallbackMs?: number;
  /** Guest (2.41): this browser's key; `loadGuestKey` by default, tests pass their own. */
  guestKey?: () => Promise<GuestKey>;
};
type Request = {
  participant: Participant;
  kind: 'admit' | 'extra';
  at: number;
  peers: Map<string, string>;
  verified: boolean;
};
/** Challenges and unverified identities a host keeps at most (old ones go first). */
const MAX_CHALLENGES = 512;
const remember = <K, V>(map: Map<K, V>, key: K, value: V) => {
  map.delete(key);
  map.set(key, value);
  while (map.size > MAX_CHALLENGES) map.delete(map.keys().next().value as K);
};
/** The part of a command needed to answer it, whether or not this page knows the rest. */
const envelopeSchema = z.object({ id: z.uuid(), op: z.string().max(40) });
/**
 * True when a command fails the check only because it names an op, field or choice this page has
 * never heard of: a guest on a newer website, not malformed traffic. Wrong types and missing parts
 * are malformed.
 */
const fromNewerPage = (error: z.ZodError) =>
  error.issues.every((i) => i.code === 'unrecognized_keys' || i.code === 'invalid_value');
const byteLength = (value: unknown) => new TextEncoder().encode(JSON.stringify(value)).byteLength;
export class Session {
  private layoutAction?: RequestAction<string, string>;
  private layouts = new Map<string, SharedLayout>();
  private layoutRates = new Map<string, RateLimit>();
  private uploadAction?: RequestAction<Uint8Array, string>;
  private uploading = new Set<string>();
  private waveSubscriptions = new Map<string, Set<string>>();
  private room?: ReturnType<typeof joinRoom>;
  private action?: {
    send: (data: Message, options: { target: string }) => Promise<unknown>;
    onMessage: ((data: Message, context: { peerId: string }) => void) | null;
  };
  /** The relay link (2.37): always open on a host, opened by a guest when direct does not work. */
  private link?: RelayLink;
  private linkStartedAt = 0;
  private accepted = new Set<string>();
  private people = new Map<string, Participant>();
  private sentRevision = new Map<string, number>();
  private denied = new Set<string>();
  private rates = new Map<string, { control: RateLimit; other: RateLimit; abuse: RateLimit }>();
  /** Host: full snapshots still on their way to a relay guest; ticks wait for them (2.37). */
  private heavy = new Map<string, number>();
  /** Host: lobby and capacity (2.37). */
  private policy: SessionPolicy;
  private requests = new Map<string, Request>();
  /**
   * Host: participants whose welcome is being signed (2.37 review). They count as seated, so two quick
   * decisions (two hellos at once, or "Let guests in at once" letting the waiting list in) never give
   * away the same place twice.
   */
  private joining = new Set<string>();
  /** Host: relay peers with a welcome still on its way, by the hello nonce it answers (2.37 review). */
  private welcoming = new Map<string, string>();
  private lapsed = new Map<string, number>();
  /** When each participant's last connection left, so a reload keeps their place for a while. */
  private left = new Map<string, number>();
  /**
   * Host: when each participant was first let in by this page (2.39). Relayed with their commands so the
   * companion shows them the recording history from then on; a reconnect never moves it. The host's Disconnect
   * or Turn away ends it, so letting them back in counts from then (the companion forgets its copy too).
   */
  private joined = new Map<string, number>();
  /** Participants for peers whose hello named nobody (pages older than names), stable per peer. */
  private guessed = new Map<string, Participant>();
  /** Host (2.41): the challenge each connection's guest signs to prove their key. */
  private challenges = new Map<string, string>();
  /** Host (2.41): identities given to guests that proved no key, by their page load (profile id and nonce). */
  private unverifiedIds = new Map<string, string>();
  /** Host (2.41): participant ids that are not backed by a key. */
  private unverified = new Set<string>();
  /** Guest (2.41): this browser's key, once loaded, and each host connection's challenge and proof. */
  private key?: Promise<GuestKey | undefined>;
  private keyKind?: 'saved' | 'page';
  private hostChallenges = new Map<string, { challenge: string; proof?: Promise<string> }>();
  /** Guest: the verified host connection in use, and every verified one (direct and relay). */
  private hostPeer?: string;
  private verified = new Set<string>();
  private nonce = crypto.randomUUID();
  private pending = new Map<
    string,
    {
      resolve: (data: unknown) => void;
      reject: (e: Error) => void;
      timer: ReturnType<typeof setTimeout>;
      control: boolean;
    }
  >();
  /** Unanswered commands other than playback and transport ones; only these count toward the cap. */
  private waiting = 0;
  private timer: ReturnType<typeof setInterval>;
  private lastState = 0;
  private lastRevision = -1;
  private started = Date.now();
  private stopped = false;
  private revoked = false;
  private sentVersion = new Map<string, string>();
  private peerId: string;
  private hostOffset = 0;
  private offsets: number[] = [];
  private welcomedSite = '';
  /** The host page said it relays the 2.40 commands (drafts, guest preview and Keep, budgets). */
  private welcomedDrafts = false;
  private olderHostRate = new RateLimit(OLDER_HOST_RATE);
  // Guest connection state (2.37).
  private since = Date.now();
  private connectedAt?: number;
  private everConnected = false;
  private answered = false;
  private ended = false;
  private lobbyState?: LobbyState;
  private canAskExtra = false;
  private askExtra = false;
  private askAgainNext = false;
  private ice?: IceReport;
  private probing = false;
  private lastDirectHello = 0;
  private direct: boolean;
  private fallbackMs: number;
  /** Website version the host's tab reported in its welcome ('' from a tab older than 2.35). */
  get hostSite() {
    return this.hostPeer ? this.welcomedSite : '';
  }
  /**
   * Guest: whether the host's page relays the 2.40 commands. An older page refuses them, so they are
   * offered only when its welcome says so: by its version, or by naming them (a page whose version
   * was not bumped yet, such as a development build, still knows its own commands).
   */
  get hostRelaysDrafts() {
    return !!this.hostPeer && this.welcomedDrafts;
  }
  /** Guest: how this page reaches the host now (2.37). */
  get transport(): Transport | undefined {
    return this.hostPeer ? (isRelay(this.hostPeer) ? 'relay' : 'direct') : undefined;
  }
  constructor(
    private identity: Invite,
    private host: HostIdentity | undefined,
    private current: () => Snapshot,
    private execute: (
      c: Command,
      peer: string,
      person?: Participant,
      joinedAt?: number,
    ) => Promise<unknown>,
    private receive: (s: Snapshot) => void,
    private status: (s: string) => void,
    /** `unverified` (2.41): the listed peers whose guest proved no key. */
    private guests: (ids: string[], links: Record<string, Transport>, unverified: string[]) => void,
    transport = { join: joinRoom, id: selfId },
    private profiles?: {
      get: () => Participant;
      changed: (people: Record<string, Participant>) => void;
    },
    private extensions?: {
      layouts?: (layouts: SharedLayout[]) => void;
      waveform?: (data: unknown) => void;
      /** `guest` is the uploader's participant id (2.40), whose storage budget the upload counts against. */
      upload: (
        bytes: Uint8Array,
        name: string,
        by: string,
        peer: string,
        guest?: string,
      ) => Promise<unknown>;
    },
    /** This page's website version, told to the other side; tests pass their own. */
    private site: string = APP_VERSION,
    private options: SessionOptions = {},
  ) {
    this.peerId = transport.id;
    this.policy = { ...DEFAULT_POLICY, ...options.policy };
    this.direct = options.direct !== false || !!host;
    this.fallbackMs = options.fallbackMs ?? FALLBACK_MS;
    if (!host) {
      // Every hello waits for the key, so a new host never mistakes this page for one without a key.
      this.key = (options.guestKey ?? loadGuestKey)().then(
        (key) => {
          this.keyKind = key.durable ? 'saved' : 'page';
          this.report();
          return key;
        },
        () => undefined,
      );
    }
    if (this.direct) this.joinDirect(transport);
    this.timer = setInterval(() => this.tick(), 2500);
    // A host always listens on the relay too, so a guest whose network blocks WebRTC still gets in.
    // A guest without WebRTC has nothing else to try.
    if (host || !this.direct) this.startRelay();
    if (!host && !this.direct) this.status(NO_WEBRTC);
    queueMicrotask(() => this.report());
  }
  private joinDirect(transport: { join: typeof joinRoom; id: string }) {
    const host = this.host;
    let turnConfig: RTCIceServer[] | undefined;
    try {
      const configured = import.meta.env.VITE_TURN_CONFIG;
      if (configured) turnConfig = JSON.parse(configured);
    } catch {
      this.status('Invalid optional TURN configuration.');
    }
    const room = transport.join(
      {
        appId: 'org.soundpad-web.v1',
        password: this.identity.secret,
        maxReceiveBytes: 16 * 1024 * 1024,
        turnConfig,
      },
      this.identity.room,
      {
        onJoinError: () => {
          if (host || this.hostPeer) return;
          // Signalling worked but WebRTC could not connect (2.37): the relay is the way in.
          this.startRelay();
          if (!this.options.relay)
            this.status(
              'Connection failed. Check the invite and network; restrictive NAT may need your own TURN configuration.',
            );
        },
      },
    );
    this.room = room;
    if (this.extensions) {
      const extensions = this.extensions;
      this.layoutAction = room.makeAction<string, string>('organization', {
        kind: 'request',
        onReceive: ({ peerId, byteLength, kind }) =>
          kind === 'response'
            ? !this.host && this.verified.has(peerId) && byteLength <= 1024
            : !!this.host &&
              this.accepted.has(peerId) &&
              !this.denied.has(peerId) &&
              byteLength <= LAYOUT_BYTES,
        onRequest: (text, { peerId }) => {
          this.layoutFrom(peerId, text);
          return 'ok';
        },
      });
      this.uploadAction = room.makeAction<Uint8Array, string>('audio-upload', {
        kind: 'request',
        onReceive: ({ peerId, byteLength, kind }) =>
          kind === 'response'
            ? !this.host && this.verified.has(peerId) && byteLength <= 1024
            : !!this.host &&
              this.accepted.has(peerId) &&
              !this.denied.has(peerId) &&
              byteLength <= 16 * 1024 * 1024 &&
              this.uploading.size < 2 &&
              !this.uploading.has(peerId) &&
              this.permissionsOf(peerId).guestUploads !== false,
        onRequest: async (bytes, { peerId, metadata }) => {
          if (
            !this.accepted.has(peerId) ||
            this.denied.has(peerId) ||
            this.uploading.size >= 2 ||
            this.uploading.has(peerId) ||
            // The host page is where a guest's own permissions are enforced (2.37); see `permissionsOf`.
            this.permissionsOf(peerId).guestUploads === false
          )
            throw Error('Upload unavailable.');
          const name =
            typeof metadata === 'string' && metadata.length <= 256 ? metadata : 'audio.wav';
          this.uploading.add(peerId);
          try {
            const result = await extensions.upload(
              bytes,
              name,
              this.people.get(peerId)?.name ?? 'Guest',
              peerId,
              this.people.get(peerId)?.id,
            );
            this.broadcast();
            return JSON.stringify(result ?? {});
          } finally {
            this.uploading.delete(peerId);
          }
        },
      });
    }
    const action = room.makeAction<Message>('protocol', {
      onReceive: ({ byteLength, peerId }) =>
        byteLength <= (host ? GUEST_MESSAGE_BYTES : 16 * 1024 * 1024) && !this.denied.has(peerId),
    });
    this.action = action;
    action.onMessage = (data, { peerId }) => {
      void this.handle(data, peerId).catch(() => this.drop(peerId));
    };
    room.onPeerJoin = (peer) => {
      if (this.denied.has(peer)) {
        room.getPeers()[peer]?.close();
        return;
      }
      if (!host) this.sendHello(peer);
    };
    room.onPeerLeave = (peer) => this.peerLeft(peer);
  }
  /** Opens the relay link (2.37). A host listens; a guest announces itself until the host answers. */
  private startRelay() {
    const factory = this.options.relay;
    if (!factory || this.link || this.stopped || this.revoked) return;
    const host = !!this.host;
    const link = factory({
      room: this.identity.room,
      secret: this.identity.secret,
      selfId: this.peerId,
      role: host ? 'host' : 'guest',
      // A guest's messages are small, but a shared layout (2 MiB) has no other way over the relay.
      maxReceiveBytes: host ? LAYOUT_BYTES + 65536 : 16 * 1024 * 1024,
      maxPayloadBytes: host ? 16 * 1024 * 1024 : LAYOUT_BYTES + 65536,
    });
    this.link = link;
    this.linkStartedAt = Date.now();
    link.onMessage((from, value) => {
      if (this.link !== link || value instanceof Uint8Array) return;
      const peer = RELAY + from;
      if (this.denied.has(peer)) return;
      // The same bound as the WebRTC path; only a layout may be larger.
      if (
        host &&
        (value as { type?: unknown } | null)?.type !== 'layout' &&
        byteLength(value) > GUEST_MESSAGE_BYTES
      )
        return;
      void this.handle(value, peer).catch(() => this.drop(peer));
    });
    link.onPeer((id, event) => {
      if (this.link !== link) return;
      const peer = RELAY + id;
      if (event === 'lost') this.peerLeft(peer);
      else if (!host && !this.denied.has(peer)) this.sendHello(peer);
    });
    link.onStatus(() => {
      if (this.link !== link) return;
      // A relay verdict (none reachable, say) is shown at once, not at the next 2.5 s tick.
      if (!host && !this.hostPeer && !this.revoked) this.showLooking(Date.now());
      this.report();
    });
    void link.start().catch(() => {});
    if (!host) link.broadcastHello();
    this.report();
  }
  private stopRelay() {
    const link = this.link;
    if (!link) return;
    this.link = undefined;
    for (const peer of [...this.verified]) if (isRelay(peer)) this.verified.delete(peer);
    void link.stop().catch(() => {});
  }
  private peerLeft(peer: string) {
    const person = this.people.get(peer);
    const wasAccepted = this.accepted.delete(peer);
    this.people.delete(peer);
    this.profiles?.changed(Object.fromEntries(this.people));
    this.sentRevision.delete(peer);
    this.sentVersion.delete(peer);
    this.rates.delete(peer);
    this.heavy.delete(peer);
    this.welcoming.delete(peer);
    this.waveSubscriptions.delete(peer);
    this.challenges.delete(peer);
    this.hostChallenges.delete(peer);
    for (const request of this.requests.values()) request.peers.delete(peer);
    if (this.host && wasAccepted && person && !this.peersOf(person.id).length)
      this.left.set(person.id, Date.now());
    if (this.host) this.notifyGuests();
    else this.lose(peer);
  }
  /** Guest: a host connection went away. Another verified one takes over without a gap (2.37). */
  private lose(peer: string) {
    this.verified.delete(peer);
    if (peer !== this.hostPeer) return;
    const other = [...this.verified].sort((a, b) => +isRelay(a) - +isRelay(b))[0];
    if (other) {
      this.hostPeer = other;
      this.report();
      return;
    }
    this.hostPeer = undefined;
    this.lastRevision = -1;
    this.since = Date.now();
    this.answered = false;
    this.connectedAt = undefined;
    this.status('Reconnecting');
    this.rejectPending();
    // Both ways back at once: the relay costs little, and direct keeps being retried as well.
    this.startRelay();
    this.link?.broadcastHello();
    this.report();
  }
  private tick() {
    if (this.stopped || this.revoked) return;
    const now = Date.now();
    if (this.host) {
      this.expireRequests(now);
      for (const [id, at] of this.left) if (now - at > HOLD_MS) this.left.delete(id);
      for (const [id, at] of this.lapsed) if (now - at > 10 * REQUEST_MS) this.lapsed.delete(id);
      // The "unverified" label is only needed while that guest is in, asking or holding a place.
      const members = this.members();
      for (const id of this.unverified)
        if (
          !members.has(id) &&
          !this.requests.has(id) &&
          !this.left.has(id) &&
          !this.joining.has(id)
        )
          this.unverified.delete(id);
      this.broadcast(true);
      return;
    }
    if (this.hostPeer) {
      // The relay is slower and a full report can take a while to cross it; it also tells of a lost
      // peer by itself, so it gets longer before silence counts as a loss.
      if (now - this.lastState > (isRelay(this.hostPeer) ? 30000 : 10000)) {
        const peer = this.hostPeer;
        this.verified.clear();
        this.verified.add(peer);
        this.lose(peer);
      } else if (isRelay(this.hostPeer) && this.room && now - this.lastDirectHello >= 10000) {
        // Connected through the relay: keep trying direct now and then, and switch when it works.
        this.lastDirectHello = now;
        for (const peer of Object.keys(this.room.getPeers()))
          if (!this.verified.has(peer)) this.sendHello(peer);
      }
    }
    if (!this.hostPeer) {
      if (this.lobbyState !== 'turnedAway') this.helloAll();
      if (!this.link && now - this.since >= this.fallbackMs) this.startRelay();
      if (this.direct && !this.ice && !this.probing && now - this.since >= this.fallbackMs)
        void this.runProbe();
      this.showLooking(now);
    }
    this.report();
  }
  /** Guest: says what is happening while it looks for the host. */
  private showLooking(now: number) {
    if (this.lobbyState) return;
    if (this.ended) {
      this.status(ENDED_INVITE);
      return;
    }
    if (this.everConnected) {
      this.status('Reconnecting');
      return;
    }
    const diagnosis = this.diagnosis(now);
    if (diagnosis) this.status(diagnosis.text);
    else if (!this.direct) this.status(NO_WEBRTC);
    else if (now - this.started > 25000)
      this.status(
        'Host unavailable. Ask the host to keep Soundpad Web open (its window or host tab), check the invite, or try another network. Public relays and direct WebRTC can be blocked.',
      );
    else if (this.link) this.status('Connecting through a relay');
  }
  private diagnosis(now = Date.now()) {
    if (this.host || this.hostPeer) return undefined;
    const status = this.link?.status();
    return diagnose({
      webrtc: this.direct,
      relayState: status?.state,
      relays: status?.relays ?? [],
      relaysChecked: !!status && ['ready', 'degraded', 'failed'].includes(status.state),
      ice: this.ice ? judgeIce(this.ice) : undefined,
      elapsedMs: now - this.since,
      answered: this.answered,
    });
  }
  private async runProbe() {
    this.probing = true;
    try {
      this.ice = await (this.options.probe ?? (() => probeIce()))();
    } catch {
      this.ice = undefined;
    } finally {
      this.probing = false;
    }
    if (!this.stopped) this.report();
  }
  /** Runs the network check again (2.37: "Check again" in Connection details). */
  async checkNetwork() {
    if (this.probing) return;
    await this.runProbe();
  }
  /** Tells the page how this session is connected (2.37). */
  private reported = '';
  private report(reconnected = false) {
    if (this.stopped || !this.options.connection) return;
    const relay = this.link?.status();
    const info: ConnectionInfo = {
      transport: this.transport,
      webrtc: this.direct,
      startedAt: this.started,
      since: this.since,
      connectedAt: this.connectedAt,
      reconnecting: !this.host && !this.hostPeer && this.everConnected && !this.revoked,
      ended: this.ended || undefined,
      lobby: this.hostPeer ? undefined : this.lobbyState,
      canAskExtra: this.canAskExtra || undefined,
      relay: relay && {
        state: relay.state,
        startedAt: this.linkStartedAt,
        relays: relay.relays.map((r) => ({
          url: r.url,
          state: r.state,
          latencyMs: r.latencyMs,
          error: r.error,
        })),
        rttMs: relay.rttMs,
      },
      ice: this.ice,
      iceVerdict: this.ice ? judgeIce(this.ice) : undefined,
      diagnosis: this.diagnosis(),
      reconnected: reconnected || undefined,
      guestKey: this.keyKind,
    };
    if (this.host) {
      const links: NonNullable<ConnectionInfo['links']> = {};
      const rtt = new Map(relay?.peers.map((p) => [RELAY + p.id, p.rttMs]) ?? []);
      for (const [id, peers] of this.members()) {
        const direct = peers.find((p) => !isRelay(p));
        links[id] = direct
          ? { transport: 'direct' }
          : { transport: 'relay', rttMs: rtt.get(peers[0]) };
      }
      info.links = links;
    }
    // The guest timer reports every 2.5 s; the page only hears of a change.
    const text = JSON.stringify(info);
    if (text === this.reported) return;
    this.reported = text;
    this.options.connection(info);
  }
  /** Sends a message; over the relay, the promise says how its sending ended (direct: undefined). */
  private send(
    data: Message,
    peer: string,
    options?: SendOptions,
  ): Promise<SendResult | undefined> {
    if (this.stopped) return Promise.resolve(undefined);
    if (!isRelay(peer)) {
      void this.action?.send(data, { target: peer }).catch(() => {});
      return Promise.resolve(undefined);
    }
    const link = this.link;
    if (!link) return Promise.resolve(undefined);
    // A full report or welcome can take seconds to cross the relay; ticks wait for it (see broadcast).
    const heavy = data.type === 'welcome' || data.type === 'state';
    if (heavy) this.heavy.set(peer, (this.heavy.get(peer) ?? 0) + 1);
    return link
      .send(peer.slice(RELAY.length), data as unknown as JsonValue, options)
      .then((result) => {
        if (!heavy) return result;
        const left = (this.heavy.get(peer) ?? 1) - 1;
        if (left > 0) this.heavy.set(peer, left);
        else this.heavy.delete(peer);
        // A lost library makes the next report a full one again.
        if (result === 'failed') this.sentVersion.delete(peer);
        if (!left) this.broadcast();
        return result;
      })
      .catch(() => undefined);
  }
  /**
   * Guest: says hello to a peer that may be the host. Since 2.41 every hello carries this browser's public key and,
   * once that connection's host has sent its challenge, the proof (see guest-key.ts). A host older than 2.41
   * ignores both.
   */
  private sendHello(peer: string) {
    const ready = this.key ?? Promise.resolve(undefined);
    void ready
      .then(async (key) => {
        const asked = this.hostChallenges.get(peer);
        let proof: string | undefined;
        if (key && asked) {
          const { host, guest } = this.proof(peer);
          asked.proof ??= signGuest(
            key,
            guestProof(this.identity.room, this.nonce, asked.challenge, host, guest),
          );
          proof = await asked.proof;
        }
        if (this.stopped) return;
        this.send(
          {
            v: 1,
            type: 'hello',
            nonce: this.nonce,
            person: this.profiles?.get(),
            site: this.site,
            ...(this.askExtra ? { extra: true } : {}),
            ...(this.askAgainNext ? { again: true } : {}),
            ...(key && { key: key.publicKey }),
            ...(asked && proof && { challenge: asked.challenge, proof }),
          },
          peer,
        );
      })
      .catch(() => {});
  }
  /** Guest: hello to every peer that could be the host, over both transports. */
  private helloAll() {
    for (const peer of Object.keys(this.room?.getPeers() ?? {})) this.sendHello(peer);
    for (const peer of this.link?.status().peers ?? []) this.sendHello(RELAY + peer.id);
  }
  /** Guest: asks for an extra place in a full session (2.37). */
  askForPlace() {
    if (this.host || this.hostPeer) return;
    this.askExtra = true;
    this.lobbyState = 'waiting';
    this.status(LOBBY_TEXT.waiting);
    this.helloAll();
    this.report();
  }
  /** Guest: asks again after the host did not answer in time (2.37). */
  askAgain() {
    if (this.host || this.hostPeer) return;
    this.askAgainNext = true;
    this.lobbyState = 'waiting';
    this.status(LOBBY_TEXT.waiting);
    this.helloAll();
    this.report();
  }
  /**
   * Keeps the guest's idea of the host's clock current (2.35). It was measured once, at the welcome,
   * so a later step of the guest's clock made every play look expired. Each sample is the true
   * difference minus its travel time, so the largest recent one is the closest; a jump of seconds
   * means a clock was changed, and the estimate starts again from there.
   */
  private syncClock(hostNow: unknown, restart = false) {
    if (typeof hostNow !== 'number' || !Number.isFinite(hostNow)) return;
    const sample = hostNow - Date.now();
    if (restart || Math.abs(sample - this.hostOffset) > 2000) this.offsets = [];
    this.offsets = [...this.offsets.slice(-7), sample];
    this.hostOffset = Math.max(...this.offsets);
  }
  rename() {
    if (this.hostPeer) this.sendHello(this.hostPeer);
  }
  private drop(peer: string) {
    const person = this.people.get(peer);
    this.denied.add(peer);
    this.accepted.delete(peer);
    this.people.delete(peer);
    this.profiles?.changed(Object.fromEntries(this.people));
    this.rates.delete(peer);
    this.welcoming.delete(peer);
    this.challenges.delete(peer);
    if (!isRelay(peer)) this.room?.getPeers()[peer]?.close();
    if (person && !this.peersOf(person.id).length) this.left.delete(person.id);
    if (this.host) this.notifyGuests();
    else this.lose(peer);
  }
  /** The ids this page and a peer use in a welcome's proof; relay peers are named as such on both sides. */
  private proof(peer: string) {
    return isRelay(peer)
      ? {
          host: this.host ? RELAY + this.peerId : peer,
          guest: this.host ? peer : RELAY + this.peerId,
        }
      : { host: this.host ? this.peerId : peer, guest: this.host ? peer : this.peerId };
  }
  // ---- host: who is in ------------------------------------------------------------------------
  /** Connected participants and their connections (one guest can be on both transports at once). */
  private members() {
    const members = new Map<string, string[]>();
    for (const peer of this.accepted) {
      const id = this.people.get(peer)?.id;
      if (!id) continue;
      members.set(id, [...(members.get(id) ?? []), peer]);
    }
    return members;
  }
  private peersOf(id: string) {
    return [...this.accepted].filter((peer) => this.people.get(peer)?.id === id);
  }
  /** Places taken by others: connected guests, and those who left a moment ago (a reload). */
  private seats(except?: string) {
    const ids = new Set(this.members().keys());
    for (const id of this.left.keys()) ids.add(id);
    for (const id of this.joining) ids.add(id);
    ids.delete(except ?? '');
    return ids.size;
  }
  private notifyGuests() {
    const ids: string[] = [];
    const links: Record<string, Transport> = {};
    const unverified: string[] = [];
    for (const [id, peers] of this.members()) {
      // One row per guest: their direct connection when they have one.
      const peer = peers.find((p) => !isRelay(p)) ?? peers[0];
      ids.push(peer);
      links[peer] = isRelay(peer) ? 'relay' : 'direct';
      if (this.unverified.has(id)) unverified.push(peer);
    }
    this.guests(ids, links, unverified);
    this.report();
  }
  /**
   * Host (2.41): who a hello from a connection that is not in yet comes from. A key with a valid proof over this
   * connection's challenge is that key's identity, whatever name or profile id the hello also carries. A key
   * without a valid proof is answered with the challenge (undefined: nothing to decide yet). No key at all is a
   * page older than 2.41: an unverified guest, new to the host on every page load, so nothing granted to one
   * identity is ever reused by another that merely claims it.
   */
  private async identify(
    peer: string,
    m: Message,
    person: Participant,
  ): Promise<{ id: string; verified: boolean } | undefined> {
    if (m.key !== undefined) {
      if (typeof m.key !== 'string' || !GUEST_KEY_PATTERN.test(m.key)) throw Error('Invalid key');
      let challenge = this.challenges.get(peer);
      if (
        challenge &&
        m.challenge === challenge &&
        typeof m.proof === 'string' &&
        m.proof.length <= 200
      ) {
        const { host, guest } = this.proof(peer);
        const proof = guestProof(this.identity.room, m.nonce!, challenge, host, guest);
        if (await verifyGuest(m.key, m.proof, proof))
          return { id: await guestIdOf(m.key), verified: true };
      }
      if (!challenge) {
        challenge = newChallenge();
        remember(this.challenges, peer, challenge);
      }
      this.send({ v: 1, type: 'challenge', nonce: m.nonce, challenge }, peer);
      return undefined;
    }
    const load = `${person.id}:${m.nonce}`;
    let id = this.unverifiedIds.get(load);
    if (!id) {
      id = crypto.randomUUID();
      remember(this.unverifiedIds, load, id);
    }
    return { id, verified: false };
  }
  /** This guest's permissions: the host's override for them where set, the session's otherwise. */
  private permissionsOf(peer: string) {
    const state = this.current();
    const id = this.people.get(peer)?.id;
    return effectivePermissions(state.permissions, id ? this.policy.overrides[id] : undefined);
  }
  /** The snapshot as one guest should see it: their own permissions, so their page shows the right controls. */
  private stateFor(peer: string, state: Snapshot) {
    const id = this.people.get(peer)?.id;
    const override = id ? this.policy.overrides[id] : undefined;
    // How much history a crash recovered, and the host's disk problems, are the host's (2.39): they describe
    // recording from before any guest joined.
    const history = state.mimic?.history;
    const base =
      history && (history.recoveredMs || history.problem)
        ? {
            ...state,
            mimic: { ...state.mimic!, history: { ...history, recoveredMs: 0, problem: null } },
          }
        : state;
    return override
      ? { ...base, permissions: effectivePermissions(base.permissions, override) }
      : base;
  }
  private participantOf(peer: string, raw: unknown) {
    const person = participantSchema.safeParse(raw);
    if (person.success) return person.data;
    const known = this.people.get(peer) ?? this.guessed.get(peer);
    if (known) return known;
    const guess = { id: crypto.randomUUID(), name: 'Guest' };
    if (this.guessed.size > 256) this.guessed.clear();
    this.guessed.set(peer, guess);
    return guess;
  }
  /** Host: decides about a hello from a peer that is not in yet (2.37). */
  private admit(
    peer: string,
    nonce: string,
    participant: Participant,
    m: Message,
    verified: boolean,
  ) {
    const id = participant.id;
    const policy = this.policy;
    if (policy.turnedAway.some((p) => p.id === id)) return 'turnedAway' as const;
    // Already in on another connection, or just reloaded: their place is theirs.
    if (this.members().has(id) || this.left.has(id)) return 'in' as const;
    const request = this.requests.get(id);
    if (request) {
      request.peers.set(peer, nonce);
      request.participant = participant;
      if (m.extra && request.kind === 'admit' && this.seats(id) >= places(policy))
        request.kind = 'extra';
      this.lobbyChanged();
      return 'waiting' as const;
    }
    if (this.lapsed.has(id)) {
      if (!m.again) return 'expired' as const;
      this.lapsed.delete(id);
    }
    const full = this.seats(id) >= places(policy);
    const ask = (kind: Request['kind']) => {
      // One connection holds one request (2.37 review): a hello with another id replaces its earlier
      // request instead of adding to the list, and the list never grows past what views can show.
      for (const [other, r] of this.requests)
        if (r.peers.delete(peer) && !r.peers.size) this.requests.delete(other);
      if (this.requests.size >= LIST) {
        this.lobbyChanged();
        return 'full' as const;
      }
      this.requests.set(id, {
        participant,
        kind,
        at: Date.now(),
        peers: new Map([[peer, nonce]]),
        verified,
      });
      this.lobbyChanged();
      return 'waiting' as const;
    };
    if (full) return m.extra && this.canGrow() ? ask('extra') : ('full' as const);
    if (policy.admission === 'ask' && !policy.admitted.includes(id)) return ask('admit');
    return 'in' as const;
  }
  private canGrow() {
    return places(this.policy) < MAX_PLACES + MAX_EXTRA && this.policy.extra < MAX_EXTRA;
  }
  private async welcome(peer: string, nonce: string, participant: Participant) {
    const host = this.host!;
    const { host: hostId, guest } = this.proof(peer);
    // The place is taken in the same step as the decision; see `joining`.
    this.joining.add(participant.id);
    let signature: string;
    try {
      signature = await sign(host, nonce, hostId, guest);
    } finally {
      this.joining.delete(participant.id);
    }
    if (this.denied.has(peer) || this.stopped) return;
    this.accepted.add(peer);
    this.people.set(peer, participant);
    this.left.delete(participant.id);
    if (!this.joined.has(participant.id)) this.joined.set(participant.id, Date.now());
    if (this.requests.delete(participant.id)) this.lobbyChanged();
    if (!this.policy.admitted.includes(participant.id))
      this.setPolicy(
        { ...this.policy, admitted: bounded([...this.policy.admitted, participant.id]) },
        false,
      );
    this.profiles?.changed(Object.fromEntries(this.people));
    this.notifyGuests();
    // A relay guest says hello every 2.5 s until a welcome has arrived, and a large one takes longer
    // than that to cross the relay. Sending it again would cancel the one on its way, every time, so
    // the guest never got in (2.37 review). When it has arrived, broadcast() brings the guest up to date.
    if (isRelay(peer) && this.welcoming.get(peer) === nonce) return;
    if (isRelay(peer)) this.welcoming.set(peer, nonce);
    const state = this.current();
    void this.send(
      {
        v: 1,
        type: 'welcome',
        nonce,
        signature,
        publicKey: host.publicKey,
        state: this.stateFor(peer, state),
        hostNow: Date.now(),
        site: this.site,
        // 2.40: what this page relays beyond what its version says (see hostRelaysDrafts).
        relays: ['drafts'],
        data: [...this.layouts.values()] as unknown as JsonValue,
      },
      peer,
      { supersedeKey: 'welcome' },
    ).then(() => {
      // Whatever the outcome (sent, superseded by a reload's hello, failed), the next hello may ask again.
      if (this.welcoming.get(peer) === nonce) this.welcoming.delete(peer);
    });
    this.sentVersion.set(peer, state.library.version);
  }
  /**
   * A lobby answer is signed like a welcome (over the guest's nonce, tagged with the answer), so
   * another holder of the invitation cannot tell a guest it was turned away.
   */
  private async tellLobby(peer: string, nonce: string, lobby: LobbyState) {
    const { host: hostId, guest } = this.proof(peer);
    const signature = await sign(this.host!, `lobby:${lobby}:${nonce}`, hostId, guest);
    if (this.stopped) return;
    this.send(
      {
        v: 1,
        type: 'lobby',
        nonce,
        lobby,
        signature,
        publicKey: this.host!.publicKey,
        ...(lobby === 'full' ? { more: this.canGrow() } : {}),
      },
      peer,
    );
  }
  private lobbyChanged() {
    this.options.lobby?.(this.lobby());
  }
  /** Host: guests waiting for a decision, oldest first. */
  lobby(): LobbyRequest[] {
    return [...this.requests.entries()]
      .map(([id, r]) => ({
        id,
        name: r.participant.name,
        kind: r.kind,
        at: r.at,
        expiresAt: r.at + REQUEST_MS,
        ...(!r.verified && { unverified: true }),
      }))
      .sort((a, b) => a.at - b.at);
  }
  private expireRequests(now: number) {
    let changed = false;
    for (const [id, request] of this.requests) {
      if (now - request.at < REQUEST_MS) continue;
      this.requests.delete(id);
      this.lapsed.set(id, now);
      changed = true;
      for (const [peer, nonce] of request.peers) void this.tellLobby(peer, nonce, 'expired');
    }
    if (changed) this.lobbyChanged();
  }
  /** Host: the current admission and permission settings. */
  getPolicy() {
    return this.policy;
  }
  /** Host: changes the settings, tells the page, and gives every guest their permissions again. */
  setPolicy(policy: SessionPolicy, resend = true) {
    if (!this.host) return;
    this.policy = policy;
    this.options.policyChanged?.(policy);
    if (resend) this.broadcast(true);
    // Switching to "Let guests in at once" lets in whoever was waiting, while there is room.
    if (policy.admission === 'open')
      for (const [id, request] of [...this.requests])
        if (request.kind === 'admit' && this.seats(id) < places(policy)) void this.letIn(id);
  }
  /**
   * Host: lets a waiting guest in (2.37). A full session gets one more place for this session, so a
   * friend asking for an extra place is never refused by the click that was meant to let them in.
   */
  async letIn(id: string) {
    const request = this.requests.get(id);
    const away = this.policy.turnedAway.find((p) => p.id === id);
    if (!request && !away) return;
    let policy = {
      ...this.policy,
      turnedAway: this.policy.turnedAway.filter((p) => p.id !== id),
      admitted: bounded([...this.policy.admitted.filter((a) => a !== id), id]),
    };
    if (request && this.seats(id) >= places(policy)) {
      if (!this.canGrow())
        throw Error(`The session is at its largest (${MAX_PLACES + MAX_EXTRA} guests).`);
      policy = { ...policy, extra: policy.extra + 1 };
    }
    this.requests.delete(id);
    // Their place is taken now, before the settings change below lets anyone else in (2.37 review).
    if (request) this.joining.add(id);
    try {
      this.setPolicy(policy, false);
      this.lobbyChanged();
      // A guest turned away earlier stopped asking; the next time they open the invitation they are in.
      for (const [peer, nonce] of request?.peers ?? [])
        await this.welcome(peer, nonce, request!.participant);
    } finally {
      // Seated by now (the welcome took the place over), or nobody was left to welcome.
      this.joining.delete(id);
    }
  }
  /** Host: turns a waiting guest away; they are told calmly and are not asked about again. */
  turnAway(id: string) {
    const request = this.requests.get(id);
    if (!request) return;
    this.requests.delete(id);
    this.joined.delete(id);
    this.setPolicy(
      {
        ...this.policy,
        turnedAway: bounded([
          ...this.policy.turnedAway.filter((p) => p.id !== id),
          request.participant,
        ]),
        admitted: this.policy.admitted.filter((a) => a !== id),
      },
      false,
    );
    this.lobbyChanged();
    for (const [peer, nonce] of request.peers) void this.tellLobby(peer, nonce, 'turnedAway');
  }
  // ---- messages -------------------------------------------------------------------------------
  private async handle(raw: unknown, peer: string) {
    if (this.stopped || this.denied.has(peer)) return;
    if (!raw || typeof raw !== 'object' || !('v' in raw) || raw.v !== 1 || !('type' in raw))
      throw Error('Protocol mismatch');
    const m = raw as Message;
    const rate = this.rates.get(peer) ?? {
      control: new RateLimit(GUEST_RATES.control),
      other: new RateLimit(GUEST_RATES.other),
      abuse: new RateLimit(this.host ? GUEST_RATES.abuse : HOST_FLOOD),
    };
    this.rates.set(peer, rate);
    if (!rate.abuse.accept()) {
      // A guest never disconnects from its verified host over volume; it lets the excess go. Any other
      // peer in the mesh that floods a guest is dropped as before.
      if (!this.host && this.verified.has(peer)) return;
      throw Error('Rate limit');
    }
    if (this.host) {
      const envelope = m.type === 'command' ? envelopeSchema.safeParse(m.command) : null;
      // The roomy control budget needs a 2.35 companion. Older ones have a single 32/s budget for the
      // host and every guest together, so there each guest keeps the single 12/s budget of 2.34.
      const control =
        !!envelope?.success &&
        controlOps.some((op) => op === envelope.data.op) &&
        supports(this.current().companionVersion, 'intents');
      if (!(control ? rate.control : rate.other).accept()) {
        // Over a budget: say so (a command gets its refusal), but never disconnect for it (2.35).
        if (this.accepted.has(peer) && envelope?.success)
          this.send(
            {
              v: 1,
              type: 'ack',
              id: envelope.data.id,
              ok: false,
              error: 'Too many requests. Please try again shortly.',
            },
            peer,
          );
        return;
      }
      if (m.type === 'hello' && typeof m.nonce === 'string' && m.nonce.length <= 64) {
        // Two connections per guest at most (direct and relay), for the largest session there can be.
        if (!this.accepted.has(peer) && this.accepted.size >= 2 * (MAX_PLACES + MAX_EXTRA))
          throw Error('Session full');
        let participant = this.participantOf(peer, m.person);
        // A connection keeps the id it was let in with; only the name can change (2.37 review). A new id
        // on the same connection would leave its own permissions, the lobby and the places behind.
        const known = this.accepted.has(peer) ? this.people.get(peer) : undefined;
        if (known && participant.id !== known.id) participant = { ...participant, id: known.id };
        if (!this.accepted.has(peer)) {
          // 2.41: the id every decision follows is the one the guest's key proves, never the one it declares.
          const who = await this.identify(peer, m, participant);
          if (!who || this.stopped || this.denied.has(peer)) return;
          if (this.accepted.has(peer)) return; // let in meanwhile by another hello on this connection
          participant = { id: who.id, name: participant.name };
          if (!who.verified) this.unverified.add(who.id);
          const verdict = this.admit(peer, m.nonce, participant, m, who.verified);
          if (verdict !== 'in') {
            await this.tellLobby(peer, m.nonce, verdict);
            return;
          }
        }
        await this.welcome(peer, m.nonce, participant);
        return;
      }
      if (m.type === 'layout' && this.accepted.has(peer) && typeof m.data === 'string') {
        // A layout over the relay (2.37), where the WebRTC request action does not exist.
        const id = typeof m.id === 'string' && z.uuid().safeParse(m.id).success ? m.id : undefined;
        if (!id) throw Error('Invalid layout');
        try {
          this.layoutFrom(peer, m.data);
          this.send({ v: 1, type: 'ack', id, ok: true }, peer);
        } catch (e) {
          this.send(
            {
              v: 1,
              type: 'ack',
              id,
              ok: false,
              error: e instanceof Error ? e.message : 'Layout refused.',
            },
            peer,
          );
        }
        return;
      }
      if (m.type !== 'command' || !this.accepted.has(peer)) return;
      const parsed = commandSchema.safeParse(m.command);
      if (!parsed.success) {
        // Not a command at all: malformed traffic, so the peer goes. A well-formed command this page
        // does not know (a guest on a newer website) is only refused, so that guest stays (2.35).
        if (!envelope?.success || !fromNewerPage(parsed.error)) throw Error('Invalid command');
        this.send(
          { v: 1, type: 'ack', id: envelope.data.id, ok: false, error: OLDER_HOST_PAGE },
          peer,
        );
        return;
      }
      const c = parsed.data;
      if (c.op === 'waveform' && c.soundId) {
        const ids = this.waveSubscriptions.get(peer) ?? new Set<string>();
        if (ids.size < 50000) ids.add(c.soundId);
        this.waveSubscriptions.set(peer, ids);
      }
      // The enforcement point for per-guest permissions (2.37). The companion checks the
      // session-wide permissions again, but it cannot tell guests apart: it trusts this page for who
      // sent a command. So this page checks each guest's own permissions, for every relayed command.
      if (!isGuestAllowed(c, { ...this.current(), permissions: this.permissionsOf(peer) })) {
        this.send(
          { v: 1, type: 'ack', id: c.id, ok: false, error: 'The host has disabled this action.' },
          peer,
        );
        return;
      }
      try {
        const person = this.people.get(peer);
        const data = (await this.execute(
          c,
          peer,
          person,
          person && this.joined.get(person.id),
        )) as JsonValue;
        this.send({ v: 1, type: 'ack', id: c.id, ok: true, data }, peer);
        this.broadcast();
      } catch (e) {
        this.send(
          {
            v: 1,
            type: 'ack',
            id: c.id,
            ok: false,
            error: e instanceof Error ? e.message : 'Command failed.',
          },
          peer,
        );
      }
    } else {
      if (m.type === 'welcome' && m.nonce === this.nonce && typeof m.signature === 'string') {
        // A real host welcomes only in reply to a hello; this bounds how often another peer can make
        // this page check a forged signature.
        if (!rate.other.accept()) return;
        const { host, guest } = this.proof(peer);
        if (!(await verify(this.identity, this.nonce, host, guest, m.signature, m.publicKey))) {
          // Someone answered for this room but cannot prove they are this invitation's host: the
          // host started over with a new identity (2.37). This page keeps looking quietly, since
          // another holder of the invitation could send the same, but says what it most likely means.
          if (!this.hostPeer) {
            this.answered = true;
            this.ended = true;
            this.status(ENDED_INVITE);
            this.report();
          }
          return;
        }
        if (this.stopped || this.revoked) return;
        this.welcomed(peer, m);
        return;
      }
      if (m.type === 'lobby' && m.nonce === this.nonce && typeof m.signature === 'string') {
        if (!LOBBY_STATES.includes(m.lobby as LobbyState) || !rate.other.accept()) return;
        const { host, guest } = this.proof(peer);
        const lobby = m.lobby as LobbyState;
        if (
          !(await verify(
            this.identity,
            `lobby:${lobby}:${this.nonce}`,
            host,
            guest,
            m.signature,
            m.publicKey,
          ))
        )
          return;
        if (this.hostPeer || this.stopped) return;
        this.answered = true;
        this.ended = false;
        this.lobbyState = lobby;
        this.canAskExtra = lobby === 'full' && m.more === true;
        if (lobby !== 'waiting') {
          this.askExtra = false;
          this.askAgainNext = false;
        }
        this.status(LOBBY_TEXT[lobby]);
        this.report();
        return;
      }
      if (
        m.type === 'challenge' &&
        m.nonce === this.nonce &&
        typeof m.challenge === 'string' &&
        CHALLENGE_PATTERN.test(m.challenge)
      ) {
        // 2.41: the host asks this browser to prove its key for this connection. Answered with a hello carrying
        // the proof; bounded like a welcome, since anyone in the room could send one.
        if (this.hostPeer === peer || !rate.other.accept()) return;
        if (this.hostChallenges.get(peer)?.challenge !== m.challenge)
          remember(this.hostChallenges, peer, { challenge: m.challenge });
        this.sendHello(peer);
        return;
      }
      if (!this.verified.has(peer)) return;
      this.lastState = Date.now();
      if (m.type === 'layouts') {
        this.receiveLayouts(m.data);
        return;
      }
      if (m.type === 'waveform') {
        this.extensions?.waveform?.(m.data);
        return;
      }
      if (m.type === 'state' || m.type === 'tick') this.syncClock(m.hostNow);
      if (m.type === 'state') this.apply(m.state);
      else if (m.type === 'tick') {
        if (m.libraryVersion === this.current().library.version)
          this.apply(m.state, this.current().library);
        else this.send({ v: 1, type: 'hello', nonce: this.nonce }, peer);
      } else if (m.type === 'ack') {
        const ack = ackSchema.parse({ ...m, type: 'ack', v: undefined });
        const p = this.settle(ack.id);
        if (p) {
          // An ok ack whose data says { superseded: true } (2.35) is a success like any other: the
          // click lost to a newer one, and the caller stays quiet about it.
          if (ack.ok) p.resolve(ack.data);
          else p.reject(Error(ack.error || 'Host rejected command.'));
        }
      } else if (m.type === 'revoked') {
        this.revoked = true;
        this.hostPeer = undefined;
        this.verified.clear();
        this.rejectPending();
        this.status('Access revoked. Ask the host for a new invitation.');
        this.stopRelay();
        void this.room?.leave();
        this.report();
      }
    }
  }
  /** Guest: a welcome that verified with this invitation (2.37: over either transport). */
  private welcomed(peer: string, m: Message) {
    const first = !this.hostPeer;
    this.verified.add(peer);
    // Direct is preferred; the relay is the way in until it works.
    const take = first || (!isRelay(peer) && isRelay(this.hostPeer!));
    if (take) {
      this.hostPeer = peer;
      this.welcomedSite = typeof m.site === 'string' && m.site.length <= 40 ? m.site : '';
      this.welcomedDrafts =
        siteRelaysDrafts(this.welcomedSite) ||
        (Array.isArray(m.relays) && m.relays.length <= 16 && m.relays.includes('drafts'));
      this.syncClock(m.hostNow, true);
    }
    const reconnected = first && this.everConnected;
    if (first) {
      this.connectedAt = Date.now();
      this.everConnected = true;
      this.ended = false;
      this.answered = true;
      this.lobbyState = undefined;
      this.askExtra = false;
      this.askAgainNext = false;
    }
    this.lastRevision = -1;
    this.apply(m.state);
    this.receiveLayouts(m.data);
    // Direct works again: the relay is no longer needed. It is opened again if direct is lost.
    if (take && !isRelay(peer) && this.link) this.stopRelay();
    // Said once, so the page can tell the person "Reconnected".
    this.report(reconnected);
  }
  private apply(raw: unknown, library?: Snapshot['library']) {
    const state = parseSnapshot(raw, library);
    this.lastState = Date.now();
    if (state.revision >= this.lastRevision) {
      this.lastRevision = state.revision;
      this.receive(state);
      this.status('Connected');
    }
  }
  broadcast(force = false) {
    if (!this.host) return;
    const state = this.current();
    for (const peer of this.accepted) {
      // A relay guest still receiving a full report gets the next one when it has arrived (2.37).
      if (isRelay(peer) && this.heavy.has(peer)) continue;
      if (!force && this.sentRevision.get(peer) === state.revision) continue;
      this.sentRevision.set(peer, state.revision);
      const version = state.library.version;
      const own = this.stateFor(peer, state);
      // Every report carries the host's clock, so guests keep their offset current (2.35).
      const hostNow = Date.now();
      if (this.sentVersion.get(peer) === version) {
        const light = { ...own, library: undefined } as unknown as Snapshot;
        this.send({ v: 1, type: 'tick', state: light, libraryVersion: version, hostNow }, peer, {
          supersedeKey: 'tick',
        });
      } else {
        this.send({ v: 1, type: 'state', state: own, hostNow }, peer, { supersedeKey: 'state' });
        this.sentVersion.set(peer, version);
      }
    }
  }
  private receiveLayouts(raw: unknown) {
    if (!Array.isArray(raw) || raw.length > 32 || !this.current().hostId) return;
    const layouts = raw.map((value) => {
      if (
        !value ||
        typeof value.owner !== 'string' ||
        value.owner.length > 128 ||
        typeof value.name !== 'string'
      )
        throw Error('Invalid shared layout.');
      return {
        owner: value.owner,
        name: cleanName(value.name),
        organization: parseOrganization(JSON.stringify(value.organization), this.current().hostId!),
      };
    });
    this.extensions?.layouts?.(layouts);
  }
  /** Host: a guest's shared layout, from either transport. */
  private layoutFrom(peerId: string, text: string) {
    if (!this.accepted.has(peerId) || this.denied.has(peerId)) throw Error('Not authorized.');
    const rate = this.layoutRates.get(peerId) ?? new RateLimit(4, 1000);
    this.layoutRates.set(peerId, rate);
    if (!rate.accept()) throw Error('Too many layout changes.');
    this.storeLayout(
      peerId,
      this.people.get(peerId)?.name ?? 'Guest',
      parseOrganization(text, this.current().hostId!),
    );
  }
  private storeLayout(owner: string, name: string, organization: Organization) {
    if (this.layouts.size >= 32 && !this.layouts.has(owner))
      throw Error('Shared layout limit reached.');
    if (
      !organization.folders.length &&
      !organization.favorites.length &&
      !organization.hidden.length &&
      !organization.order.length
    )
      this.layouts.delete(owner);
    else {
      const candidate = { owner, name: cleanName(name), organization };
      const others = [...this.layouts.values()].filter((l) => l.owner !== owner);
      if (JSON.stringify([...others, candidate]).length > 4 * 1024 * 1024)
        throw Error('Shared layouts exceed the 4 MiB session budget.');
      this.layouts.set(owner, candidate);
    }
    const layouts = [...this.layouts.values()];
    this.extensions?.layouts?.(layouts);
    for (const peer of this.accepted)
      this.send({ v: 1, type: 'layouts', data: layouts as unknown as JsonValue }, peer, {
        supersedeKey: 'layouts',
      });
  }
  async publishLayout(organization: Organization) {
    if (this.host)
      this.storeLayout(
        this.peerId,
        this.profiles?.get().name === 'Guest' ? 'Host' : (this.profiles?.get().name ?? 'Host'),
        organization,
      );
    else if (this.hostPeer && isRelay(this.hostPeer))
      await this.request({ type: 'layout', data: JSON.stringify(organization) }, 90000);
    else if (this.hostPeer && this.layoutAction)
      await this.layoutAction.request(JSON.stringify(organization), { target: this.hostPeer });
  }
  broadcastWaveform(data: { soundId: string }) {
    if (!this.host) return;
    for (const peer of this.accepted)
      if (this.waveSubscriptions.get(peer)?.has(data.soundId))
        this.send({ v: 1, type: 'waveform', data: data as unknown as JsonValue }, peer, {
          priority: 'bulk',
        });
  }
  async upload(bytes: Uint8Array, name: string) {
    if (!this.hostPeer || this.revoked) throw Error('Connect to the host first.');
    // 16 MiB through public relays would take minutes and use up their goodwill (2.37).
    if (isRelay(this.hostPeer)) throw Error(UPLOAD_RELAY);
    if (!this.uploadAction) throw Error('Connect to the host first.');
    const result = await this.uploadAction.request(bytes, {
      target: this.hostPeer,
      metadata: name,
      timeoutMs: 90000,
    });
    return JSON.parse(result);
  }
  private settle(id: string) {
    const p = this.pending.get(id);
    if (!p) return undefined;
    clearTimeout(p.timer);
    this.pending.delete(id);
    if (!p.control) this.waiting--;
    return p;
  }
  /** Guest: a request answered with an ack, like a command (2.37: layouts over the relay). */
  private request(message: Omit<Message, 'v' | 'id'>, timeoutMs: number) {
    return new Promise<unknown>((resolve, reject) => {
      if (!this.hostPeer || this.revoked) {
        reject(new Disconnected('Host is not connected.'));
        return;
      }
      if (this.waiting >= 16) {
        reject(Error('Too many pending commands.'));
        return;
      }
      const id = crypto.randomUUID();
      const timer = setTimeout(() => {
        this.settle(id);
        reject(Error('Host did not acknowledge the command.'));
      }, timeoutMs);
      this.pending.set(id, { resolve, reject, timer, control: false });
      this.waiting++;
      this.send({ ...message, v: 1, id } as Message, this.hostPeer);
    });
  }
  sendCommand(c: Command) {
    return new Promise<unknown>((resolve, reject) => {
      if (!this.hostPeer || this.revoked) {
        reject(new Disconnected('Host is not connected.'));
        return;
      }
      // A host tab that loaded before 2.35 checks commands strictly and would disconnect this guest
      // for an op or field it does not know. Its welcome says which it is; leave the new ones out.
      const command = { ...c, at: Math.round(c.at + this.hostOffset) };
      if (!siteRelaysIntents(this.welcomedSite)) {
        if (c.op === 'panic') {
          reject(Error(OLDER_HOST_PAGE));
          return;
        }
        delete command.autoplay;
        // Refused here rather than sent: one more message could cost this guest its connection.
        if (!this.olderHostRate.accept()) {
          reject(Error('Too many requests. Please try again shortly.'));
          return;
        }
      }
      // Slow work shares 16 places. A Play or Stop must never wait for one (2.35): control commands
      // are answered quickly and skip the cap.
      const control = controlOps.includes(c.op);
      if (control ? this.pending.size >= MAX_PENDING_CONTROL : this.waiting >= 16) {
        reject(Error('Too many pending commands.'));
        return;
      }
      const timer = setTimeout(
        () => {
          this.settle(c.id);
          reject(Error('Host did not acknowledge the command.'));
        },
        slowOps.includes(c.op) ? 90000 : c.op === 'waveform' ? 45000 : 12000,
      );
      // A repeated id replaces its earlier request, so the count above stays exact.
      this.settle(c.id)?.reject(Error('Command sent again.'));
      this.pending.set(c.id, { resolve, reject, timer, control });
      if (!control) this.waiting++;
      this.send({ v: 1, type: 'command', command }, this.hostPeer);
    });
  }
  /** Host: disconnects a guest on every connection they have (2.37: direct and relay). */
  disconnect(peer: string) {
    const id = this.people.get(peer)?.id;
    // Letting them back in later is a new join: they never see what was recorded while they were out (2.39).
    if (id) this.joined.delete(id);
    const peers = id ? this.peersOf(id) : [peer];
    if (!peers.includes(peer)) peers.push(peer);
    for (const p of peers) this.send({ v: 1, type: 'revoked' }, p);
    // Disconnect is the host's decision: with "Ask me first" this guest has to ask again.
    if (id && this.policy.admitted.includes(id))
      this.setPolicy(
        { ...this.policy, admitted: this.policy.admitted.filter((a) => a !== id) },
        false,
      );
    setTimeout(() => {
      for (const p of peers) this.drop(p);
      if (id) this.left.delete(id);
    }, 200);
  }
  private rejectPending() {
    for (const p of this.pending.values()) {
      clearTimeout(p.timer);
      p.reject(new Disconnected('Host disconnected.'));
    }
    this.pending.clear();
    this.waiting = 0;
  }
  async close(revoke = false) {
    if (revoke) for (const peer of this.accepted) this.send({ v: 1, type: 'revoked' }, peer);
    clearInterval(this.timer);
    this.rejectPending();
    if (revoke) await new Promise((r) => setTimeout(r, 100));
    this.stopped = true;
    const link = this.link;
    this.link = undefined;
    await Promise.all([this.room?.leave(), link?.stop().catch(() => {})]);
  }
}
