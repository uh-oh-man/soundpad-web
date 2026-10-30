export type Invite = { v: 1; room: string; secret: string; publicKey: string; shortCode?: string };
export type HostIdentity = Invite & { privateKey: string };
export function encode(bytes: Uint8Array) {
  return btoa(String.fromCharCode(...bytes))
    .replaceAll('+', '-')
    .replaceAll('/', '_')
    .replace(/=+$/, '');
}
export function decode(text: string) {
  return Uint8Array.from(atob(text.replaceAll('-', '+').replaceAll('_', '/')), (c) =>
    c.charCodeAt(0),
  );
}
async function hash(bytes: Uint8Array) {
  return new Uint8Array(await crypto.subtle.digest('SHA-256', bytes as Uint8Array<ArrayBuffer>));
}
async function credentials(seed: Uint8Array) {
  const derive = (label: string) =>
    hash(new Uint8Array([...seed, ...new TextEncoder().encode(label)]));
  return {
    room: encode((await derive('soundpad-room-v2')).slice(0, 24)),
    secret: encode(await derive('soundpad-secret-v2')),
  };
}
async function fingerprint(publicKey: string) {
  return (await hash(decode(publicKey))).slice(0, 16);
}
export async function prepareInvite(identity: HostIdentity) {
  identity.shortCode ??=
    '1' +
    encode(
      new Uint8Array([
        ...decode(identity.room),
        ...decode(identity.secret),
        ...(await fingerprint(identity.publicKey)),
      ]),
    );
  return identity;
}
export async function createIdentity(): Promise<HostIdentity> {
  const pair = await crypto.subtle.generateKey({ name: 'ECDSA', namedCurve: 'P-256' }, true, [
    'sign',
    'verify',
  ]);
  const seed = crypto.getRandomValues(new Uint8Array(16));
  const derived = await credentials(seed);
  const publicKey = encode(new Uint8Array(await crypto.subtle.exportKey('spki', pair.publicKey)));
  return {
    v: 1,
    ...derived,
    shortCode: '2' + encode(new Uint8Array([...seed, ...(await fingerprint(publicKey))])),
    publicKey,
    privateKey: encode(new Uint8Array(await crypto.subtle.exportKey('pkcs8', pair.privateKey))),
  };
}
export function inviteUrl(identity: Invite) {
  if (identity.shortCode) return location.href.split('#')[0] + '#join=' + identity.shortCode;
  const { v, room, secret, publicKey } = identity;
  return (
    location.href.split('#')[0] +
    '#join=' +
    encode(new TextEncoder().encode(JSON.stringify({ v, room, secret, publicKey })))
  );
}
export async function parseInvite(raw: string): Promise<Invite> {
  if (/^2[-\w]{43}$/.test(raw)) {
    const bytes = decode(raw.slice(1));
    if (bytes.length !== 32) throw Error('Invalid invitation.');
    return {
      v: 1,
      ...(await credentials(bytes.slice(0, 16))),
      publicKey: 'fp:' + encode(bytes.slice(16)),
      shortCode: raw,
    };
  }
  if (/^1[-\w]{96}$/.test(raw)) {
    const bytes = decode(raw.slice(1));
    if (bytes.length !== 72) throw Error('Invalid invitation.');
    return {
      v: 1,
      room: encode(bytes.slice(0, 24)),
      secret: encode(bytes.slice(24, 56)),
      publicKey: 'fp:' + encode(bytes.slice(56)),
      shortCode: raw,
    };
  }
  if (raw.length > 2000) throw Error('Invalid invitation.');
  const x = JSON.parse(new TextDecoder().decode(decode(raw)));
  if (
    x.v !== 1 ||
    !/^[-\w]{32}$/.test(x.room) ||
    !/^[-\w]{43}$/.test(x.secret) ||
    !/^[-\w]{100,200}$/.test(x.publicKey)
  )
    throw Error('Invalid invitation.');
  return { v: 1, room: x.room, secret: x.secret, publicKey: x.publicKey };
}
function proof(room: string, nonce: string, host: string, guest: string) {
  return new TextEncoder().encode(JSON.stringify([1, room, nonce, host, guest]));
}
export async function sign(identity: HostIdentity, nonce: string, host: string, guest: string) {
  const key = await crypto.subtle.importKey(
    'pkcs8',
    decode(identity.privateKey),
    { name: 'ECDSA', namedCurve: 'P-256' },
    false,
    ['sign'],
  );
  return encode(
    new Uint8Array(
      await crypto.subtle.sign(
        { name: 'ECDSA', hash: 'SHA-256' },
        key,
        proof(identity.room, nonce, host, guest),
      ),
    ),
  );
}
export async function verify(
  identity: Invite,
  nonce: string,
  host: string,
  guest: string,
  signature: string,
  suppliedKey?: string,
) {
  try {
    let publicKey = identity.publicKey;
    if (publicKey.startsWith('fp:')) {
      if (
        !suppliedKey ||
        !/^[-\w]{100,200}$/.test(suppliedKey) ||
        'fp:' + encode(await fingerprint(suppliedKey)) !== publicKey
      )
        return false;
      publicKey = suppliedKey;
    }
    const key = await crypto.subtle.importKey(
      'spki',
      decode(publicKey),
      { name: 'ECDSA', namedCurve: 'P-256' },
      false,
      ['verify'],
    );
    return await crypto.subtle.verify(
      { name: 'ECDSA', hash: 'SHA-256' },
      key,
      decode(signature),
      proof(identity.room, nonce, host, guest),
    );
  } catch {
    return false;
  }
}
