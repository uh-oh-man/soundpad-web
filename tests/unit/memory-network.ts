// A stand-in for the part of Trystero's room that `session.ts` uses: peers meet when they join the same
// room with the same password, and messages are delivered as JSON copies in order. `blocked` (2.37)
// stands for networks that cannot open a direct WebRTC connection: nobody meets and nothing is
// delivered until `heal()`, when every pair meets as if the connection had just opened.
import type { joinRoom } from 'trystero';

type Handler = (data: unknown, context: { peerId: string }) => void;
export type MemoryRoom = {
  id: string;
  handler?: Handler;
  onPeerJoin?: (id: string) => void;
  onPeerLeave?: (id: string) => void;
  makeAction: () => {
    send: (data: unknown, options: { target: string }) => Promise<void>;
    onMessage: Handler | null;
  };
  getPeers: () => Record<string, { close: () => void }>;
  leave: () => Promise<void>;
};
export class MemoryNetwork {
  groups = new Map<string, Map<string, MemoryRoom>>();
  constructor(public blocked = false) {}
  /** Direct connections start working: every pair in every room meets. */
  heal() {
    this.blocked = false;
    for (const peers of this.groups.values())
      for (const a of peers.values())
        for (const b of peers.values()) if (a.id !== b.id) a.onPeerJoin?.(b.id);
  }
  transport(id: string) {
    return {
      id,
      join: ((config: { password: string }, roomId: string) => {
        const key = roomId + config.password;
        const peers = this.groups.get(key) ?? new Map<string, MemoryRoom>();
        this.groups.set(key, peers);
        const room: MemoryRoom = {
          id,
          makeAction: () => {
            const action = {
              send: async (data: unknown, options: { target: string }) => {
                const target = peers.get(options.target);
                if (target && !this.blocked)
                  queueMicrotask(() =>
                    target.handler?.(JSON.parse(JSON.stringify(data)), { peerId: id }),
                  );
              },
              get onMessage() {
                return room.handler ?? null;
              },
              set onMessage(f: Handler | null) {
                room.handler = f ?? undefined;
              },
            };
            return action;
          },
          getPeers: () =>
            this.blocked
              ? {}
              : Object.fromEntries(
                  [...peers.keys()]
                    .filter((x) => x !== id)
                    .map((x) => [
                      x,
                      {
                        close: () => {
                          peers.delete(x);
                          room.onPeerLeave?.(x);
                        },
                      },
                    ]),
                ),
          leave: async () => {
            peers.delete(id);
            if (!this.blocked) for (const p of peers.values()) p.onPeerLeave?.(id);
          },
        };
        peers.set(id, room);
        queueMicrotask(() => {
          if (this.blocked) return;
          for (const p of peers.values())
            if (p.id !== id) {
              p.onPeerJoin?.(id);
              room.onPeerJoin?.(p.id);
            }
        });
        return room;
      }) as unknown as typeof joinRoom,
    };
  }
}
