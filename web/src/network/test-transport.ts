import type { joinRoom, JsonValue } from 'trystero';

/**
 * Dev server and e2e tests only (2.35): a stand-in for the part of Trystero's room that `session.ts`
 * uses, carried by a BroadcastChannel, so a guest page joins a host page in the same browser without
 * relays or WebRTC. The controller loads it only under `import.meta.env.DEV` and only when a test set
 * localStorage "soundpad-test-transport" to "broadcast"; a production build contains none of it.
 *
 * It keeps Trystero's contract where Session depends on it: peers learn of each other through
 * onPeerJoin/onPeerLeave, every incoming message, request and response passes the action's onReceive
 * gate with its byte length first, a closed peer is gone for both sides, and delivery is in order.
 * It has no encryption and no size limit of its own; the room password is ignored.
 */
export const TEST_TRANSPORT = 'soundpad-broadcast-test-transport';

type Wire = {
  from: string;
  /** Recipient; absent on the room-wide join and leave announcements. */
  to?: string;
  kind: 'join' | 'here' | 'leave' | 'close' | 'message' | 'request' | 'response';
  action?: string;
  data?: unknown;
  metadata?: JsonValue;
  request?: string;
  error?: string;
};
type ReceiveContext = {
  peerId: string;
  byteLength: number;
  kind: 'message' | 'request' | 'response';
  signal: AbortSignal;
  metadata?: JsonValue;
};
type Action = {
  onReceive: ((context: ReceiveContext) => boolean | Promise<boolean>) | null;
  onMessage: ((data: unknown, context: { peerId: string; metadata?: JsonValue }) => unknown) | null;
  onRequest:
    | ((
        data: unknown,
        context: { peerId: string; metadata?: JsonValue; signal: AbortSignal },
      ) => unknown)
    | null;
  onReceiveProgress: null;
};
type Config = Partial<Pick<Action, 'onReceive' | 'onMessage' | 'onRequest'>> & { kind?: string };

const size = (data: unknown) =>
  data instanceof ArrayBuffer || ArrayBuffer.isView(data)
    ? data.byteLength
    : new TextEncoder().encode(JSON.stringify(data ?? null)).byteLength;

function join(config: { appId: string }, roomId: string) {
  const self = transport.id;
  const channel = new BroadcastChannel(`${TEST_TRANSPORT}:${config.appId}:${roomId}`);
  const peers = new Set<string>();
  const actions = new Map<string, Action>();
  const waiting = new Map<
    string,
    { resolve: (data: unknown) => void; reject: (e: Error) => void; timer: number; action: Action }
  >();
  const never = new AbortController().signal;
  let left = false;
  const post = (message: Omit<Wire, 'from'>) => {
    if (!left) channel.postMessage({ ...message, from: self });
  };
  const room = {
    onPeerJoin: null as ((peer: string) => void) | null,
    onPeerLeave: null as ((peer: string) => void) | null,
    makeAction(name: string, options: Config = {}) {
      const action: Action = {
        onReceive: options.onReceive ?? null,
        onMessage: options.onMessage ?? null,
        onRequest: options.onRequest ?? null,
        onReceiveProgress: null,
      };
      actions.set(name, action);
      return Object.assign(action, {
        send: async (
          data: unknown,
          to: { target?: string | string[] | null; metadata?: JsonValue } = {},
        ) => {
          const targets = to.target == null ? [...peers] : [to.target].flat();
          for (const target of targets)
            if (peers.has(target))
              post({ kind: 'message', to: target, action: name, data, metadata: to.metadata });
        },
        request: (
          data: unknown,
          to: { target: string; metadata?: JsonValue; timeoutMs?: number },
        ) =>
          new Promise<unknown>((resolve, reject) => {
            if (!peers.has(to.target)) {
              reject(Error('Peer is not connected.'));
              return;
            }
            const request = crypto.randomUUID();
            const timer = window.setTimeout(() => {
              waiting.delete(request);
              reject(Error('Request timed out.'));
            }, to.timeoutMs ?? 30000);
            waiting.set(request, { resolve, reject, timer, action });
            post({
              kind: 'request',
              to: to.target,
              action: name,
              data,
              metadata: to.metadata,
              request,
            });
          }),
        requestMany: async () => {
          throw Error('The test transport has no requestMany.');
        },
      });
    },
    getPeers: () =>
      Object.fromEntries(
        [...peers].map((peer) => [
          peer,
          {
            close() {
              if (!peers.delete(peer)) return;
              post({ kind: 'close', to: peer });
              room.onPeerLeave?.(peer);
            },
          },
        ]),
      ),
    async leave() {
      if (left) return;
      post({ kind: 'leave' });
      left = true;
      for (const entry of waiting.values()) {
        clearTimeout(entry.timer);
        entry.reject(Error('Left the room.'));
      }
      waiting.clear();
      peers.clear();
      channel.close();
    },
  };
  const meet = (peer: string) => {
    if (peers.has(peer)) return;
    peers.add(peer);
    room.onPeerJoin?.(peer);
  };
  const part = (peer: string) => {
    if (peers.delete(peer)) room.onPeerLeave?.(peer);
  };
  // One message is handled to the end before the next starts, as a data channel delivers them.
  let line = Promise.resolve();
  async function receive(m: Wire) {
    if (left || m.from === self || (m.to !== undefined && m.to !== self)) return;
    if (m.kind === 'join') {
      meet(m.from);
      post({ kind: 'here', to: m.from });
    } else if (m.kind === 'here') meet(m.from);
    else if (m.kind === 'leave' || m.kind === 'close') part(m.from);
    else if (peers.has(m.from)) {
      const context = { peerId: m.from, metadata: m.metadata, signal: never };
      if (m.kind === 'response') {
        const entry = waiting.get(m.request!);
        if (!entry) return;
        waiting.delete(m.request!);
        clearTimeout(entry.timer);
        const allowed =
          (await entry.action.onReceive?.({
            ...context,
            byteLength: size(m.data),
            kind: 'response',
          })) ?? true;
        if (!allowed) entry.reject(Error('Response refused.'));
        else if (m.error !== undefined) entry.reject(Error(m.error));
        else entry.resolve(m.data);
        return;
      }
      const action = actions.get(m.action!);
      if (!action) return;
      const allowed =
        (await action.onReceive?.({ ...context, byteLength: size(m.data), kind: m.kind })) ?? true;
      if (m.kind === 'message') {
        if (allowed) void action.onMessage?.(m.data, context);
        return;
      }
      // A request is answered either way, so the asker does not wait for its timeout. The handler
      // runs off the line: an upload takes seconds, and messages must keep flowing meanwhile.
      const answer = { kind: 'response' as const, to: m.from, request: m.request };
      if (!allowed || !action.onRequest) post({ ...answer, error: 'Request refused.' });
      else
        void Promise.resolve()
          .then(() => action.onRequest!(m.data, context))
          .then(
            (data) => post({ ...answer, data }),
            (e) => post({ ...answer, error: e instanceof Error ? e.message : 'Request failed.' }),
          );
    }
  }
  channel.onmessage = (event: MessageEvent<Wire>) => {
    line = line.then(() => receive(event.data)).catch(() => {});
  };
  // A closed or reloaded page leaves, as a dropped connection would tell the others.
  addEventListener('pagehide', () => void room.leave());
  post({ kind: 'join' });
  return room;
}

const transport = {
  join: join as unknown as typeof joinRoom,
  /** Like Trystero's selfId: new for every page load. */
  id: crypto.randomUUID().replaceAll('-', '').slice(0, 20),
};
export const broadcastTransport = () => transport;
