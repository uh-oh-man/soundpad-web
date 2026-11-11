/** Byte helpers for the relay transport. Everything here is synchronous and browser-safe. */

/** A byte array WebCrypto accepts without a cast (TypeScript 5.9 separates SharedArrayBuffer). */
export type Bytes = Uint8Array<ArrayBuffer>;

const encoder = new TextEncoder();
// Fatal, so a packet with broken UTF-8 is dropped instead of delivered with replacement characters.
const decoder = new TextDecoder('utf-8', { fatal: true });

export const utf8 = (text: string): Bytes => encoder.encode(text) as Bytes;
/** Decodes UTF-8; throws on malformed input. */
export const fromUtf8 = (bytes: Uint8Array) => decoder.decode(bytes);

/** Copies any byte view into a plain `Bytes`, so later changes by the caller cannot reach a queued send. */
export function copy(bytes: Uint8Array): Bytes {
  const out = new Uint8Array(bytes.length);
  out.set(bytes);
  return out;
}

export function concat(...parts: Uint8Array[]): Bytes {
  const out = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
  let at = 0;
  for (const p of parts) {
    out.set(p, at);
    at += p.length;
  }
  return out;
}

export function equal(a: Uint8Array, b: Uint8Array) {
  if (a.length !== b.length) return false;
  for (let i = 0; i < a.length; i++) if (a[i] !== b[i]) return false;
  return true;
}

export function toHex(bytes: Uint8Array) {
  let out = '';
  for (const b of bytes) out += b.toString(16).padStart(2, '0');
  return out;
}

/** Standard base64. Works in slices because `String.fromCharCode(...bytes)` overflows the stack on large inputs. */
export function toBase64(bytes: Uint8Array) {
  let binary = '';
  for (let i = 0; i < bytes.length; i += 0x8000)
    binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(binary);
}

/** Returns `undefined` for anything that is not base64 (relays can hand us arbitrary text). */
export function fromBase64(text: string): Bytes | undefined {
  try {
    const binary = atob(text);
    const out = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) out[i] = binary.charCodeAt(i);
    return out;
  } catch {
    return undefined;
  }
}

/** Base64url without padding, the form the rest of the app uses for keys shown to people. */
export const toBase64Url = (bytes: Uint8Array) =>
  toBase64(bytes).replaceAll('+', '-').replaceAll('/', '_').replace(/=+$/, '');

/** Builds a binary packet. Integers are big-endian. */
export class Writer {
  private parts: Bytes[] = [];
  u8(n: number) {
    this.parts.push(Uint8Array.of(n & 0xff));
    return this;
  }
  u16(n: number) {
    this.parts.push(Uint8Array.of((n >>> 8) & 0xff, n & 0xff));
    return this;
  }
  u32(n: number) {
    const b = new Uint8Array(4);
    new DataView(b.buffer).setUint32(0, n >>> 0);
    this.parts.push(b);
    return this;
  }
  /** A non-negative safe integer (sequence numbers, millisecond timestamps) as 8 bytes. */
  u64(n: number) {
    return this.u32(Math.floor(n / 0x100000000)).u32(n % 0x100000000);
  }
  bytes(b: Uint8Array) {
    this.parts.push(b as Bytes);
    return this;
  }
  /** Bytes with a one-byte length in front (at most 255 bytes). */
  short(b: Uint8Array) {
    if (b.length > 255) throw RangeError('Field too long');
    return this.u8(b.length).bytes(b);
  }
  done() {
    return concat(...this.parts);
  }
}

/** Reads a binary packet. Every read throws `RangeError` past the end, so callers wrap one try/catch around a parse. */
export class Reader {
  private at = 0;
  private data: Bytes;
  private view: DataView;
  // No parameter properties in this folder: Node runs these files directly for the manual live check,
  // and its type stripping only removes syntax that has no runtime effect.
  constructor(data: Bytes) {
    this.data = data;
    this.view = new DataView(data.buffer, data.byteOffset, data.byteLength);
  }
  get left() {
    return this.data.length - this.at;
  }
  private take(n: number) {
    if (n < 0 || this.left < n) throw RangeError('Truncated packet');
    const at = this.at;
    this.at += n;
    return at;
  }
  u8() {
    return this.view.getUint8(this.take(1));
  }
  u16() {
    return this.view.getUint16(this.take(2));
  }
  u32() {
    return this.view.getUint32(this.take(4));
  }
  u64() {
    const high = this.u32();
    const value = high * 0x100000000 + this.u32();
    if (!Number.isSafeInteger(value)) throw RangeError('Number too large');
    return value;
  }
  bytes(n: number): Bytes {
    const at = this.take(n);
    return this.data.subarray(at, at + n) as Bytes;
  }
  short() {
    return this.bytes(this.u8());
  }
  rest() {
    return this.bytes(this.left);
  }
}
