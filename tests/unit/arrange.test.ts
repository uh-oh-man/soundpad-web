import { describe, it, expect } from 'vitest';
import { arrangeSounds, boardTitle, type BoardSelection } from '../../web/src/arrange';
import { companionAtLeast, supports } from '../../web/src/version';
import { blankWorkspace, type Workspace } from '../../web/src/workspace';
import { EMPTY, type Snapshot, type Sound } from '../../web/src/protocol';
const host = 'a'.repeat(64);
const id = (c: string) => c.repeat(64);
const sound = (c: string, name: string, extra: Partial<Sound> = {}) =>
  ({ id: id(c), name, icon: null, ...extra }) as Sound;
const sounds = [
  sound('1', 'Airhorn', { durationMs: 3000, nativePlays: 4 }),
  sound('2', 'bonk', { durationMs: 1000, nativePlays: 9 }),
  sound('3', 'Crickets', { durationMs: 2000, nativePlays: 1 }),
  sound('4', 'Airhorn reversed', { internalVariant: true }),
];
const state: Snapshot = {
  ...EMPTY,
  library: {
    version: 'test',
    sounds,
    categories: [
      {
        id: 'memes',
        name: 'Memes',
        icon: null,
        soundIds: [id('3'), id('1')],
        children: [],
      } as unknown as Snapshot['library']['categories'][number],
    ],
  },
};
const all: BoardSelection = { category: 'all', myCategory: 'all', search: '' };
const names = (list: Sound[]) => list.map((s) => s.name);
const mine = (patch: Partial<Workspace> = {}): Workspace => ({
  ...blankWorkspace(host),
  layout: 'mine',
  ...patch,
});
describe('board arrangement', () => {
  it('shows the host library without internal variants', () => {
    expect(names(arrangeSounds(state, null, all, {}))).toEqual(['Airhorn', 'bonk', 'Crickets']);
    expect(names(arrangeSounds(state, null, { ...all, category: 'memes' }, {}))).toEqual([
      'Crickets',
      'Airhorn',
    ]);
  });
  it('sorts by each key in both directions', () => {
    const w = (sort: Workspace['sort'], descending = false) => ({
      ...blankWorkspace(host),
      sort,
      descending,
    });
    expect(names(arrangeSounds(state, w('name'), all, {}))).toEqual([
      'Airhorn',
      'bonk',
      'Crickets',
    ]);
    expect(names(arrangeSounds(state, w('duration', true), all, {}))).toEqual([
      'Airhorn',
      'Crickets',
      'bonk',
    ]);
    expect(names(arrangeSounds(state, w('native'), all, {}))[0]).toBe('Crickets');
    const totals = { [id('3')]: { total: 7, lastPlayedAt: 5 } };
    expect(names(arrangeSounds(state, w('web', true), all, totals))[0]).toBe('Crickets');
  });
  it('applies personal folders, favorites, hidden sounds and custom order', () => {
    const folder = crypto.randomUUID();
    const w = mine({
      folders: [{ id: folder, name: 'Loud', parent: null, soundIds: [id('2'), id('1')] }],
      favorites: [id('3')],
      hidden: [id('2')],
      order: [id('3'), id('1')],
    });
    expect(names(arrangeSounds(state, w, all, {}))).toEqual(['Crickets', 'Airhorn']);
    expect(names(arrangeSounds(state, w, { ...all, myCategory: 'favorites' }, {}))).toEqual([
      'Crickets',
    ]);
    expect(names(arrangeSounds(state, w, { ...all, myCategory: folder }, {}))).toEqual(['Airhorn']);
    expect(boardTitle(state, w, { ...all, myCategory: folder })).toBe('Loud');
  });
  it('searches every personal sound case-insensitively, ignoring surrounding spaces', () => {
    const w = mine({ favorites: [id('3')] });
    const selection = { ...all, myCategory: 'favorites', search: '  AIR ' };
    expect(names(arrangeSounds(state, w, selection, {}))).toEqual(['Airhorn']);
    expect(boardTitle(state, w, selection)).toBe('Search results');
  });
  it('titles host categories and falls back for unknown ones', () => {
    expect(boardTitle(state, null, { ...all, category: 'memes' })).toBe('Memes');
    expect(boardTitle(state, null, { ...all, category: 'missing' })).toBe('All sounds');
  });
});
describe('companion version gate', () => {
  it('compares major and minor numerically', () => {
    expect(companionAtLeast('2.3.0', 2, 3)).toBe(true);
    expect(companionAtLeast('2.10', 2, 3)).toBe(true);
    expect(companionAtLeast('3.0', 2, 3)).toBe(true);
    expect(companionAtLeast('2.2.9', 2, 3)).toBe(false);
    expect(companionAtLeast(null, 2, 3)).toBe(false);
    expect(companionAtLeast('dev', 2, 3)).toBe(false);
  });
});

describe('feature gates for older companions', () => {
  it('hides each feature until the companion that added it', () => {
    expect(supports('2.3.1', 'mimic')).toBe(false);
    expect(supports('2.4.0', 'mimic')).toBe(true);
    expect(supports('2.4.0', 'transcripts')).toBe(false);
    expect(supports('2.6.0', 'hotkey')).toBe(false);
    expect(supports('2.8.1', 'trim')).toBe(true);
    expect(supports('2.8.1', 'effects')).toBe(false);
    expect(supports('2.10.1', 'effects')).toBe(true);
    expect(supports('2.10.4', 'hotkeyLength')).toBe(false);
    expect(supports('2.11.0', 'hotkeyLength')).toBe(true);
    expect(supports('2.13.5', 'hotkeyKey')).toBe(false);
    expect(supports('2.14.0', 'hotkeyKey')).toBe(true);
  });
});
