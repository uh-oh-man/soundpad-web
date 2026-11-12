import {
  ackSchema,
  controlOps,
  Disconnected,
  parseSnapshot,
  type Command,
  type Snapshot,
  type Participant,
  participantSchema,
  slowOps,
} from '../protocol';
import { z } from 'zod';
import type { SharedLayout } from '../shared-layout';
import type { HostIdentity } from './identity';
import type { LobbyRequest } from './session';
import type { SessionPolicy } from './policy';
export type BridgeAddress = { port: number; token: string };

/**
 * Desktop session (2.36, feature "desktopSession"). A 2.36 companion admits several host pages and
 * names one of them the session owner, which runs the guest room; the others are views. Its window
 * page carries the launch's owner key. Older companions send none of these messages.
 */
export type HostRole = {
  role: 'owner' | 'view';
  identity: HostIdentity | null;
  /** The session settings an owner sent earlier in this launch (2.37); read with `readPolicy`. */
  settings?: unknown;
};
/** What the owner tells every view about the session, relayed by the companion. */
export type SessionInfo = {
  invite: string;
  guests: string[];
  people: Record<string, Participant>;
  layouts: SharedLayout[];
  /** True when the owner is the companion window (absent if a companion drops it: then a window). */
  ownerWindow?: boolean;
  /** The lobby, settings and each guest's connection (2.37), for a view to show. */
  control?: {
    requests: LobbyRequest[];
    /** Read with `readPolicy`. */
    policy: unknown;
    links: Record<string, 'direct' | 'relay'>;
  };
};
export type OwnerAction = { action: 'rotate' } | { action: 'disconnect'; peer: string };
export type DesktopEvents = {
  role: (role: HostRole) => void;
  session: (info: SessionInfo) => void;
  ownerAction: (action: OwnerAction) => void;
};
/** The identity the companion keeps for this launch; it came from an owner page, so check its shape. */
const identitySchema = z.object({
  v: z.literal(1),
  room: z.string().regex(/^[-\w]{32}$/),
  secret: z.string().regex(/^[-\w]{43}$/),
  publicKey: z.string().regex(/^[-\w]{100,200}$/),
  privateKey: z.string().regex(/^[-\w]{100,400}$/),
  shortCode: z
    .string()
    .regex(/^[12][-\w]{43,96}$/)
    .optional(),
});
const roleSchema = z.object({
  type: z.literal('role'),
  role: z.enum(['owner', 'view']),
  identity: identitySchema.nullable().optional(),
  // Checked by the controller (readPolicy), which falls back to the defaults.
  settings: z.unknown().optional(),
});
const peerId = z.string().min(1).max(100);
const soundIds = z.array(z.string().regex(/^[a-f0-9]{64}$/)).max(50000);
const sessionInfoSchema = z.object({
  type: z.literal('session'),
  invite: z.string().max(4000),
  guests: z.array(peerId).max(64),
  people: z.record(peerId, participantSchema),
  layouts: z
    .array(
      z.object({
        owner: peerId,
        name: z.string().max(80),
        organization: z.object({
          folders: z
            .array(
              z.object({
                id: z.uuid(),
                name: z.string().min(1).max(80),
                parent: z.uuid().nullable(),
                soundIds,
              }),
            )
            .max(128),
          favorites: soundIds,
          hidden: soundIds,
          order: soundIds,
        }),
      }),
    )
    .max(32),
  ownerWindow: z.boolean().optional(),
  control: z
    .object({
      requests: z
        .array(
          z.object({
            id: z.uuid(),
            name: z.string().max(40),
            kind: z.enum(['admit', 'extra']),
            at: z.number(),
            expiresAt: z.number(),
          }),
        )
        .max(64),
      policy: z.unknown(),
      links: z.record(peerId, z.enum(['direct', 'relay'])),
    })
    .optional(),
});
const ownerActionSchema = z.union([
  z.object({ type: z.literal('ownerAction'), action: z.literal('rotate') }),
  z.object({ type: z.literal('ownerAction'), action: z.literal('disconnect'), peer: peerId }),
]);
/** What a host page sends about the desktop session (2.36); only to a companion that sent a role. */
export type DesktopMessage =
  | { type: 'identity'; identity: HostIdentity }
  | { type: 'sessionSettings'; settings: SessionPolicy }
  | ({ type: 'session' } & SessionInfo)
  | ({ type: 'ownerAction' } & OwnerAction);
