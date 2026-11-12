import {
  command,
  Disconnected,
  EMPTY,
  cleanName,
  participantSchema,
  type Participant,
  type Command,
  type Snapshot,
} from './protocol';
import { acceptWaveform } from './waveform-cache';
import { waveformSchema } from './protocol';
import {
  LocalBridge,
  type BridgeAddress,
  type HostRole,
  type OwnerAction,
  type SessionInfo,
} from './network/local';
import { Session } from './network/session';
import {
  createIdentity,
  prepareInvite,
  inviteUrl,
  parseInvite,
  type HostIdentity,
  type Invite,
} from './network/identity';
import { notify, type NoticeAction, type NoticeKind } from './notifications';
import { APP_VERSION, companionAtLeast, supports } from './version';
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
  /**
   * Where a host page's guest room runs (2.36): in this tab as before, in this page because it is the
   * companion window's, or elsewhere because this page is a view of the window's (or another tab's) session.
   */
  hosting: 'tab' | 'window' | 'view';
  /** For a view: whether the owner is the companion window (false when another browser tab hosts). */
  ownerWindow: boolean;
};
/** Largest session message an owner sends (2.36), under the companion's 200,000-byte socket limit. */
export const SESSION_INFO_BYTES = 190000;
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
    hosting: 'tab',
    ownerWindow: true,
  };
  private listeners = new Set<() => void>();
  private bridge?: LocalBridge;
  private session?: Session;
  private identity?: HostIdentity;
  private address?: BridgeAddress;
  /**
   * Desktop session (2.36): this page's role from the companion, which sends one before its first state.
   * Unset with an older companion, which never sends one; then this page hosts exactly as before.
   */
  private role?: 'owner' | 'view';
  /** The identity the companion kept for this launch, so a new owner keeps the same invitation. */
  private relayed: HostIdentity | null = null;
  /** Counts role changes, so a guest room that finished starting meanwhile is brought in line. */
  private roleChanges = 0;
  /** The companion window's launch key; only its page has one, kept for its reloads. */
  private ownerKey?: string;
  private clientId?: string;
  /**
   * This page's id toward the companion (2.36): kept across a reload of this tab, so a reloading owner gets the place the
   * companion holds for it back at once. The id is handed over only when the page goes away and taken back on load, so a
   * duplicated tab (which copies session storage while the page is open) gets an id of its own.
   */
  private hostClientId() {
    if (this.clientId) return this.clientId;
    let kept: string | null = null;
    try {
      kept = sessionStorage.getItem('soundpad-client-id');
      sessionStorage.removeItem('soundpad-client-id');
    } catch {
      /* A new id per load still works; only the instant reclaim after a reload is lost. */
    }
    const id = kept && /^[0-9a-f-]{36}$/.test(kept) ? kept : crypto.randomUUID();
    this.clientId = id;
    try {
      addEventListener('pagehide', () => {
        try {
          sessionStorage.setItem('soundpad-client-id', id);
        } catch {
          /* As above. */
        }
      });
    } catch {
      /* Not a browser page (unit tests). */
    }
    return id;
  }
  private publishTimer?: ReturnType<typeof setTimeout>;
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
  /** Dev server only (2.35): an e2e test's stand-in for Trystero; see `start`. Always unset in a build. */
  private transport?: ConstructorParameters<typeof Session>[7];
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
    if (
      this.role === 'owner' &&
      ('invite' in p || 'guests' in p || 'people' in p || 'layouts' in p)
    )
      this.publishSession();
  }
  /**
   * Tells the views what they show about the session (2.36). Guests joining or renaming can come in
   * bursts, so changes are gathered for a quarter second: at most four messages a second.
   */
  private publishSession() {
    if (this.publishTimer) return;
    this.publishTimer = setTimeout(() => {
      this.publishTimer = undefined;
      if (this.role !== 'owner' || !this.session) return;
      const { invite, guests, people, layouts } = this.view;
      const info = {
        type: 'session' as const,
        invite,
        guests,
        people,
        layouts,
        ownerWindow: !!this.ownerKey,
      };
      // The companion ends a socket over any message above 200,000 bytes, which would cost this page
      // its ownership (and the views a promotion loop). Many large shared layouts can pass that, so
      // then the views go without layouts rather than without the session.
      const fits = new TextEncoder().encode(JSON.stringify(info)).byteLength <= SESSION_INFO_BYTES;
      this.bridge?.post(fits ? info : { ...info, layouts: [] });
    }, 250);
  }
  notify = (message: string, kind: NoticeKind = 'info', action?: NoticeAction) =>
    notify(message, kind, action);
  async start() {
    if (this.initialized) return;
    this.initialized = true;
    try {
      // Dev server only (2.35): an e2e test can ask for a transport that needs no network, so a guest
      // page joins a host page in the same browser. A build drops this branch and never has the module.
      if (import.meta.env.DEV) {
        let wanted: string | null = null;
        try {
          wanted = localStorage.getItem('soundpad-test-transport');
        } catch {
          /* No storage: the normal transport. */
        }
        if (wanted === 'broadcast')
          this.transport = (await import('./network/test-transport')).broadcastTransport();
      }
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
        const fromLink = hash.get('host');
        const raw = fromLink || sessionStorage.getItem('soundpad-bridge');
        if (!raw) return;
        const match = /^(\d{1,5})\.([a-f0-9]{64})$/.exec(raw);
        if (!match || +match[1] < 1 || +match[1] > 65535)
          throw Error('Invalid companion launch link. Relaunch the EXE.');
        sessionStorage.setItem('soundpad-bridge', raw);
        // The companion window's page (2.36) also carries its owner key. Kept like the address so a
        // reload stays the owner, and taken out of the address bar with it. A new launch link without
        // one is an ordinary browser tab.
        const owner = fromLink ? hash.get('owner') : sessionStorage.getItem('soundpad-owner-key');
        if (owner && /^[a-f0-9]{64}$/.test(owner)) {
          sessionStorage.setItem('soundpad-owner-key', owner);
          this.ownerKey = owner;
        } else sessionStorage.removeItem('soundpad-owner-key');
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
    if (typeof RTCPeerConnection === 'undefined' && !this.transport) {
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
      this.transport,
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
        if (!this.session && !this.starting && this.role !== 'view')
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
      {
        ownerKey: this.ownerKey,
        role: (role) =>
          void this.takeRole(role).catch((e) => this.notify(errorMessage(e), 'error')),
        session: (info) => this.showSession(info),
        ownerAction: (action) => this.ownerAction(action),
      },
      this.hostClientId(),
    );
  }
  /** The companion named this page the session owner or a view (2.36). */
  private async takeRole({ role, identity }: HostRole) {
    const was = this.role;
    this.role = role;
    this.relayed = identity;
    this.roleChanges++;
    if (role === 'view') {
      clearTimeout(this.publishTimer);
      this.publishTimer = undefined;
      // Demoted (the window took over): its guests reconnect to the window, so they are not this
      // page's to list until the owner's session info arrives.
      this.update({ hosting: 'view', ...(was === 'owner' ? { guests: [], people: {} } : {}) });
    } else {
      this.update({ hosting: this.ownerKey ? 'window' : 'tab' });
      // The companion may hold older info than this page has (after a reconnect), so views get it again.
      this.publishSession();
    }
    await this.reconcile();
  }
  /**
   * Brings this page's guest room in line with its role (2.36). The first state starts it, and a start
   * that is running finishes first.
   */
  private async reconcile() {
    if (this.starting) return;
    // A demoted page leaves its room even while the companion is unreachable: closing a room needs no companion,
    // and a page that kept hosting would answer guests with the same invitation as the new owner (review, 2.36).
    if (this.role === 'view') {
      // Leave the room without revoking anyone: the same invitation goes on in the new owner.
      const session = this.session;
      this.session = undefined;
      await session?.close(false);
      return;
    }
    if (!this.live) return;
    if (!this.session) await this.hostSession(false);
    // A running room is the live invitation: tell the companion again if it holds none or an older one
    // (a Replace invite whose message was lost while the companion was unreachable).
    else if (this.role === 'owner' && this.relayed?.room !== this.identity?.room)
      this.sendIdentity();
  }
  /** Gives the companion this page's identity, which it keeps in memory for the next owner (2.36). */
  private sendIdentity() {
    if (this.role !== 'owner' || !this.identity) return;
    if (this.bridge?.post({ type: 'identity', identity: this.identity }))
      this.relayed = this.identity;
  }
  /** A view shows the owner's invitation, guests and layouts (2.36). */
  private showSession(info: SessionInfo) {
    if (this.role !== 'view') return;
    this.update({
      invite: info.invite,
      guests: info.guests,
      people: info.people,
      layouts: info.layouts,
      ownerWindow: info.ownerWindow !== false,
    });
  }
  /** Replace invite and Disconnect pressed in a view, carried out here in the owner (2.36). */
  private ownerAction(action: OwnerAction) {
    if (this.role !== 'owner' || !this.session) return;
    if (action.action === 'rotate') void this.rotate();
    else if (this.view.guests.includes(action.peer)) this.disconnect(action.peer);
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
    const changes = this.roleChanges;
    try {
      if (rotate) await this.session?.close(true);
      const audienceId = crypto.randomUUID();
      this.bridge!.setAudience(audienceId);
      if (companionAtLeast(this.view.state.companionVersion, 2, 2))
        await this.bridge!.send(command('beginSession', { audienceId }), 'host');
      const key = 'soundpad-identity-' + this.address!.token;
      // A 2.36 companion keeps the launch's identity in memory, so a reloaded, restarted or promoted
      // owner keeps the same invitation and guests reconnect by themselves. This tab's own copy is the
      // fallback for older companions.
      const relayed = rotate ? null : this.relayed;
      // A 2.36 companion (it sent a role) is the only keeper of the identity, so an invitation that End hosting
      // ended cannot come back from this tab's copy; the copy is for older companions (review, 2.36).
      const stored = rotate || relayed || this.role ? null : sessionStorage.getItem(key);
      this.identity = relayed
        ? { ...relayed }
        : stored
          ? JSON.parse(stored)
          : await createIdentity();
      await prepareInvite(this.identity!);
      sessionStorage.setItem(key, JSON.stringify(this.identity));
      if (this.relayed?.room !== this.identity!.room) this.sendIdentity();
      // A room this page has left (a Replace invite, or the window taking over in 2.36) can still
      // report peers leaving; only the current one may change the guest list a view is shown.
      const current = () => this.session === session;
      const session: Session = new Session(
        this.identity!,
        this.identity,
        () => this.view.state,
        (c, peer, person) => this.bridge!.send(c, 'guest', person, peer),
        () => {},
        () => {},
        (guests) => current() && this.update({ guests }),
        this.transport,
        {
          get: () => this.view.profile,
          changed: (people) => current() && this.update({ people }),
        },
        {
          layouts: (layouts) => current() && this.update({ layouts }),
          upload: (bytes, name, by, peer) => this.bridge!.upload(bytes, name, by, false, peer),
        },
      );
      this.session = session;
      this.update({ invite: inviteUrl(this.identity!), guests: [] });
    } finally {
      this.starting = false;
      // The role changed while the room started (the window took over, say).
      if (changes !== this.roleChanges)
        void this.reconcile().catch((e) => this.notify(errorMessage(e), 'error'));
    }
  }
  /** Runs a command; a refusal is shown as an error notice unless `quiet` (the caller reports it). */
  async run(op: Command['op'], fields: Partial<Command> = {}, { quiet = false } = {}) {
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
      if (!quiet) this.notify(errorMessage(e, 'Command failed.'), 'error');
      throw e;
    }
  }
  /**
   * Website version of the host's tab, which relays a guest's commands: this page for the host, what
   * the host's welcome reported for a guest ('' from a tab older than 2.35).
   */
  hostSite() {
    return this.view.mode === 'host' ? APP_VERSION : (this.session?.hostSite ?? '');
  }
  private dispatch(c: Command) {
    if (this.view.mode === 'host') {
      if (!this.live) return Promise.reject(new Disconnected('Companion is disconnected.'));
      return this.bridge!.send(c, 'host', {
        ...this.view.profile,
        name: this.view.profile.name === 'Guest' ? 'Host' : this.view.profile.name,
      });
    } else if (this.view.mode === 'guest' && this.view.status === 'Connected')
      return this.session!.sendCommand(c);
    return Promise.reject(new Disconnected('Connect to the host first.'));
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
      const send = () => {
        if (this.view.mode === 'host' && this.live)
          return this.bridge!.upload(
            bytes,
            file.name,
            this.view.profile.name === 'Guest' ? 'Host' : this.view.profile.name,
            true,
          );
        if (this.view.mode === 'guest') return this.session!.upload(bytes, file.name);
        throw Error('Connect first.');
      };
      // The companion refuses an upload while two other audio jobs wait ("busy; retry shortly"), e.g.
      // during a reverse. Retry a few times instead of making the person do it (2.34.1).
      let result: unknown;
      for (let attempt = 0; ; attempt++) {
        try {
          result = await send();
          break;
        } catch (e) {
          if (attempt >= 5 || !(e instanceof Error) || !/busy; retry shortly/i.test(e.message))
            throw e;
          await new Promise((r) => setTimeout(r, 1500));
        }
      }
      // Undo (2.33): the wrong file goes straight back out, for the host and (companion 2.32+) for the
      // guest who uploaded it. The remove step is loaded on demand; it imports this controller.
      const assetId = (result as { assetId?: unknown })?.assetId;
      const undo =
        typeof assetId === 'string' &&
        (this.view.mode === 'host' || supports(this.view.state.companionVersion, 'guestUndo'))
          ? {
              label: 'Undo',
              run: () =>
                void import('./remove-temp')
                  .then((m) => m.removeNewTempAudio(assetId))
                  .then(() => this.notify('Upload removed from Temp.', 'success'))
                  .catch(() => {}),
            }
          : undefined;
      this.notify(
        (result as { state?: string })?.state === 'waiting-soundpad'
          ? 'Audio prepared. Open Soundpad to finish its Temp entry.'
          : 'Added to Temp for this session. Only the host can keep it permanently.',
        'success',
        undo,
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
    if (this.role === 'view') {
      // The owner replaces it (2.36); this view shows the new invitation when the owner reports it.
      if (this.bridge?.post({ type: 'ownerAction', action: 'rotate' }))
        this.notify(
          this.view.ownerWindow
            ? 'Replacing the invitation in the Soundpad Web window.'
            : 'Replacing the invitation in the hosting tab.',
        );
      else this.notify('Companion is disconnected.', 'error');
      return;
    }
    // One replacement at a time: two overlapping ones (a press here and one relayed from a view) would leave a
    // second room running with its own working invitation (review, 2.36).
    if (this.starting) {
      this.notify('The invitation is already being replaced. Try again in a moment.');
      return;
    }
    try {
      await this.hostSession(true);
      this.notify('Invitation replaced. Previous guests have been disconnected.', 'success');
    } catch (e) {
      this.notify(errorMessage(e), 'error');
    }
  }
  disconnect(peer: string) {
    if (this.role === 'view') {
      // Only the owner holds the guest's connection (2.36); it also revokes the guest's audio there.
      if (!this.bridge?.post({ type: 'ownerAction', action: 'disconnect', peer }))
        this.notify('Companion is disconnected.', 'error');
      return;
    }
    void this.run('revokePeer', { ownerId: peer }).catch(() => {});
    this.session?.disconnect(peer);
  }
  async leave() {
    const wasHost = this.view.mode === 'host';
    try {
      // A view never ends the session (2.36): the owner runs it, and End hosting is not offered there.
      if (wasHost && this.live && this.role !== 'view') {
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
      clearTimeout(this.publishTimer);
      this.publishTimer = undefined;
      this.role = undefined;
      this.relayed = null;
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
        hosting: 'tab',
        ownerWindow: true,
      });
    }
  }
}
export const controller = new Controller();
