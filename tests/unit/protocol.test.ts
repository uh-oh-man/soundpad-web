import { describe, it, expect } from 'vitest';
import {
  EMPTY,
  commandSchema,
  RateLimit,
  selectSounds,
  flattenCategories,
  isGuestAllowed,
  command,
  controlOps,
  parseSnapshot,
} from '../../web/src/protocol';
import { createIdentity, sign, verify, parseInvite, encode } from '../../web/src/network/identity';
const state = {
  ...EMPTY,
  library: {
    version: 'fixture',
    sounds: [
      { id: 'a', name: 'Laugh', icon: null },
      { id: 'b', name: 'Laugh', icon: null },
      { id: 'c', name: 'Airhorn', icon: null },
    ],
    categories: [
      {
        id: '2',
        name: 'Z first',
        icon: null,
        soundIds: ['b', 'a'],
        children: [{ id: '3', name: 'Nested', icon: null, soundIds: ['c'], children: [] }],
      },
      { id: '4', name: 'A last', icon: null, soundIds: [], children: [] },
    ],
  },
};
describe('library views', () => {
  it('preserves nesting and exact order', () => {
    expect(
      flattenCategories(state.library.categories).map((c) => [c.category.name, c.depth]),
    ).toEqual([
      ['Z first', 0],
      ['Nested', 1],
      ['A last', 0],
    ]);
  });
  it('uses category membership order with duplicate names', () => {
    expect(selectSounds(state, '2', '').map((s) => s.id)).toEqual(['b', 'a']);
  });
  it('keeps empty categories empty', () => expect(selectSounds(state, '4', '')).toEqual([]));
  it('searches across categories without altering them', () => {
    expect(selectSounds(state, '4', 'laugh').map((s) => s.id)).toEqual(['a', 'b']);
    expect(state.library.categories[1].soundIds).toEqual([]);
  });
  it('searches thousands of sounds', () => {
    const big = {
      ...state,
      library: {
        ...state.library,
        sounds: Array.from({ length: 10000 }, (_, i) => ({
          id: String(i),
          name: `Sound ${i}`,
          icon: null,
        })),
      },
    };
    expect(selectSounds(big, 'all', 'Sound 9999')).toHaveLength(1);
  });
});
describe('validation and access', () => {
  it('rejects raw commands and unexpected properties', () => {
    expect(commandSchema.safeParse({ ...command('play'), op: 'shell' }).success).toBe(false);
    expect(commandSchema.safeParse({ ...command('play'), raw: 'DoStartRecording()' }).success).toBe(
      false,
    );
  });
  it('restricts guest operations independently of UI', () => {
    expect(isGuestAllowed(command('reset'), state)).toBe(false);
    expect(isGuestAllowed(command('counter', { delta: 1 }), state)).toBe(false);
    expect(isGuestAllowed(command('play'), state)).toBe(true);
    expect(
      isGuestAllowed(command('stop'), {
        ...state,
        permissions: { ...state.permissions, remotePlayback: false },
      }),
    ).toBe(false);
  });
  it('knows Panic, autoplay and the suspended flag, and works without them (2.35)', () => {
    expect(commandSchema.safeParse(command('panic', { action: 'enable' })).success).toBe(true);
    expect(commandSchema.safeParse(command('panic', { action: 'disable' })).success).toBe(true);
    for (const op of ['trimAudio', 'mimicClip', 'mimicLibraryImport'] as const)
      expect(commandSchema.safeParse(command(op, { autoplay: true })).success).toBe(true);
    expect(commandSchema.safeParse({ ...command('trimAudio'), autoplay: 'yes' }).success).toBe(
      false,
    );
    // Panic is the host's alone, and "make and play" needs what playing needs.
    const noPlayback = {
      ...state,
      permissions: { ...state.permissions, remotePlayback: false },
    };
    expect(isGuestAllowed(command('panic', { action: 'enable' }), state)).toBe(false);
    expect(isGuestAllowed(command('panic', { action: 'disable' }), state)).toBe(false);
    expect(isGuestAllowed(command('trimAudio', { autoplay: true }), state)).toBe(true);
    expect(isGuestAllowed(command('trimAudio', { autoplay: true }), noPlayback)).toBe(false);
    expect(isGuestAllowed(command('trimAudio'), noPlayback)).toBe(true);
    expect(isGuestAllowed(command('mimicClip', { autoplay: true }), state)).toBe(false);
    // The flag is optional: companions before 2.35 send none, and a restart clears it.
    expect(parseSnapshot(EMPTY).playbackSuspended).toBeUndefined();
    expect(parseSnapshot({ ...EMPTY, playbackSuspended: true }).playbackSuspended).toBe(true);
    expect(parseSnapshot({ ...EMPTY, playbackSuspended: false }).playbackSuspended).toBe(false);
    expect(() => parseSnapshot({ ...EMPTY, playbackSuspended: 'yes' })).toThrow();
    // The commands that get their own budget and never wait behind slow work.
    expect([...controlOps].sort()).toEqual(
      [
        'audioPlay',
        'panic',
        'pause',
        'play',
        'region',
        'reverse',
        'seek',
        'startAt',
        'stop',
      ].sort(),
    );
  });
  it('allows bursts but bounds flooding', () => {
    const r = new RateLimit(3, 1000);
    expect([r.accept(1), r.accept(2), r.accept(3), r.accept(4), r.accept(1002)]).toEqual([
      true,
      true,
      true,
      false,
      true,
    ]);
  });
});
describe('host authentication', () => {
  it('creates independent cryptographic sessions and sanitized invites', async () => {
    const a = await createIdentity(),
      b = await createIdentity();
    expect(a.room).not.toBe(b.room);
    expect(a.secret).not.toBe(b.secret);
    const raw = encode(new TextEncoder().encode(JSON.stringify(a)));
    expect(await parseInvite(raw)).not.toHaveProperty('privateKey');
  });
  it('uses a 44 character secure invite token and rejects an unpinned host key', async () => {
    const host = await createIdentity(),
      evil = await createIdentity();
    expect(host.shortCode).toHaveLength(44);
    const guest = await parseInvite(host.shortCode!);
    expect(guest.room).toBe(host.room);
    expect(guest.secret).toBe(host.secret);
    const signature = await sign(host, 'nonce', 'host', 'guest');
    expect(await verify(guest, 'nonce', 'host', 'guest', signature, host.publicKey)).toBe(true);
    expect(
      await verify(
        guest,
        'nonce',
        'host',
        'guest',
        await sign(evil, 'nonce', 'host', 'guest'),
        evil.publicKey,
      ),
    ).toBe(false);
    expect(await verify(guest, 'nonce', 'host', 'guest', signature)).toBe(false);
  });
  it('binds signatures to the room, challenge, host and guest', async () => {
    const a = await createIdentity();
    const signature = await sign(a, 'nonce', 'host', 'guest');
    expect(await verify(a, 'nonce', 'host', 'guest', signature)).toBe(true);
    expect(await verify(a, 'nonce2', 'host', 'guest', signature)).toBe(false);
    expect(await verify(a, 'nonce', 'host', 'other-guest', signature)).toBe(false);
    expect(await verify({ ...a, room: 'other' }, 'nonce', 'host', 'guest', signature)).toBe(false);
  });
  it('rejects a guest impersonating the host with another key', async () => {
    const a = await createIdentity(),
      evil = await createIdentity();
    expect(
      await verify(a, 'nonce', 'evil', 'guest', await sign(evil, 'nonce', 'evil', 'guest')),
    ).toBe(false);
  });
  it('rejects malformed invitation material', async () => {
    await expect(parseInvite('bad')).rejects.toThrow();
  });
});
