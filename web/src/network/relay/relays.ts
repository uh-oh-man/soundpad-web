/**
 * Relays the relay link uses unless told otherwise.
 *
 * Every entry also appears in Trystero's built-in Nostr list
 * (`node_modules/@trystero-p2p/nostr/dist/index.mjs`, `defaultRelayUrls`), which the app already
 * uses for discovery, so the fallback path introduces no new third party. Both ends of a link must
 * use the same list: each side keeps the relays that pass its own self-check, and messages flow
 * through the relays both sides kept.
 */
export const DEFAULT_RELAYS = [
  'wss://cdn.satellite.earth',
  'wss://relay.routstr.com',
  'wss://nostr.stakey.net',
  'wss://nos.lol',
  'wss://nostr.robosats.org',
  'wss://relay.degmods.com',
];
