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
import { APP_VERSION, siteRelaysIntents, supports } from '../version';
import { sign, verify, type HostIdentity, type Invite } from './identity';

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
};
/** Most guests one host session admits at once. */
export const MAX_GUESTS = 12;
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
/** The part of a command needed to answer it, whether or not this page knows the rest. */
const envelopeSchema = z.object({ id: z.uuid(), op: z.string().max(40) });
/**
 * True when a command fails the check only because it names an op, field or choice this page has
 * never heard of: a guest on a newer website, not malformed traffic. Wrong types and missing parts
 * are malformed.
 */
const fromNewerPage = (error: z.ZodError) =>
  error.issues.every((i) => i.code === 'unrecognized_keys' || i.code === 'invalid_value');
export class Session {
  private layoutAction?: RequestAction<string, string>;
  private layouts = new Map<string, SharedLayout>();
  private layoutRates = new Map<string, RateLimit>();
  private uploadAction?: RequestAction<Uint8Array, string>;
  private uploading = new Set<string>();
  private waveSubscriptions = new Map<string, Set<string>>();
  private room: ReturnType<typeof joinRoom>;
  private action: {
    send: (data: Message, options: { target: string }) => Promise<unknown>;
    onMessage: ((data: Message, context: { peerId: string }) => void) | null;
  };
  private accepted = new Set<string>();
  private people = new Map<string, Participant>();
  private sentRevision = new Map<string, number>();
  private denied = new Set<string>();
  private rates = new Map<string, { control: RateLimit; other: RateLimit; abuse: RateLimit }>();
  private hostPeer?: string;
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
  private olderHostRate = new RateLimit(OLDER_HOST_RATE);
  /** Website version the host's tab reported in its welcome ('' from a tab older than 2.35). */
  get hostSite() {
    return this.hostPeer ? this.welcomedSite : '';
  }
  constructor(
    private identity: Invite,
    private host: HostIdentity | undefined,
    private current: () => Snapshot,
    private execute: (c: Command, peer: string, person?: Participant) => Promise<unknown>,
    private receive: (s: Snapshot) => void,
    private status: (s: string) => void,
    private guests: (ids: string[]) => void,
    transport = { join: joinRoom, id: selfId },
    private profiles?: {
      get: () => Participant;
      changed: (people: Record<string, Participant>) => void;
    },
    private extensions?: {
      layouts?: (layouts: SharedLayout[]) => void;
      waveform?: (data: unknown) => void;
      upload: (bytes: Uint8Array, name: string, by: string, peer: string) => Promise<unknown>;
    },
    /** This page's website version, told to the other side; tests pass their own. */
    private site: string = APP_VERSION,
  ) {
    this.peerId = transport.id;
    let turnConfig: RTCIceServer[] | undefined;
    try {
      const configured = import.meta.env.VITE_TURN_CONFIG;
      if (configured) turnConfig = JSON.parse(configured);
    } catch {
      status('Invalid optional TURN configuration.');
    }
    this.room = transport.join(
      {
        appId: 'org.soundpad-web.v1',
        password: identity.secret,
        maxReceiveBytes: 16 * 1024 * 1024,
        turnConfig,
      },
      identity.room,
      {
        onJoinError: () => {
          if (!host && !this.hostPeer)
            this.status(
              'Connection failed. Check the invite and network; restrictive NAT may need your own TURN configuration.',
            );
        },
      },
    );
    if (extensions)
      this.layoutAction = this.room.makeAction<string, string>('organization', {
        kind: 'request',
        onReceive: ({ peerId, byteLength, kind }) =>
          kind === 'response'
            ? !this.host && peerId === this.hostPeer && byteLength <= 1024
            : !!this.host &&
              this.accepted.has(peerId) &&
              !this.denied.has(peerId) &&
              byteLength <= 2 * 1024 * 1024,
        onRequest: (text, { peerId }) => {
          if (!this.accepted.has(peerId) || this.denied.has(peerId)) throw Error('Not authorized.');
          const rate = this.layoutRates.get(peerId) ?? new RateLimit(4, 1000);
          this.layoutRates.set(peerId, rate);
          if (!rate.accept()) throw Error('Too many layout changes.');
          this.storeLayout(
            peerId,
            this.people.get(peerId)?.name ?? 'Guest',
            parseOrganization(text, this.current().hostId!),
          );
          return 'ok';
        },
      });
    if (extensions)
      this.uploadAction = this.room.makeAction<Uint8Array, string>('audio-upload', {
        kind: 'request',
        onReceive: ({ peerId, byteLength, kind }) =>
          kind === 'response'
            ? !this.host && peerId === this.hostPeer && byteLength <= 1024
            : !!this.host &&
              this.accepted.has(peerId) &&
              !this.denied.has(peerId) &&
              byteLength <= 16 * 1024 * 1024 &&
              this.uploading.size < 2 &&
              !this.uploading.has(peerId) &&
              this.current().permissions.guestUploads !== false,
        onRequest: async (bytes, { peerId, metadata }) => {
          if (
            !this.accepted.has(peerId) ||
            this.denied.has(peerId) ||
            this.uploading.size >= 2 ||
            this.uploading.has(peerId) ||
            this.current().permissions.guestUploads === false
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
            );
            this.broadcast();
            return JSON.stringify(result ?? {});
          } finally {
            this.uploading.delete(peerId);
          }
        },
      });
    this.action = this.room.makeAction<Message>('protocol', {
      onReceive: ({ byteLength, peerId }) =>
        byteLength <= (host ? 8192 : 16 * 1024 * 1024) && !this.denied.has(peerId),
    });
    this.action.onMessage = (data, { peerId }) => {
      void this.handle(data, peerId).catch(() => this.drop(peerId));
    };
    this.room.onPeerJoin = (peer) => {
      if (this.denied.has(peer)) {
        this.room.getPeers()[peer]?.close();
        return;
      }
      if (!host) this.sendHello(peer);
    };
    this.room.onPeerLeave = (peer) => {
      this.accepted.delete(peer);
      this.people.delete(peer);
      this.profiles?.changed(Object.fromEntries(this.people));
      this.sentRevision.delete(peer);
      this.sentVersion.delete(peer);
      this.rates.delete(peer);
      this.guests([...this.accepted]);
      if (peer === this.hostPeer) {
        this.hostPeer = undefined;
        this.lastRevision = -1;
        this.status('Reconnecting');
        this.rejectPending();
      }
    };
    this.timer = setInterval(() => {
      if (this.stopped || this.revoked) return;
      if (host) {
        this.broadcast(true);
      } else {
        if (this.hostPeer && Date.now() - this.lastState > 10000) {
          this.status('Reconnecting');
          this.hostPeer = undefined;
          this.lastRevision = -1;
          this.rejectPending();
        }
        if (!this.hostPeer) {
          for (const peer of Object.keys(this.room.getPeers())) this.sendHello(peer);
          if (Date.now() - this.started > 25000)
            this.status(
              'Host unavailable. Keep the host tab open, check the invite, or try another network. Public relays and direct WebRTC can be blocked.',
            );
        }
      }
    }, 2500);
  }
  private send(data: Message, peer: string) {
    if (!this.stopped) void this.action.send(data, { target: peer }).catch(() => {});
  }
  private sendHello(peer: string) {
    this.send(
      { v: 1, type: 'hello', nonce: this.nonce, person: this.profiles?.get(), site: this.site },
      peer,
    );
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
    this.denied.add(peer);
    this.accepted.delete(peer);
    this.people.delete(peer);
    this.profiles?.changed(Object.fromEntries(this.people));
    this.rates.delete(peer);
    this.room.getPeers()[peer]?.close();
    this.guests([...this.accepted]);
  }
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
      if (!this.host && peer === this.hostPeer) return;
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
        if (this.accepted.size >= MAX_GUESTS && !this.accepted.has(peer))
          throw Error('Session full');
        const signature = await sign(this.host, m.nonce, this.peerId, peer);
        if (this.denied.has(peer) || this.stopped) return;
        this.accepted.add(peer);
        const person = participantSchema.safeParse(m.person);
        this.people.set(
          peer,
          person.success
            ? person.data
            : (this.people.get(peer) ?? { id: crypto.randomUUID(), name: 'Guest' }),
        );
        this.profiles?.changed(Object.fromEntries(this.people));
        this.guests([...this.accepted]);
        this.send(
          {
            v: 1,
            type: 'welcome',
            nonce: m.nonce,
            signature,
            publicKey: this.host.publicKey,
            state: this.current(),
            hostNow: Date.now(),
            site: this.site,
            data: [...this.layouts.values()] as unknown as JsonValue,
          },
          peer,
        );
        this.sentVersion.set(peer, this.current().library.version);
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
      if (!isGuestAllowed(c, this.current())) {
        this.send(
          { v: 1, type: 'ack', id: c.id, ok: false, error: 'The host has disabled this action.' },
          peer,
        );
        return;
      }
      try {
        const data = (await this.execute(c, peer, this.people.get(peer))) as JsonValue;
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
      if (
        m.type === 'welcome' &&
        m.nonce === this.nonce &&
        typeof m.signature === 'string' &&
        // A real host welcomes only in reply to a hello; this bounds how often another peer can make
        // this page check a forged signature.
        rate.other.accept() &&
        (await verify(this.identity, this.nonce, peer, this.peerId, m.signature, m.publicKey))
      ) {
        this.hostPeer = peer;
        this.welcomedSite = typeof m.site === 'string' && m.site.length <= 40 ? m.site : '';
        this.syncClock(m.hostNow, true);
        this.lastRevision = -1;
        this.apply(m.state);
        this.receiveLayouts(m.data);
        return;
      }
      if (peer !== this.hostPeer) return;
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
        this.rejectPending();
        this.status('Access revoked. Ask the host for a new invitation.');
        void this.room.leave();
      }
    }
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
      if (!force && this.sentRevision.get(peer) === state.revision) continue;
      this.sentRevision.set(peer, state.revision);
      const version = state.library.version;
      // Every report carries the host's clock, so guests keep their offset current (2.35).
      const hostNow = Date.now();
      if (this.sentVersion.get(peer) === version) {
        const light = { ...state, library: undefined } as unknown as Snapshot;
        this.send({ v: 1, type: 'tick', state: light, libraryVersion: version, hostNow }, peer);
      } else {
        this.send({ v: 1, type: 'state', state, hostNow }, peer);
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
      this.send({ v: 1, type: 'layouts', data: layouts as unknown as JsonValue }, peer);
  }
  async publishLayout(organization: Organization) {
    if (this.host)
      this.storeLayout(
        this.peerId,
        this.profiles?.get().name === 'Guest' ? 'Host' : (this.profiles?.get().name ?? 'Host'),
        organization,
      );
    else if (this.hostPeer && this.layoutAction)
      await this.layoutAction.request(JSON.stringify(organization), { target: this.hostPeer });
  }
  broadcastWaveform(data: { soundId: string }) {
    if (!this.host) return;
    for (const peer of this.accepted)
      if (this.waveSubscriptions.get(peer)?.has(data.soundId))
        this.send({ v: 1, type: 'waveform', data: data as unknown as JsonValue }, peer);
  }
  async upload(bytes: Uint8Array, name: string) {
    if (!this.hostPeer || !this.uploadAction || this.revoked)
      throw Error('Connect to the host first.');
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
  disconnect(peer: string) {
    this.send({ v: 1, type: 'revoked' }, peer);
    setTimeout(() => this.drop(peer), 200);
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
    await this.room.leave();
  }
}
