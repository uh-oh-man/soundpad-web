import {
  command,
  EMPTY,
  cleanName,
  participantSchema,
  type Participant,
  type Command,
  type Snapshot,
} from './protocol';
import { acceptWaveform } from './waveform-cache';
import { waveformSchema } from './protocol';
import { LocalBridge, type BridgeAddress } from './network/local';
import { Session } from './network/session';
import {
  createIdentity,
  prepareInvite,
  inviteUrl,
  parseInvite,
  type HostIdentity,
  type Invite,
} from './network/identity';
import { notify, type NoticeKind } from './notifications';
import { companionAtLeast } from './version';
import type { SharedLayout, Organization } from './shared-layout';
type View = {
  layouts: SharedLayout[];
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
const errorMessage = (e: unknown, fallback = 'Something went wrong.') =>
  e instanceof Error ? e.message : typeof e === 'string' && e ? e : fallback;
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
/** Shown to a guest whose browser has no WebRTC, which joining needs. */
export const NO_WEBRTC =
  "WebRTC blocked. This browser can't reach the host because WebRTC is turned off or not supported. Use an up-to-date Chrome, Edge, Firefox or Safari, and check that no setting or privacy extension blocks WebRTC.";

export class Controller {
  private view: View = {
    layouts: [],
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
  private permissionSequence = 0;
  private pendingPermissions?: { sequence: number; value: Snapshot['permissions'] };
  private canonicalPermissions?: Snapshot['permissions'];
  private visibleState(state: Snapshot) {
    this.canonicalPermissions = state.permissions;
    const desired = this.pendingPermissions?.value;
    if (desired) {
      const keys = [
        'remotePlayback',
        'guestCounter',
        'guestSeeking',
        'guestSpotify',
        'guestUploads',
        'guestTransforms',
        'guestMimic',
      ] as const;
      if (keys.every((key) => (state.permissions[key] ?? true) === (desired[key] ?? true)))
        this.pendingPermissions = undefined;
    }
    return this.pendingPermissions
      ? { ...state, permissions: this.pendingPermissions.value }
      : state;
  }
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
  notify = (message: string, kind: NoticeKind = 'info') => notify(message, kind);
  async start() {
    if (this.initialized) return;
    this.initialized = true;
    try {
      const hash = new URLSearchParams(location.hash.slice(1));
      if (hash.has('join')) {
        const invite = await parseInvite(hash.get('join')!);
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
    // Guests reach the host over WebRTC. Without it (turned off by a setting or privacy extension, or an
    // old browser) say so at once instead of waiting for the generic "Host unavailable" after 25 s.
    if (typeof RTCPeerConnection === 'undefined') {
      this.update({ mode: 'guest', status: NO_WEBRTC, needsName: false });
      return;
    }
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
      {
        layouts: (layouts) => this.update({ layouts }),
        waveform: (data) => acceptWaveform(this.view.state.hostId, data),
        upload: async () => {
          throw Error('Host only.');
        },
      },
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
        this.update({ state: this.visibleState(state), status: 'Host ready' });
        this.session?.broadcast();
        if (!this.session && !this.starting)
          void this.hostSession(false).catch((e) => this.notify(errorMessage(e), 'error'));
      },
      (status) => {
        this.live = false;
        this.update({ status, state: { ...this.view.state, available: false, error: status } });
        this.session?.broadcast(true);
      },
      (raw) => {
        const data = waveformSchema.parse(raw);
        acceptWaveform(this.view.state.hostId, data);
        this.session?.broadcastWaveform(data);
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
      const audienceId = crypto.randomUUID();
      this.bridge!.setAudience(audienceId);
      if (companionAtLeast(this.view.state.companionVersion, 2, 2))
        await this.bridge!.send(command('beginSession', { audienceId }), 'host');
      const key = 'soundpad-identity-' + this.address!.token;
      const stored = rotate ? null : sessionStorage.getItem(key);
      this.identity = stored ? JSON.parse(stored) : await createIdentity();
      await prepareInvite(this.identity!);
      sessionStorage.setItem(key, JSON.stringify(this.identity));
      this.session = new Session(
        this.identity!,
        this.identity,
        () => this.view.state,
        (c, peer, person) => this.bridge!.send(c, 'guest', person, peer),
        () => {},
        () => {},
        (guests) => this.update({ guests }),
        undefined,
        { get: () => this.view.profile, changed: (people) => this.update({ people }) },
        {
          layouts: (layouts) => this.update({ layouts }),
          upload: (bytes, name, by, peer) => this.bridge!.upload(bytes, name, by, false, peer),
        },
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
    let permissionAttempt = 0;
    if (op === 'permissions' && fields.permissions && this.view.mode === 'host') {
      permissionAttempt = ++this.permissionSequence;
      this.pendingPermissions = { sequence: permissionAttempt, value: fields.permissions };
      this.update({ state: { ...this.view.state, permissions: fields.permissions } });
    }
    try {
      return await this.dispatch(c);
    } catch (e) {
      if (permissionAttempt && this.pendingPermissions?.sequence === permissionAttempt) {
        this.pendingPermissions = undefined;
        this.update({
          state: {
            ...this.view.state,
            permissions: this.canonicalPermissions ?? previousPermissions,
          },
        });
      }
      this.notify(errorMessage(e, 'Command failed.'), 'error');
      throw e;
    }
  }
  private dispatch(c: Command) {
    if (this.view.mode === 'host') {
      if (!this.live) return Promise.reject(Error('Companion is disconnected.'));
      return this.bridge!.send(c, 'host', {
        ...this.view.profile,
        name: this.view.profile.name === 'Guest' ? 'Host' : this.view.profile.name,
      });
    } else if (this.view.mode === 'guest' && this.view.status === 'Connected')
      return this.session!.sendCommand(c);
    return Promise.reject(Error('Connect to the host first.'));
  }
  /** Mimic's live view polls; failures are retried by the caller, so they are not notified. */
  peekMimic() {
    return this.dispatch(command('mimicPeek'));
  }
  async publishLayout(organization: Organization) {
    await this.session?.publishLayout(organization);
  }
  async upload(file: File) {
    try {
      if (!file.size || file.size > 16 * 1024 * 1024) throw Error('Choose audio up to 16 MiB.');
      const bytes = new Uint8Array(await file.arrayBuffer());
      let result: unknown;
      if (this.view.mode === 'host' && this.live)
        result = await this.bridge!.upload(
          bytes,
          file.name,
          this.view.profile.name === 'Guest' ? 'Host' : this.view.profile.name,
          true,
        );
      else if (this.view.mode === 'guest') result = await this.session!.upload(bytes, file.name);
      else throw Error('Connect first.');
      this.notify(
        (result as { state?: string })?.state === 'waiting-soundpad'
          ? 'Audio prepared. Open Soundpad to finish its Temp entry.'
          : 'Added to Temp for this session. Only the host can keep it permanently.',
        'success',
      );
    } catch (e) {
      this.notify(errorMessage(e, 'Upload failed.'), 'error');
      throw e;
    }
  }
  async copyInvite() {
    if (!this.view.invite) {
      this.notify('Session is still starting. Try again in a moment.');
      return;
    }
    try {
      await navigator.clipboard.writeText(this.view.invite);
      this.notify('Invitation copied. Anyone with this link can join.', 'success');
    } catch {
      this.notify('Clipboard unavailable. Copy the invitation from Access settings.', 'error');
    }
  }
  async rotate() {
    try {
      await this.hostSession(true);
      this.notify('Invitation replaced. Previous guests have been disconnected.', 'success');
    } catch (e) {
      this.notify(errorMessage(e), 'error');
    }
  }
  disconnect(peer: string) {
    void this.run('revokePeer', { ownerId: peer }).catch(() => {});
    this.session?.disconnect(peer);
  }
  async leave() {
    const wasHost = this.view.mode === 'host';
    try {
      if (wasHost && this.live) {
        try {
          await this.run('endSession');
        } catch {
          this.notify('Hosting ended. Temporary audio cleanup will retry from the companion.');
        }
      }
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
        layouts: [],
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
