import {
  command,
  EMPTY,
  cleanName,
  participantSchema,
  type Participant,
  type Command,
  type Snapshot,
} from './protocol';
import { LocalBridge, type BridgeAddress } from './network/local';
import { Session } from './network/session';
import {
  createIdentity,
  inviteUrl,
  parseInvite,
  type HostIdentity,
  type Invite,
} from './network/identity';
type View = {
  mode: 'landing' | 'host' | 'guest';
  status: string;
  state: Snapshot;
  guests: string[];
  invite: string;
  notice: string;
  people: Record<string, Participant>;
  profile: Participant;
  resumeAvailable: boolean;
  needsName: boolean;
};
function savedProfile(): Participant {
  try {
    const p = participantSchema.safeParse(
      JSON.parse(localStorage.getItem('soundpad-profile') || 'null'),
    );
    if (p.success) return p.data;
  } catch {
    /* The controller reports command errors; browser storage is optional for display names. */
  }
  const profile = { id: crypto.randomUUID(), name: 'Guest' };
  try {
    localStorage.setItem('soundpad-profile', JSON.stringify(profile));
  } catch {
    /* Names work without local storage. */
  }
  return profile;
}
export class Controller {
  private view: View = {
    mode: 'landing',
    status: 'Not connected',
    state: EMPTY,
    guests: [],
    invite: '',
    notice: '',
    people: {},
    profile: savedProfile(),
    resumeAvailable: false,
    needsName: false,
  };
  private listeners = new Set<() => void>();
  private bridge?: LocalBridge;
  private session?: Session;
  private identity?: HostIdentity;
  private address?: BridgeAddress;
  private starting = false;
  private live = false;
  private initialized = false;
  private pendingInvite?: Invite;
  subscribe = (listener: () => void) => {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  };
  getSnapshot = () => this.view;
  private update(p: Partial<View>) {
    this.view = { ...this.view, ...p };
    for (const f of this.listeners) f();
  }
  notify = (notice: string) => this.update({ notice });
  start() {
    if (this.initialized) return;
    this.initialized = true;
    try {
      const hash = new URLSearchParams(location.hash.slice(1));
      if (hash.has('join')) {
        const invite = parseInvite(hash.get('join')!);
        this.pendingInvite = invite;
        let confirmed = false;
        try {
          confirmed = localStorage.getItem('soundpad-name-confirmed') === '1';
        } catch {
          /* Prompt still works without browser storage. */
        }
        if (confirmed) this.connectGuest(invite);
        else this.update({ mode: 'guest', status: 'Choose your name', needsName: true });
      } else {
        const raw = hash.get('host') || sessionStorage.getItem('soundpad-bridge');
        if (!raw) return;
        const match = /^(\d{1,5})\.([a-f0-9]{64})$/.exec(raw);
        if (!match || +match[1] < 1 || +match[1] > 65535)
          throw Error('Invalid companion launch link. Relaunch the EXE.');
        sessionStorage.setItem('soundpad-bridge', raw);
        history.replaceState(null, '', location.pathname + location.search);
        this.address = { port: +match[1], token: match[2] };
        if (sessionStorage.getItem('soundpad-ended')) {
          this.update({ resumeAvailable: true, status: 'Hosting ended' });
          return;
        }
        this.connectHost();
      }
    } catch (e) {
      this.update({
        status: 'Connection failed',
        notice: e instanceof Error ? e.message : 'Invalid connection link.',
      });
    }
  }
  private connectGuest(invite: Invite) {
    this.update({ mode: 'guest', status: 'Connecting', needsName: false });
    this.session = new Session(
      invite,
      undefined,
      () => this.view.state,
      async () => {},
      (state) => this.update({ state }),
      (status) => this.update({ status }),
      () => {},
      undefined,
      { get: () => this.view.profile, changed: () => {} },
    );
  }
  confirmName(name: string) {
    if (!this.view.needsName || !name.trim() || !this.pendingInvite) return;
    this.rename(name);
    try {
      localStorage.setItem('soundpad-name-confirmed', '1');
    } catch {
      /* This tab can still join. */
    }
    this.connectGuest(this.pendingInvite);
  }
  private connectHost() {
    this.update({ mode: 'host', status: 'Connecting to companion' });
    this.bridge = new LocalBridge(
      this.address!,
      (state) => {
        this.live = true;
        this.update({ state, status: 'Host ready' });
        this.session?.broadcast();
        if (!this.session && !this.starting)
          void this.hostSession(false).catch((e) => this.notify(String(e)));
      },
      (status) => {
        this.live = false;
        this.update({ status, state: { ...this.view.state, available: false, error: status } });
        this.session?.broadcast(true);
      },
    );
  }
  resume() {
    if (!this.address) return;
    sessionStorage.removeItem('soundpad-ended');
    this.update({ resumeAvailable: false });
    this.connectHost();
  }
  rename(name: string) {
    const profile = { ...this.view.profile, name: cleanName(name) };
    try {
      localStorage.setItem('soundpad-profile', JSON.stringify(profile));
    } catch {
      /* The controller reports command errors; browser storage is optional for display names. */
    }
    this.update({ profile });
    this.session?.rename();
  }
  private async hostSession(rotate: boolean) {
    this.starting = true;
    try {
      if (rotate) await this.session?.close(true);
      const key = 'soundpad-identity-' + this.address!.token;
      const stored = rotate ? null : sessionStorage.getItem(key);
      this.identity = stored ? JSON.parse(stored) : await createIdentity();
      sessionStorage.setItem(key, JSON.stringify(this.identity));
      this.session = new Session(
        this.identity!,
        this.identity,
        () => this.view.state,
        (c, _peer, person) => this.bridge!.send(c, 'guest', person),
        () => {},
        () => {},
        (guests) => this.update({ guests }),
        undefined,
        { get: () => this.view.profile, changed: (people) => this.update({ people }) },
      );
      this.update({ invite: inviteUrl(this.identity!), guests: [] });
    } finally {
      this.starting = false;
    }
  }
  async run(op: Command['op'], fields: Partial<Command> = {}) {
    const c = command(op, fields);
    if ((this.view.state.protocol ?? 1) < 2) c.v = 1;
    const previousPermissions = this.view.state.permissions;
    if (op === 'permissions' && fields.permissions && this.view.mode === 'host')
      this.update({ state: { ...this.view.state, permissions: fields.permissions } });
    try {
      if (this.view.mode === 'host') {
        if (!this.live) throw Error('Companion is disconnected.');
        return await this.bridge!.send(c, 'host', {
          ...this.view.profile,
          name: this.view.profile.name === 'Guest' ? 'Host' : this.view.profile.name,
        });
      } else if (this.view.mode === 'guest' && this.view.status === 'Connected')
        return await this.session!.sendCommand(c);
      else throw Error('Connect to the host first.');
    } catch (e) {
      if (op === 'permissions')
        this.update({ state: { ...this.view.state, permissions: previousPermissions } });
      this.notify(e instanceof Error ? e.message : 'Command failed.');
      throw e;
    }
  }
  async copyInvite() {
    try {
      if (!this.view.invite) throw Error('Session is still starting.');
      await navigator.clipboard.writeText(this.view.invite);
      this.notify('Invitation copied. Anyone with this link can join.');
    } catch {
      this.notify('Clipboard unavailable. Copy the invitation from Access settings.');
    }
  }
  async rotate() {
    try {
      await this.hostSession(true);
      this.notify('Invitation replaced. Previous guests have been disconnected.');
    } catch (e) {
      this.notify(String(e));
    }
  }
  disconnect(peer: string) {
    this.session?.disconnect(peer);
  }
  async leave() {
    const wasHost = this.view.mode === 'host';
    try {
      await this.session?.close(wasHost);
    } finally {
      this.bridge?.close();
      this.bridge = undefined;
      this.session = undefined;
      this.live = false;
      if (wasHost) {
        sessionStorage.setItem('soundpad-ended', '1');
        sessionStorage.removeItem('soundpad-identity-' + this.address!.token);
      }
      this.update({
        mode: 'landing',
        state: EMPTY,
        status: wasHost ? 'Hosting ended' : 'Disconnected',
        guests: [],
        people: {},
        invite: '',
        resumeAvailable: wasHost,
      });
    }
  }
}
export const controller = new Controller();
