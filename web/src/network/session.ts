import { joinRoom, selfId, type JsonValue } from 'trystero';
import {
  commandSchema,
  parseSnapshot,
  ackSchema,
  isGuestAllowed,
  RateLimit,
  type Command,
  type Snapshot,
  participantSchema,
  type Participant,
} from '../protocol';
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
};
export class Session {
  private room: ReturnType<typeof joinRoom>;
  private action: {
    send: (data: Message, options: { target: string }) => Promise<unknown>;
    onMessage: ((data: Message, context: { peerId: string }) => void) | null;
  };
  private accepted = new Set<string>();
  private people = new Map<string, Participant>();
  private sentRevision = new Map<string, number>();
  private denied = new Set<string>();
  private rates = new Map<string, RateLimit>();
  private bursts = new Map<string, RateLimit>();
  private hostPeer?: string;
  private nonce = crypto.randomUUID();
  private pending = new Map<
    string,
    {
      resolve: (data: unknown) => void;
      reject: (e: Error) => void;
      timer: ReturnType<typeof setTimeout>;
    }
  >();
  private timer: ReturnType<typeof setInterval>;
  private lastState = 0;
  private lastRevision = -1;
  private started = Date.now();
  private stopped = false;
  private revoked = false;
  private sentVersion = new Map<string, string>();
  private peerId: string;
  private hostOffset = 0;
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
      this.bursts.delete(peer);
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
    this.send({ v: 1, type: 'hello', nonce: this.nonce, person: this.profiles?.get() }, peer);
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
    this.bursts.delete(peer);
    this.room.getPeers()[peer]?.close();
    this.guests([...this.accepted]);
  }
  private async handle(raw: unknown, peer: string) {
    if (this.stopped || this.denied.has(peer)) return;
    if (!raw || typeof raw !== 'object' || !('v' in raw) || raw.v !== 1 || !('type' in raw))
      throw Error('Protocol mismatch');
    const m = raw as Message;
    const rate = this.rates.get(peer) ?? new RateLimit(this.host ? 12 : 60);
    this.rates.set(peer, rate);
    const burst = this.bursts.get(peer) ?? new RateLimit(this.host ? 24 : 60);
    this.bursts.set(peer, burst);
    if (!burst.accept()) throw Error('Rate limit');
    if (!rate.accept()) {
      const parsed = m.type === 'command' ? commandSchema.safeParse(m.command) : null;
      if (this.host && this.accepted.has(peer) && parsed?.success) {
        this.send(
          {
            v: 1,
            type: 'ack',
            id: parsed.data.id,
            ok: false,
            error: 'Too many requests. Please try again shortly.',
          },
          peer,
        );
        return;
      }
      throw Error('Rate limit');
    }
    if (this.host) {
      if (m.type === 'hello' && typeof m.nonce === 'string' && m.nonce.length <= 64) {
        if (this.accepted.size >= 12 && !this.accepted.has(peer)) throw Error('Session full');
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
            state: this.current(),
            hostNow: Date.now(),
          },
          peer,
        );
        this.sentVersion.set(peer, this.current().library.version);
        return;
      }
      if (m.type !== 'command' || !this.accepted.has(peer)) return;
      const parsed = commandSchema.safeParse(m.command);
      if (!parsed.success) throw Error('Invalid command');
      const c = parsed.data;
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
        (await verify(this.identity, this.nonce, peer, this.peerId, m.signature))
      ) {
        this.hostPeer = peer;
        if (typeof m.hostNow === 'number' && Number.isFinite(m.hostNow))
          this.hostOffset = m.hostNow - Date.now();
        this.lastRevision = -1;
        this.apply(m.state);
        return;
      }
      if (peer !== this.hostPeer) return;
      if (m.type === 'state') this.apply(m.state);
      else if (m.type === 'tick') {
        if (m.libraryVersion === this.current().library.version)
          this.apply(m.state, this.current().library);
        else this.send({ v: 1, type: 'hello', nonce: this.nonce }, peer);
      } else if (m.type === 'ack') {
        const ack = ackSchema.parse({ ...m, type: 'ack', v: undefined });
        const p = this.pending.get(ack.id);
        if (p) {
          clearTimeout(p.timer);
          this.pending.delete(ack.id);
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
      if (this.sentVersion.get(peer) === version) {
        const light = { ...state, library: undefined } as unknown as Snapshot;
        this.send({ v: 1, type: 'tick', state: light, libraryVersion: version }, peer);
      } else {
        this.send({ v: 1, type: 'state', state }, peer);
        this.sentVersion.set(peer, version);
      }
    }
  }
  sendCommand(c: Command) {
    return new Promise<unknown>((resolve, reject) => {
      if (!this.hostPeer || this.revoked) {
        reject(Error('Host is not connected.'));
        return;
      }
      if (this.pending.size >= 16) {
        reject(Error('Too many pending commands.'));
        return;
      }
      const timer = setTimeout(
        () => {
          this.pending.delete(c.id);
          reject(Error('Host did not acknowledge the command.'));
        },
        c.op === 'waveform' ? 45000 : 12000,
      );
      this.pending.set(c.id, { resolve, reject, timer });
      this.send(
        { v: 1, type: 'command', command: { ...c, at: Math.round(c.at + this.hostOffset) } },
        this.hostPeer,
      );
    });
  }
  disconnect(peer: string) {
    this.send({ v: 1, type: 'revoked' }, peer);
    setTimeout(() => this.drop(peer), 200);
  }
  private rejectPending() {
    for (const p of this.pending.values()) {
      clearTimeout(p.timer);
      p.reject(Error('Host disconnected.'));
    }
    this.pending.clear();
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
