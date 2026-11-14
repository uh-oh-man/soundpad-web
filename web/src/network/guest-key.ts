/**
 * A guest browser's own key (2.41, review of 2026-11-14). Up to 2.40 a guest was known to the host by the
 * participant id its page declared, so anyone could say they were someone else. Now each guest browser keeps an
 * ECDSA P-256 key pair: the private key is created non-extractable and kept in IndexedDB, so a page can sign with
 * it but nothing can read it out. In every hello the guest sends the public key and a signature over the host's
 * challenge for that connection (see `guestProof`), and the host derives the guest's identity from the public key
 * (`guestIdOf`). Admission, places, permissions, history join times, Keep budgets and drafts all follow that
 * identity, never the display name or the declared profile id.
 *
 * Clearing the browser's site data makes a new key, so that guest is simply new to the host, as before. A browser
 * that cannot keep the key (storage blocked, a private window that refuses IndexedDB) gets one for this page only
 * and is told so (`durable: false`).
 */
import { decode, encode } from './identity';

export type GuestKey = {
  /** The public key (SPKI), base64url. */
  publicKey: string;
  privateKey: CryptoKey;
  /** False when this browser could not keep the key: it lasts until the page closes. */
  durable: boolean;
};
const ALGORITHM = { name: 'ECDSA', namedCurve: 'P-256' } as const;
const SIGN = { name: 'ECDSA', hash: 'SHA-256' } as const;
const DATABASE = 'soundpad-web-keys';
const STORE = 'keys';
const RECORD = 'guest-v1';
/** A public key as a hello may carry it (a P-256 SPKI is 91 bytes, 122 characters). */
export const GUEST_KEY_PATTERN = /^[-\w]{100,200}$/;
/** A host challenge: 32 random bytes, base64url. */
export const CHALLENGE_PATTERN = /^[-\w]{43}$/;

/** A new key pair; the private key can sign but can never be exported. */
export async function createGuestKey(durable = false): Promise<GuestKey> {
  const pair = await crypto.subtle.generateKey(ALGORITHM, false, ['sign', 'verify']);
  // The public half of a pair is always exportable.
  const publicKey = encode(new Uint8Array(await crypto.subtle.exportKey('spki', pair.publicKey)));
  return { publicKey, privateKey: pair.privateKey, durable };
}

function request<T>(r: IDBRequest<T>) {
  return new Promise<T>((resolve, reject) => {
    r.onsuccess = () => resolve(r.result);
    r.onerror = () => reject(r.error ?? Error('Storage failed.'));
  });
}
async function fromStorage(): Promise<GuestKey> {
  const record = recordName();
  const open = indexedDB.open(DATABASE, 1);
  open.onupgradeneeded = () => {
    if (!open.result.objectStoreNames.contains(STORE)) open.result.createObjectStore(STORE);
  };
  const db = await request(open);
  try {
    const valid = (x: unknown): x is { v: 1; publicKey: string; privateKey: CryptoKey } =>
      !!x &&
      typeof x === 'object' &&
      (x as { v?: unknown }).v === 1 &&
      typeof (x as { publicKey?: unknown }).publicKey === 'string' &&
      GUEST_KEY_PATTERN.test((x as { publicKey: string }).publicKey) &&
      (x as { privateKey?: unknown }).privateKey instanceof CryptoKey;
    const read = db.transaction(STORE, 'readonly').objectStore(STORE);
    const found: unknown = await request(read.get(record));
    if (valid(found))
      return { publicKey: found.publicKey, privateKey: found.privateKey, durable: true };
    // Made outside the transaction (it would close while waiting for the key), then stored only if another tab
    // did not store one meanwhile, so every tab of this browser ends up with the same key.
    const made = await createGuestKey(true);
    const store = db.transaction(STORE, 'readwrite').objectStore(STORE);
    const again: unknown = await request(store.get(record));
    if (valid(again))
      return { publicKey: again.publicKey, privateKey: again.privateKey, durable: true };
    await request(
      store.put({ v: 1, publicKey: made.publicKey, privateKey: made.privateKey }, record),
    );
    return made;
  } finally {
    db.close();
  }
}

/**
 * The stored key's name. On the dev server only, an e2e test can give one tab its own key (this tab's session storage
 * names it): its guests share one browser, and so one key, but stand for different people. A build drops this.
 */
function recordName() {
  if (import.meta.env.DEV)
    try {
      const own = sessionStorage.getItem('soundpad-test-guest-key');
      if (own && /^[-\w]{1,64}$/.test(own)) return `${RECORD}:${own}`;
    } catch {
      /* No session storage: the browser's key. */
    }
  return RECORD;
}

let loaded: Promise<GuestKey> | undefined;
/**
 * This browser's guest key: kept in IndexedDB, or for this page only when the browser refuses to keep it. With no
 * IndexedDB at all (tests under Node) every call makes a new key, so separate sessions are separate guests.
 */
export function loadGuestKey(): Promise<GuestKey> {
  if (typeof indexedDB === 'undefined') return createGuestKey(false);
  loaded ??= fromStorage().catch(() => createGuestKey(false));
  return loaded;
}

/**
 * The identity a host gives the holder of `publicKey`: a UUID (version 8) from a hash of the key, so it fits
 * everywhere a participant id did (session settings, the companion's budgets, drafts and history join times).
 */
export async function guestIdOf(publicKey: string) {
  const label = new TextEncoder().encode('soundpad-guest-id-v1');
  const bytes = new Uint8Array(
    await crypto.subtle.digest(
      'SHA-256',
      new Uint8Array([...label, ...decode(publicKey)]) as Uint8Array<ArrayBuffer>,
    ),
  ).slice(0, 16);
  bytes[6] = (bytes[6] & 0x0f) | 0x80;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  const hex = [...bytes].map((b) => b.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

/**
 * What a guest signs: the invitation's room, this page load's hello nonce, the host's challenge for this one
 * connection and both ends' peer ids (relay peers named as such). A proof made for one connection, host page or
 * session is worth nothing on any other.
 */
export function guestProof(
  room: string,
  nonce: string,
  challenge: string,
  host: string,
  guest: string,
) {
  return new TextEncoder().encode(
    JSON.stringify([2, 'guest', room, nonce, challenge, host, guest]),
  );
}
export async function signGuest(key: GuestKey, proof: Uint8Array) {
  return encode(
    new Uint8Array(
      await crypto.subtle.sign(SIGN, key.privateKey, proof as Uint8Array<ArrayBuffer>),
    ),
  );
}
export async function verifyGuest(publicKey: string, signature: string, proof: Uint8Array) {
  try {
    if (!GUEST_KEY_PATTERN.test(publicKey) || !/^[-\w]{80,200}$/.test(signature)) return false;
    const key = await crypto.subtle.importKey('spki', decode(publicKey), ALGORITHM, false, [
      'verify',
    ]);
    return await crypto.subtle.verify(
      SIGN,
      key,
      decode(signature),
      proof as Uint8Array<ArrayBuffer>,
    );
  } catch {
    return false;
  }
}
/** A new challenge for one connection. */
export const newChallenge = () => encode(crypto.getRandomValues(new Uint8Array(32)));
