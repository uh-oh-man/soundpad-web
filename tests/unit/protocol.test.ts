import { describe, it, expect } from 'vitest';
import {
  EMPTY,
  commandSchema,
  RateLimit,
  selectSounds,
  flattenCategories,
  isGuestAllowed,
  command,
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
    expect(parseInvite(raw)).not.toHaveProperty('privateKey');
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
  it('rejects malformed invitation material', () => expect(() => parseInvite('bad')).toThrow());
});
