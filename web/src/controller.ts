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
import { acceptWaveform, clearWaveforms } from './waveform-cache';
import { waveformSchema } from './protocol';
import {
  LocalBridge,
  LOBBY_VIEW_MAX,
  type BridgeAddress,
  type HostRole,
  type OwnerAction,
  type SessionInfo,
} from './network/local';
import {
  Session,
  type ConnectionInfo,
  type LobbyRequest,
  type RelayFactory,
  type Transport,
} from './network/session';
import { createRelayLink } from './network/relay';
import { NO_WEBRTC } from './network/diagnostics';
import {
  DEFAULT_POLICY,
  forNewInvite,
  LIST,
  MAX_PLACES,
  readPolicy,
  type PermissionKey,
  type SessionPolicy,
} from './network/policy';
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
// Types only: the example session's code is loaded on demand (see `startDemo`), never with this page.
import type { DemoModel } from './demo/model';
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
  /** How this page is connected (2.37): transport, relay health, ICE findings, the diagnosis. */
  connection?: ConnectionInfo;
  /** Host (2.37): guests waiting for Let in or Turn away. */
  requests: LobbyRequest[];
  /** Host (2.37): admission, guest places and per-guest permissions. */
  policy: SessionPolicy;
  /** Host (2.37): each listed guest connection (by peer id) is direct or through the relay. */
  links: Record<string, Transport>;
  /** The example session: a host page that runs entirely in this browser; nothing reaches Soundpad. */
  demo: boolean;
};
/** What Settings shows as the example's invitation; it is not a link and nothing can join with it. */
export const DEMO_INVITE = 'Example session: there is no invitation to share.';
/** What the guest room offers the session controls; the example session stands in for it. */
type Room = Pick<Session, 'letIn' | 'turnAway' | 'setPolicy' | 'getPolicy' | 'disconnect'>;
/** Largest session settings message a 2.37 companion keeps (LocalServer.MaxSessionSettingsBytes). */
export const SESSION_SETTINGS_BYTES = 65536;
/** Shown when a view is asked to change who is in the session; only the owner page can (2.37). */
export const OWNER_ONLY = {
  window: 'Change who is in the session from the Soundpad Web window.',
  tab: 'Change who is in the session from the tab that hosts it.',
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
/** Shown to a guest whose browser has no WebRTC (2.37: such a guest still joins through the relay). */
export { NO_WEBRTC };

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
    requests: [],
    policy: { ...DEFAULT_POLICY },
    links: {},
    demo: false,
  };
  private listeners = new Set<() => void>();
  private bridge?: LocalBridge;
  private session?: Session;
  /** The example session, when one runs; then there is no bridge and no guest room. */
  private demo?: DemoModel;
  private demoStarting = false;
  /** The guest room, or the example standing in for it. */
  private room(): Room | undefined {
    return this.demo ?? this.session;
  }
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
  private testRelay?: RelayFactory;
  /**
   * The relay link both roles use (2.37): the app's own encrypted messages over the public Nostr
   * relays Trystero already uses for discovery, for guests whose network blocks direct WebRTC.
   */
  private relayFactory(): RelayFactory {
    return this.testRelay ?? ((options) => createRelayLink(options));
  }
  /**
   * Admission, guest places and per-guest permissions (2.37). Kept here so a Replace invite keeps
   * them, and with a 2.37 companion also there, so a reload or another owner page keeps them too.
   */
  private policy: SessionPolicy = { ...DEFAULT_POLICY };
  private settingsTimer?: ReturnType<typeof setTimeout>;
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
      ('invite' in p ||
        'guests' in p ||
        'people' in p ||
        'layouts' in p ||
        'requests' in p ||
        'policy' in p ||
        'links' in p)
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
      const { invite, guests, people, layouts, requests, policy, links } = this.view;
      const info = {
        type: 'session' as const,
        invite,
        guests,
        people,
        layouts,
        ownerWindow: !!this.ownerKey,
        // What views show about the lobby and each guest (2.37); older views ignore it.
        // At most what the views accept (local.ts), so a crowded lobby never costs them the session info.
        control: { requests: requests.slice(0, LOBBY_VIEW_MAX), policy, links },
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
        if (wanted === 'broadcast') {
          const test = await import('./network/test-transport');
          this.transport = test.broadcastTransport();
          // Its relay too (2.37), so a test page never opens public relays.
          this.testRelay = test.broadcastRelay();
        }
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
    // old browser) say so at once instead of waiting for the generic "Host unavailable" after 25 s;
    // since 2.37 such a guest still gets in through the relay, and the message goes when it does.
    const webrtc = typeof RTCPeerConnection !== 'undefined' || !!this.transport;
    this.update({
      mode: 'guest',
      status: webrtc ? 'Connecting' : NO_WEBRTC,
      needsName: false,
      connection: undefined,
    });
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
      undefined,
      {
        relay: this.relayFactory(),
        direct: webrtc,
        connection: (connection) => this.guestConnection(connection),
      },
    );
  }
  /** A guest's connection changed (2.37). "Reconnected" means the same session came back. */
  private guestConnection(connection: ConnectionInfo) {
    this.update({ connection });
    if (connection.reconnected)
      this.notify(
        connection.transport === 'relay'
          ? 'Reconnected to the host (relayed, slower).'
          : 'Reconnected to the host.',
        'success',
      );
  }
  /** Guest (2.37): asks a full session's host for an extra place. */
  askForPlace() {
    this.session?.askForPlace();
  }
  /** Guest (2.37): asks again after the host did not answer in time. */
  askAgain() {
    this.session?.askAgain();
  }
  /** Runs the network check of Connection details again (2.37). */
  async checkNetwork() {
    await this.session?.checkNetwork();
  }
  /**
   * "Try the example" on the landing page: a host session that runs entirely in this page, with
   * example sounds played by this browser. It never contacts the companion, Soundpad or any relay.
   * Its code and sounds load only now, so the normal page never downloads them.
   */
  async startDemo() {
    if (this.view.mode !== 'landing' || this.demoStarting) return;
    this.demoStarting = true;
    const before = this.view.status;
    this.update({ status: 'Loading the example' });
    try {
      const { startDemo } = await import('./demo');
      let demo: DemoModel | undefined = undefined;
      const current = () => !!demo && this.demo === demo;
      demo = await startDemo({
        state: (state) => current() && this.update({ state: this.visibleState(state) }),
        session: ({ guests, people, links, requests, policy }) => {
          if (!current()) return;
          this.policy = policy;
          this.update({ guests, people, links, requests, policy });
        },
        notify: (message, kind) => current() && this.notify(message, kind),
      });
      if (this.view.mode !== 'landing') {
        demo.close();
        return;
      }
      this.demo = demo;
      this.live = true;
      this.policy = demo.getPolicy();
      this.update({
        mode: 'host',
        status: 'Host ready',
        demo: true,
        invite: DEMO_INVITE,
        hosting: 'tab',
        ownerWindow: true,
        guests: [],
        people: {},
        links: {},
        requests: [],
        layouts: [],
        policy: this.policy,
        connection: undefined,
      });
      demo.begin();
    } catch (e) {
      this.update({ status: before });
      this.notify(`The example could not start. ${errorMessage(e)}`, 'error');
    } finally {
      this.demoStarting = false;
    }
  }
  /** Leaves the example for the landing page; the example forgets everything that happened in it. */
  private endDemo() {
    const demo = this.demo;
    this.demo = undefined;
    this.live = false;
    demo?.close();
    clearWaveforms();
    this.policy = { ...DEFAULT_POLICY };
    this.pendingPermissions = undefined;
    this.update({
      layouts: [],
      mode: 'landing',
      state: EMPTY,
      status: 'Not connected',
      guests: [],
      people: {},
      invite: '',
      demo: false,
      connection: undefined,
      requests: [],
      policy: this.policy,
      links: {},
    });
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
  private async takeRole({ role, identity, settings }: HostRole) {
    const was = this.role;
    this.role = role;
    this.relayed = identity;
    this.roleChanges++;
    // The settings the companion kept for this launch (2.37): a reloaded or newly promoted owner
    // carries on with them. A page whose room is already running has the newer copy and sends it.
    if (role === 'owner') {
      if (!this.session && settings) this.showPolicy(readPolicy(settings), false);
      else if (this.session) this.saveSettings();
    }
    if (role === 'view') {
      clearTimeout(this.publishTimer);
      this.publishTimer = undefined;
      // Demoted (the window took over): its guests reconnect to the window, so they are not this
      // page's to list until the owner's session info arrives.
      this.update({
        hosting: 'view',
        ...(was === 'owner' ? { guests: [], people: {}, requests: [], links: {} } : {}),
      });
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
  /** A view shows the owner's invitation, guests and layouts (2.36), and its lobby (2.37). */
  private showSession(info: SessionInfo) {
    if (this.role !== 'view') return;
    this.update({
      invite: info.invite,
      guests: info.guests,
      people: info.people,
      layouts: info.layouts,
      ownerWindow: info.ownerWindow !== false,
      requests: info.control?.requests ?? [],
      policy: info.control ? readPolicy(info.control.policy) : { ...DEFAULT_POLICY },
      links: info.control?.links ?? {},
    });
  }
  /** Shows new settings and, with a 2.37 companion, keeps them there for this launch (2.37). */
  private showPolicy(policy: SessionPolicy, save = true) {
    this.policy = policy;
    this.update({ policy });
    if (save) this.saveSettings();
  }
  /**
   * Sends the settings to the companion, which keeps them like the identity (2.37). Admitting guests
   * changes them in bursts, so changes are gathered for a quarter second. An older companion would
   * not keep them, so it is not sent any; they then live in this page only.
   */
  private saveSettings() {
    if (this.settingsTimer) return;
    this.settingsTimer = setTimeout(() => {
      this.settingsTimer = undefined;
      if (this.role !== 'owner' || !supports(this.view.state.companionVersion, 'sessionControl'))
        return;
      const settings = this.policy;
      if (new TextEncoder().encode(JSON.stringify(settings)).byteLength > SESSION_SETTINGS_BYTES)
        return;
      this.bridge?.post({ type: 'sessionSettings', settings });
    }, 250);
  }
  /** True when this page may change who is in the session; a view says where to do it instead. */
  private owns() {
    if (this.role !== 'view') return !!this.room();
    this.notify(this.view.ownerWindow ? OWNER_ONLY.window : OWNER_ONLY.tab);
    return false;
  }
  /** Host (2.37): lets a waiting guest in, or a turned-away one back. */
  letIn(id: string) {
    if (!this.owns()) return;
    const away = this.policy.turnedAway.find((p) => p.id === id);
    void this.room()!
      .letIn(id)
      .then(() => {
        if (away) this.notify(`${away.name} can join again when they open the invitation.`);
      })
      .catch((e) => this.notify(errorMessage(e), 'error'));
  }
  /** Host (2.37): turns a waiting guest away. */
  turnAway(id: string) {
    if (!this.owns()) return;
    this.room()!.turnAway(id);
    // The companion forgets when they were let in before, so a later admission counts from then (2.39).
    if (!this.demo && supports(this.view.state.companionVersion, 'history'))
      void this.run('revokePeer', { guestId: id }).catch(() => {});
  }
  /** Host (2.37): "Let guests in at once" or "Ask me first". */
  setAdmission(admission: SessionPolicy['admission']) {
    if (this.owns()) this.room()!.setPolicy({ ...this.room()!.getPolicy(), admission });
  }
  /** Host (2.37): how many guests may be in at once, 1 to 12 (extra places come on top). */
  setCapacity(capacity: number) {
    if (!this.owns() || !Number.isInteger(capacity) || capacity < 1 || capacity > MAX_PLACES)
      return;
    this.room()!.setPolicy({ ...this.room()!.getPolicy(), capacity });
  }
  /**
   * Host (2.37): one guest's own setting for one permission; undefined is "Same as everyone". Keyed
   * by the guest's participant id, so it follows them through reloads and reconnects.
   */
  setGuestPermission(peer: string, key: PermissionKey, value: boolean | undefined) {
    const id = this.view.people[peer]?.id;
    if (!id || !this.owns()) return;
    const policy = this.room()!.getPolicy();
    const own = { ...policy.overrides[id] };
    if (value === undefined) delete own[key];
    else own[key] = value;
    const overrides = { ...policy.overrides };
    delete overrides[id];
    if (Object.keys(own).length) overrides[id] = own;
    // The newest choices win when the list is at its bound, so the companion never refuses it.
    this.room()!.setPolicy({
      ...policy,
      overrides: Object.fromEntries(Object.entries(overrides).slice(-LIST)),
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
        // newInvite (2.39): only a new invitation starts the recording history's guest join times and shares over;
        // a reload, a window taking over or a promoted view keeps them.
        await this.bridge!.send(
          command('beginSession', { audienceId, ...(rotate && { newInvite: true }) }),
          'host',
        );
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
      // The relay link reports its state while the room is still being constructed (2.37), so a call
      // before `session` is assigned comes from this new room and counts as current.
      let constructing = true;
      const current = () => constructing || this.session === session;
      // A new invitation starts with nobody admitted or turned away; the host's choices stay (2.37).
      if (rotate) this.policy = forNewInvite(this.policy);
      const session: Session = new Session(
        this.identity!,
        this.identity,
        () => this.view.state,
        (c, peer, person, joinedAt) => this.bridge!.send(c, 'guest', person, peer, joinedAt),
        () => {},
        () => {},
        (guests, links) => current() && this.update({ guests, links: links ?? {} }),
        this.transport,
        {
          get: () => this.view.profile,
          changed: (people) => current() && this.update({ people }),
        },
        {
          layouts: (layouts) => current() && this.update({ layouts }),
          upload: (bytes, name, by, peer) => this.bridge!.upload(bytes, name, by, false, peer),
        },
        undefined,
        {
          relay: this.relayFactory(),
          policy: this.policy,
          policyChanged: (policy) => current() && this.showPolicy(policy),
          lobby: (requests) => current() && this.update({ requests }),
          connection: (connection) => current() && this.update({ connection }),
        },
      );
      this.session = session;
      constructing = false;
      this.update({
        invite: inviteUrl(this.identity!),
        guests: [],
        links: {},
        requests: [],
        policy: this.policy,
      });
      if (rotate) this.saveSettings();
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
    if (this.demo)
      return this.demo.send(c, {
        ...this.view.profile,
        name: this.view.profile.name === 'Guest' ? 'Host' : this.view.profile.name,
      });
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
        if (this.demo)
          return this.demo.upload(bytes, file.name, {
            ...this.view.profile,
            name: this.view.profile.name === 'Guest' ? 'Host' : this.view.profile.name,
          });
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
    if (this.demo) {
      this.notify(
        'This is an example, so there is no invitation to share. To host for real, start the companion on your PC.',
      );
      return;
    }
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
    if (this.demo) {
      this.demo.rotate();
      this.notify('Invitation replaced. Previous guests have been disconnected.', 'success');
      return;
    }
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
    // guestId (2.39): their recording-history join time ends, so letting them back in counts from then.
    const guestId = this.view.people[peer]?.id;
    void this.run('revokePeer', { ownerId: peer, ...(guestId && { guestId }) }).catch(() => {});
    this.room()?.disconnect(peer);
  }
  async leave() {
    if (this.demo) return this.endDemo();
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
      clearTimeout(this.settingsTimer);
      this.settingsTimer = undefined;
      // End hosting forgets the session's settings, as the companion does (2.37).
      this.policy = { ...DEFAULT_POLICY };
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
        connection: undefined,
        requests: [],
        policy: this.policy,
        links: {},
      });
    }
  }
}
export const controller = new Controller();
