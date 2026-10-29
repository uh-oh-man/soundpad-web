import { command, EMPTY, type Command, type Snapshot } from './protocol';
import { LocalBridge, type BridgeAddress } from './network/local';
import { Session } from './network/session';
import { createIdentity, inviteUrl, parseInvite, type HostIdentity } from './network/identity';
type View = {
  mode: 'landing' | 'host' | 'guest';
  status: string;
  state: Snapshot;
  guests: string[];
  invite: string;
  notice: string;
};
export class Controller {
  private view: View = {
    mode: 'landing',
    status: 'Not connected',
    state: EMPTY,
    guests: [],
    invite: '',
    notice: '',
  };
  private listeners = new Set<() => void>();
  private bridge?: LocalBridge;
  private session?: Session;
  private identity?: HostIdentity;
  private address?: BridgeAddress;
  private starting = false;
  private live = false;
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
    try {
      const hash = new URLSearchParams(location.hash.slice(1));
      if (hash.has('join')) {
        const invite = parseInvite(hash.get('join')!);
        this.update({ mode: 'guest', status: 'Connecting' });
        this.session = new Session(
          invite,
          undefined,
          () => this.view.state,
          async () => {},
          (state) => this.update({ state }),
          (status) => this.update({ status }),
          () => {},
        );
      } else {
        const raw = hash.get('host') || sessionStorage.getItem('soundpad-bridge');
        if (!raw) return;
        const match = /^(\d{1,5})\.([a-f0-9]{64})$/.exec(raw);
        if (!match || +match[1] < 1 || +match[1] > 65535)
          throw Error('Invalid companion launch link. Relaunch the EXE.');
        sessionStorage.setItem('soundpad-bridge', raw);
        history.replaceState(null, '', location.pathname + location.search);
        this.address = { port: +match[1], token: match[2] };
        this.update({ mode: 'host', status: 'Connecting to companion' });
        this.bridge = new LocalBridge(
          this.address,
          (state) => {
            this.live = true;
            this.update({ state, status: 'Host ready' });
            if (!this.session && !this.starting)
              void this.hostSession(false).catch((e) => this.notify(String(e)));
          },
          (status) => {
            this.live = false;
            this.update({ status, state: { ...this.view.state, available: false, error: status } });
          },
        );
      }
    } catch (e) {
      this.update({
        status: 'Connection failed',
        notice: e instanceof Error ? e.message : 'Invalid connection link.',
      });
    }
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
        (c) => this.bridge!.send(c, 'guest'),
        () => {},
        () => {},
        (guests) => this.update({ guests }),
      );
      this.update({ invite: inviteUrl(this.identity!), guests: [] });
    } finally {
      this.starting = false;
    }
  }
  async run(op: Command['op'], fields: Partial<Command> = {}) {
    const c = command(op, fields);
    const previousPermissions = this.view.state.permissions;
    if (op === 'permissions' && fields.permissions && this.view.mode === 'host')
      this.update({ state: { ...this.view.state, permissions: fields.permissions } });
    try {
      if (this.view.mode === 'host') {
        if (!this.live) throw Error('Companion is disconnected.');
        await this.bridge!.send(c);
      } else if (this.view.mode === 'guest' && this.view.status === 'Connected')
        await this.session!.sendCommand(c);
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
    await this.session?.close(this.view.mode === 'host');
    this.bridge?.close();
    sessionStorage.removeItem('soundpad-bridge');
    this.update({ mode: 'landing', state: EMPTY, status: 'Disconnected', guests: [], invite: '' });
  }
}
export const controller = new Controller();