export class LocalBridge {
  private socket?: WebSocket;
  private stopped = false;
  /**
   * True once this socket's companion sent a role (2.36). Older companions read every message as a
   * command and would drop the connection over any other kind, so nothing else is sent to them.
   */
  private desktopSession = false;
  private audience?: string;
  setAudience(value: string) {
    this.audience = value;
  }
  private latest?: Snapshot;
  private retry?: ReturnType<typeof setTimeout>;
  private pending = new Map<
    string,
    {
      resolve: (data: unknown) => void;
      reject: (e: Error) => void;
      timer: ReturnType<typeof setTimeout>;
      control: boolean;
      owner: string;
    }
  >();
  /** Unanswered commands other than playback and transport ones; only these count toward the cap. */
  private waiting = 0;
  /** Unanswered control commands per sender, so a guest's backlog never crowds out the host's Stop (2.35). */
  private controls = new Map<string, number>();
  private settle(id: string) {
    const p = this.pending.get(id);
    if (!p) return undefined;
    clearTimeout(p.timer);
    this.pending.delete(id);
    if (!p.control) this.waiting--;
    else this.controls.set(p.owner, (this.controls.get(p.owner) ?? 1) - 1);
    return p;
  }
  constructor(
    private address: BridgeAddress,
    private state: (s: Snapshot) => void,
    private status: (s: string) => void,
    private waveform?: (data: unknown) => void,
    private desktop?: DesktopEvents & {
      /** The companion window's launch key (2.36); only its page has one. Never logged or stored by the companion. */
      ownerKey?: string;
    },
    /** This tab's id (2.36), kept across a reload so the companion gives a reloading owner its place back. */
    readonly clientId: string = crypto.randomUUID(),
  ) {
    this.connect();
  }
  private connect() {
    if (this.stopped) return;
    this.status('Connecting to companion');
    const ws = new WebSocket(`ws://127.0.0.1:${this.address.port}/bridge`);
    this.socket = ws;
    this.desktopSession = false;
    const timeout = setTimeout(() => ws.close(), 8000);
    ws.onopen = () => {
      // Protocol 3 (2.36) asks a desktop-session companion for a role. Older companions read only the
      // token, so the extra fields change nothing for them.
      ws.send(
        JSON.stringify({
          token: this.address.token,
          protocol: 3,
          clientId: this.clientId,
          ...(this.desktop?.ownerKey ? { ownerKey: this.desktop.ownerKey } : {}),
        }),
      );
    };
    ws.onmessage = (e) => {
      try {
        const data = JSON.parse(e.data);
        if (data.type === 'waveform') {
          this.waveform?.(data.data);
          return;
        }
        if (data.type === 'role') {
          // A role this page cannot read would leave it not knowing whether to host: a mismatch.
          const role = roleSchema.parse(data);
          this.desktopSession = true;
          this.desktop?.role({
            role: role.role,
            identity: role.identity ?? null,
            settings: role.settings ?? undefined,
          });
          return;
        }
        if (data.type === 'session' || data.type === 'ownerAction') {
          // Relayed from another page; one this page cannot read is skipped, not a reason to disconnect.
          if (data.type === 'session') {
            const info = sessionInfoSchema.safeParse(data);
            if (info.success) {
              const { invite, guests, people, layouts, ownerWindow, control } = info.data;
              this.desktop?.session({ invite, guests, people, layouts, ownerWindow, control });
            }
          } else {
            const action = ownerActionSchema.safeParse(data);
            if (action.success)
              this.desktop?.ownerAction(
                action.data.action === 'rotate'
                  ? { action: 'rotate' }
                  : { action: 'disconnect', peer: action.data.peer },
              );
          }
          return;
        }
        if (data.type === 'state' || data.type === 'tick') {
          const s = parseSnapshot(
            data.state,
            data.type === 'tick' ? this.latest?.library : undefined,
          );
          if (this.latest && s.revision < this.latest.revision) return;
          this.latest = s;
          clearTimeout(timeout);
          this.state(s);
        } else {
          const ack = ackSchema.parse(data);
          const p = this.settle(ack.id);
          if (p) {
            if (ack.ok) p.resolve(ack.data);
            else p.reject(Error(ack.error || 'Command rejected.'));
          }
        }
      } catch {
        this.status('Companion protocol mismatch. Update both components.');
        ws.close();
      }
    };
    ws.onerror = () =>
      this.status(
        'Cannot reach companion. Keep the EXE running and allow this browser’s local-network prompt. Chrome or Edge is recommended.',
      );
    ws.onclose = () => {
      clearTimeout(timeout);
      for (const p of this.pending.values()) {
        clearTimeout(p.timer);
        p.reject(new Disconnected('Companion disconnected.'));
      }
      this.pending.clear();
      this.waiting = 0;
      this.controls.clear();
      if (!this.stopped) {
        this.status('Companion disconnected. Reconnecting…');
        this.retry = setTimeout(() => this.connect(), 2500);
      }
    };
  }
  send(command: Command, actor = 'host', participant?: Participant, requester?: string) {
    return new Promise<unknown>((resolve, reject) => {
      if (this.socket?.readyState !== WebSocket.OPEN) {
        reject(new Disconnected('Companion is unavailable.'));
        return;
      }
      // Slow work (the host's own and every guest's) shares 20 places. A Play or Stop must never wait
      // for one (2.35): control commands are answered quickly and skip the cap, up to a bound that
      // only a runaway sender reaches.
      const control = controlOps.includes(command.op);
      const owner = actor === 'host' ? 'host' : (requester ?? 'guest');
      if (
        control
          ? (this.controls.get(owner) ?? 0) >= (owner === 'host' ? 512 : 64)
          : this.waiting >= 20
      ) {
        reject(Error('Too many pending commands.'));
        return;
      }
      const timer = setTimeout(
        () => {
          this.settle(command.id);
          reject(Error('Command timed out. Playback is never retried automatically.'));
        },
        // Removing hundreds of duplicate categories one by one can take minutes (2.15.9).
        command.op === 'cleanupCategories'
          ? 600000
          : slowOps.includes(command.op)
            ? 90000
            : command.op === 'waveform'
              ? 45000
              : 12000,
      );
      // A repeated id replaces its earlier request, so the count above stays exact.
      this.settle(command.id)?.reject(Error('Command sent again.'));
      this.pending.set(command.id, { resolve, reject, timer, control, owner });
      if (!control) this.waiting++;
      else this.controls.set(owner, (this.controls.get(owner) ?? 0) + 1);
      this.socket.send(
        JSON.stringify({ actor, command, participant, requester, audience: this.audience }),
      );
    });
  }
  /** Sends a desktop-session message (2.36); dropped while the companion is unreachable. */
  post(message: DesktopMessage) {
    if (this.socket?.readyState !== WebSocket.OPEN || !this.desktopSession) return false;
    this.socket.send(JSON.stringify(message));
    return true;
  }
  async upload(bytes: Uint8Array, name: string, by: string, host = false, owner = 'host') {
    const response = await fetch(`http://127.0.0.1:${this.address.port}/session-audio`, {
      method: 'POST',
      headers: {
        Authorization: `Bearer ${this.address.token}`,
        'Content-Type': 'application/octet-stream',
        'X-Audio-Name': encodeURIComponent(name),
        'X-Audio-By': encodeURIComponent(by),
        'X-Audio-Actor': host ? 'host' : 'guest',
        'X-Audio-Owner': owner,
        'X-Audio-Audience': this.audience ?? '',
      },
      body: bytes as Uint8Array<ArrayBuffer>,
    });
    if (!response.ok) throw Error(`Upload rejected (${response.status}).`);
    const result = ackSchema.omit({ id: true }).parse(await response.json());
    if (!result.ok) throw Error(result.error || 'Audio import failed.');
    return result.data;
  }
  close() {
    this.stopped = true;
    clearTimeout(this.retry);
    this.socket?.close();
  }
}
