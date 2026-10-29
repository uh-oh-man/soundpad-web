export type Invite = { v: 1; room: string; secret: string; publicKey: string };
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
export async function createIdentity(): Promise<HostIdentity> {
  const pair = await crypto.subtle.generateKey({ name: 'ECDSA', namedCurve: 'P-256' }, true, [
    'sign',
    'verify',
  ]);
  return {
    v: 1,
    room: encode(crypto.getRandomValues(new Uint8Array(24))),
    secret: encode(crypto.getRandomValues(new Uint8Array(32))),
    publicKey: encode(new Uint8Array(await crypto.subtle.exportKey('spki', pair.publicKey))),
    privateKey: encode(new Uint8Array(await crypto.subtle.exportKey('pkcs8', pair.privateKey))),
  };
}
export function inviteUrl(identity: Invite) {
  const { v, room, secret, publicKey } = identity;
  return (
    location.href.split('#')[0] +
    '#join=' +
    encode(new TextEncoder().encode(JSON.stringify({ v, room, secret, publicKey })))
  );
}
export function parseInvite(raw: string): Invite {
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
) {
  try {
    const key = await crypto.subtle.importKey(
      'spki',
      decode(identity.publicKey),
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
