/**
 * Relay fallback transport: the app's own encrypted messages carried over public Nostr relays when
 * a direct WebRTC connection cannot be made. Start at `createRelayLink` in `link.ts`.
 */
export { createRelayLink } from './link';
export type {
  DropCounts,
  JsonValue,
  RelayLink,
  RelayLinkOptions,
  RelayLinkState,
  RelayLinkStatus,
  RelayPeer,
  RelayRole,
  SendOptions,
  SendResult,
} from './link';
export { DEFAULT_RELAYS } from './relays';
export { DEFAULT_TUNING, RelayPool, createSigner, hasValidId, signEvent } from './nostr';
export type {
  Lane,
  NostrEvent,
  PoolDebug,
  PoolOptions,
  PoolTuning,
  Publication,
  PublishResult,
  RelayHealth,
  RelayState,
  WebSocketFactory,
  WebSocketLike,
} from './nostr';
